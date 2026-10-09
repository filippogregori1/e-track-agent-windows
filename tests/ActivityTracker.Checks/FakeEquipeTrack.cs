using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ActivityTracker.Core;
using static ActivityTracker.Checks.H;

namespace ActivityTracker.Checks;

// IL GIRO INTERO CONTRO UN SERVER FINTO.
//
// Un piccolo server HTTP su 127.0.0.1 (porta a caso) che fa la parte di equipe-track: il gate di perimetro
// (cookie `et_gate`), il token `Bearer` e le tre rotte dell'agente, con le regole del contratto riscritte qui in
// piccolo da `src/lib/domain/agente.ts` e `src/app/api/agente/*/route.ts`. L'agente ci parla con il suo client
// vero (HttpClient, intestazioni, JSON): se il giro passa qui, il giro vero cambia solo l'indirizzo.

/// <summary>Il server finto. Lo stato si legge e si cambia dal thread delle verifiche; le richieste arrivano su un altro.</summary>
public sealed class FakeEquipeTrack : IDisposable
{
    public sealed class Sessione
    {
        public required string Id;
        public required string Giorno;
        public required DateTimeOffset Inizio;
        public DateTimeOffset? Fine;
        public bool InPausa;
    }

    public sealed record Richiesta(string Metodo, string Percorso, Dictionary<string, string> Intestazioni, byte[] Corpo);

    private const double Tolleranza = 120;
    private const double Sovrapposizione = 2;

    public string Token { get; }
    public string GateSecret { get; }
    public int Port { get; private set; }

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private readonly List<Sessione> _sessioni = [];
    private readonly Dictionary<string, List<JsonObject>> _giornate = [];
    private readonly List<Richiesta> _richieste = [];

    public FakeEquipeTrack(string token, string gateSecret)
    {
        Token = token;
        GateSecret = gateSecret;
    }

    public bool Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
        return Port != 0;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    // Stato (dal thread delle verifiche)

    public void ApriSessione(string id, DateTimeOffset inizio)
    {
        lock (_lock) _sessioni.Add(new Sessione { Id = id, Giorno = RomeClock.DayString(inizio), Inizio = inizio });
    }

    public void Pausa(bool inPausa)
    {
        lock (_lock)
        {
            var s = _sessioni.LastOrDefault(x => x.Fine is null);
            if (s is not null) s.InPausa = inPausa;
        }
    }

    /// <summary>Chiusura d'ufficio con un istante nel passato (come la mezzanotte UTC o le otto ore).</summary>
    public void ChiudiSessione(string id, DateTimeOffset alle)
    {
        lock (_lock)
        {
            var s = _sessioni.FirstOrDefault(x => x.Id == id);
            if (s is not null) s.Fine = alle;
        }
    }

    public Sessione? GetSessione(string id)
    {
        lock (_lock) return _sessioni.FirstOrDefault(x => x.Id == id);
    }

    /// <summary>Tutti i tratti salvati, di tutte le giornate.</summary>
    public List<JsonObject> TrattiSalvati()
    {
        lock (_lock) return _giornate.OrderBy(kv => kv.Key, StringComparer.Ordinal).SelectMany(kv => kv.Value).ToList();
    }

    public List<Richiesta> RichiesteRicevute()
    {
        lock (_lock) return _richieste.ToList();
    }

    // HTTP

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new List<byte>();
            var chunk = new byte[65536];
            Richiesta? request = null;
            while (request is null)
            {
                int n;
                try { n = await stream.ReadAsync(chunk); } catch (Exception) { return; }
                if (n == 0) return;
                buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                request = Parse(buffer.ToArray());
            }
            var response = Handle(request);
            try { await stream.WriteAsync(response); } catch (Exception) { }
        }
    }

    private static Richiesta? Parse(byte[] buffer)
    {
        var text = Encoding.Latin1.GetString(buffer);
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (end < 0) return null;
        var lines = text[..end].Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var i = line.IndexOf(':');
            if (i > 0) headers[line[..i].Trim().ToLowerInvariant()] = line[(i + 1)..].Trim();
        }
        var length = int.TryParse(headers.GetValueOrDefault("content-length"), out var l) ? l : 0;
        var bodyStart = end + 4;
        if (buffer.Length - bodyStart < length) return null;
        return new Richiesta(first[0], first[1], headers, buffer.AsSpan(bodyStart, length).ToArray());
    }

    private static byte[] Response(int status, object? json = null, string text = "", Dictionary<string, string>? headers = null)
    {
        var body = json is null ? Encoding.UTF8.GetBytes(text) : JsonSerializer.SerializeToUtf8Bytes(json);
        var head = new StringBuilder();
        head.Append($"HTTP/1.1 {status} X\r\n");
        head.Append($"Content-Type: {(json is null ? "text/plain; charset=utf-8" : "application/json")}\r\n");
        head.Append($"Content-Length: {body.Length}\r\nConnection: close\r\n");
        foreach (var (k, v) in headers ?? []) head.Append($"{k}: {v}\r\n");
        head.Append("\r\n");
        return [.. Encoding.ASCII.GetBytes(head.ToString()), .. body];
    }

    private byte[] Handle(Richiesta r)
    {
        lock (_lock)
        {
            _richieste.Add(r);

            // 1. Il gate di perimetro: senza il cookie giusto le API rispondono 401 in testo semplice.
            var cookies = r.Intestazioni.GetValueOrDefault("cookie", "").Split(';').Select(c => c.Trim());
            var gate = cookies.FirstOrDefault(c => c.StartsWith("et_gate=", StringComparison.Ordinal)) is { } c1
                ? Uri.UnescapeDataString(c1["et_gate=".Length..]) : null;
            if (gate != GateSecret) return Response(401, text: "Accesso negato");

            // 2. Il token: stessa risposta per assente, sbagliato o revocato.
            var auth = r.Intestazioni.GetValueOrDefault("authorization", "");
            var given = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..].Trim() : "";
            if (!SyncClient.IsWellFormedToken(given) || given != Token)
                return Response(401, new { ok = false, errore = "token mancante, revocato o non valido" }, headers: new() { ["WWW-Authenticate"] = "Bearer" });

            var now = DateTimeOffset.UtcNow;
            switch (r.Metodo, r.Percorso)
            {
                case ("GET", "/api/agente/sessione"):
                {
                    var aperta = _sessioni.LastOrDefault(s => s.Fine is null);
                    object? sessione = aperta is null ? null
                        : new { id = aperta.Id, giorno = aperta.Giorno, tipo = "ordinaria", inizio = SyncJson.Instant(aperta.Inizio), inPausa = aperta.InPausa };
                    return Response(200, new { ok = true, traccia = aperta is not null && !aperta.InPausa, sessione, adesso = SyncJson.Instant(now) });
                }
                case ("POST", "/api/agente/stop"):
                {
                    var alle = now;
                    if (r.Corpo.Length > 0)
                    {
                        JsonNode? corpo;
                        try { corpo = JsonNode.Parse(r.Corpo); } catch (Exception) { return Response(400, new { ok = false, errore = "corpo non JSON" }); }
                        if (corpo?["alle"] is JsonValue v && v.TryGetValue<string>(out var s))
                        {
                            if (!SyncChecks.IstanteUtc.IsMatch(s) || SyncJson.ParseInstant(s) is not { } t)
                                return Response(422, new { ok = false, errore = "alle non valido: istante UTC con «Z»" });
                            alle = t < now ? t : now;
                        }
                    }
                    var aperta = _sessioni.LastOrDefault(x => x.Fine is null);
                    if (aperta is null || alle < aperta.Inizio) return Response(200, new { ok = true, chiusa = false });
                    aperta.Fine = alle;
                    return Response(200, new { ok = true, chiusa = true });
                }
                case ("POST", "/api/agente/segmenti"):
                {
                    JsonObject? corpo;
                    try { corpo = JsonNode.Parse(r.Corpo) as JsonObject; } catch (Exception) { corpo = null; }
                    if (corpo is null) return Response(400, new { ok = false, errore = "corpo non JSON" });
                    var (errore, indice, giorno, segmenti) = Verifica(corpo, now);
                    if (errore is not null)
                        return Response(422, indice is { } i ? new { ok = false, errore, indice = i } : new { ok = false, errore });
                    var rimossi = _giornate.TryGetValue(giorno!, out var old) ? old.Count : 0;
                    _giornate[giorno!] = segmenti!;
                    return Response(200, new { ok = true, giorno, scritti = segmenti!.Count, rimossi });
                }
                default:
                    return Response(404, new { ok = false, errore = "rotta sconosciuta" });
            }
        }
    }

    // Le regole dell'invio (leggiInvio + verificaControSessioni, in piccolo)

    private static string? MotivoNomeRifiutato(string nome) => PrivacyFilter.RejectReason(nome);

    private (string? Errore, int? Indice, string? Giorno, List<JsonObject>? Segmenti) Verifica(JsonObject corpo, DateTimeOffset now)
    {
        if (corpo["giorno"] is not JsonValue gv || !gv.TryGetValue<string>(out var giorno) || !System.Text.RegularExpressions.Regex.IsMatch(giorno, @"^\d{4}-\d{2}-\d{2}$"))
            return ("giorno non valido: AAAA-MM-GG", null, null, null);
        if (string.CompareOrdinal(giorno, RomeClock.DayString(now)) > 0) return ("giorno nel futuro", null, null, null);
        if (corpo["segmenti"] is not JsonArray segmenti) return ("segmenti mancanti", null, null, null);
        if (segmenti.Count > 5000) return ("troppi segmenti: al massimo 5000", null, null, null);

        var letti = new List<(string? Sessione, DateTimeOffset Inizio, DateTimeOffset Fine, int Indice)>();
        for (var i = 0; i < segmenti.Count; i++)
        {
            if (segmenti[i] is not JsonObject s) return ("segmento non valido", i, null, null);
            if (s["stato"] is not JsonValue sv || !sv.TryGetValue<string>(out var stato) || StatiAttivita.Parse(stato) is null)
                return ("stato non valido", i, null, null);
            if (s["durataSec"] is not JsonValue dv || dv.GetValueKind() != JsonValueKind.Number || !dv.TryGetValue<int>(out var durata) || durata < 1 || durata > 86_400)
                return ("durataSec non valida: intero fra 1 e 86400", i, null, null);
            if (s["inizio"] is not JsonValue iv || !iv.TryGetValue<string>(out var inizioS) || !SyncChecks.IstanteUtc.IsMatch(inizioS) || SyncJson.ParseInstant(inizioS) is not { } inizio)
                return ("inizio non valido: istante UTC con «Z»", i, null, null);
            var fine = inizio.AddSeconds(durata);
            var sessione = s["sessione"];
            var nome = s["nome"];
            if (fine > now.AddSeconds(Tolleranza)) return ("tratto nel futuro", i, null, null);

            if (stato == "fuori_sessione")
            {
                if (sessione is not null) return ("fuori_sessione non ha una sessione", i, null, null);
                if (nome is not null) return ("fuori_sessione non ha un nome", i, null, null);
                foreach (var ss in _sessioni.Where(x => x.Giorno == giorno))
                {
                    var a = ss.Inizio;
                    var b = ss.Fine ?? now;
                    if (inizio < b.AddSeconds(-Tolleranza) && fine > a.AddSeconds(Tolleranza))
                        return ("fuori_sessione si sovrappone a una sessione", i, null, null);
                }
                letti.Add((null, inizio, fine, i));
                continue;
            }
            if (sessione is not JsonValue idv || !idv.TryGetValue<string>(out var id) || id.Length == 0) return ("sessione mancante", i, null, null);
            if (nome is not JsonValue nv || !nv.TryGetValue<string>(out var nomeS)) return ("nome mancante", i, null, null);
            if (MotivoNomeRifiutato(nomeS) is { } motivo) return (motivo, i, null, null);
            var sess = _sessioni.FirstOrDefault(x => x.Id == id);
            if (sess is null) return ("sessione sconosciuta, di un altro o di un altro giorno", i, null, null);
            if (sess.Giorno != giorno) return ("la sessione non è di questa giornata", i, null, null);
            if (inizio < sess.Inizio.AddSeconds(-Tolleranza)) return ("tratto prima dell'apertura della sessione", i, null, null);
            if (fine > (sess.Fine ?? now).AddSeconds(Tolleranza))
                return (sess.Fine is not null ? "tratto dopo la chiusura della sessione" : "tratto oltre l'istante attuale", i, null, null);
            letti.Add((id, inizio, fine, i));
        }
        var ordinati = letti.OrderBy(x => x.Inizio).ToList();
        for (var k = 1; k < ordinati.Count; k++)
            if (ordinati[k].Inizio < ordinati[k - 1].Fine.AddSeconds(-Sovrapposizione))
                return ("tratti sovrapposti", ordinati[k].Indice, null, null);
        return (null, null, giorno, segmenti.OfType<JsonObject>().Select(x => (JsonObject)x.DeepClone()).ToList());
    }

    // Il giro

    /// <summary>Le tre chiamate dell'agente contro il server finto, col client, il registro e il costruttore veri.</summary>
    public static void RunChecks()
    {
        Section("equipe-track: il giro intero contro un server finto (HTTP vero su 127.0.0.1)");
        var token = "tok_" + Guid.NewGuid().ToString("N");
        const string secret = "segreto di prova; con caratteri=strani";
        using var server = new FakeEquipeTrack(token, secret);
        if (!server.Start())
        {
            Check("server finto in ascolto su 127.0.0.1", false, "impossibile aprire la porta");
            return;
        }
        Check($"server finto in ascolto su 127.0.0.1:{server.Port}", true);
        if (SyncClient.Validate($"http://127.0.0.1:{server.Port}", true).Url is not { } baseUrl)
        {
            Check("URL del server finto valido", false);
            return;
        }
        var client = new SyncClient(baseUrl, token, secret, "checks");

        SyncFault? Failure<T>(Func<Task<T>> op)
        {
            try { op().GetAwaiter().GetResult(); return null; }
            catch (SyncException e) { return e.Fault; }
        }

        try
        {
            // Perimetro e token.
            var wrongGate = new SyncClient(baseUrl, token, "sbagliato");
            var noGate = new SyncClient(baseUrl, token, null);
            var wrongToken = new SyncClient(baseUrl, "tok_" + new string('z', 30), secret);
            Check("segreto del gate sbagliato → rifiuto del gate", Failure(() => wrongGate.SessioneAsync())?.Kind == SyncErrorKind.Gate);
            Check("senza cookie del gate → rifiuto del gate", Failure(() => noGate.SessioneAsync())?.Kind == SyncErrorKind.Gate);
            Check("token sbagliato → token rifiutato", Failure(() => wrongToken.SessioneAsync())?.Kind == SyncErrorKind.Unauthorized);

            // Letture della sessione. Le chiamate sono vere e fatte adesso; gli istanti registrati sono quelli di
            // 20 minuti simulati che finiscono adesso, così i segmenti sintetici cadono dove serve.
            var store = Store.Temporary();
            var sync = new TrackingSync(store);
            var T = DateTimeOffset.UtcNow;
            ServerState Poll(double offset)
            {
                var r = client.SessioneAsync().GetAwaiter().GetResult().Risposta;
                return sync.Record(ServerState.From(r), T.AddSeconds(offset)).Current;
            }
            var primo = Poll(-1200);
            Poll(-1100);
            Check("GET /sessione senza sessione aperta → non traccia", primo == ServerState.None);
            const string sid = "sess_finta_1";
            server.ApriSessione(sid, T.AddSeconds(-1000));
            var giorno = RomeClock.DayString(T.AddSeconds(-1000));
            var aperti = new[] { -990.0, -870, -750, -630, -510 }.Select(Poll).ToList();
            Check("sessione aperta → traccia, con id e giorno della sessione", aperti.All(x => x == ServerState.Tracking(sid, giorno)));
            server.Pausa(true);
            var inPausa = new[] { Poll(-450), Poll(-330) };
            Check("sessione in pausa → non traccia", inPausa.All(x => x == ServerState.Paused(sid, giorno)));
            server.Pausa(false);
            foreach (var o in new[] { -300.0, -180, -60 }) Poll(o);
            var (risposta, sentAt, receivedAt) = client.SessioneAsync().GetAwaiter().GetResult();
            sync.Apply(risposta, sentAt, receivedAt);
            Check("ripresa → traccia di nuovo", sync.CurrentState(DateTimeOffset.UtcNow) == ServerState.Tracking(sid, giorno));
            Check("registro: nessuna sessione, sessione, pausa, sessione", sync.Ledger.Intervals.Select(x => x.Kind).SequenceEqual(["none", "tracking", "paused", "tracking"]),
                  Join(sync.Ledger.Intervals.Select(x => x.Kind)));
            Check("scarto d'orologio dal campo «adesso» (stesso PC: 0 s)", Math.Abs(sync.ClockOffset) <= 1, $"{sync.ClockOffset}");
            Check("cancello: due finestre di contenuto (le due parti attive della sessione)", sync.ContentWindows(DateTimeOffset.UtcNow).Count == 2);

            // I segmenti misurati di quei 20 minuti.
            var s = Epoch.Seconds(T);
            var segments = new List<Segment>
            {
                new(giorno, s - 1200, s - 1050, ActivityState.Active, "chrome.exe", "Google Chrome", "www.youtube.com", "Video segreto - YouTube"),
                new(giorno, s - 1050, s - 950, ActivityState.Active, "figma.exe", "Figma", titleHint: "Progetto riservato"),
                new(giorno, s - 950, s - 700, ActivityState.Active, "chrome.exe", "Google Chrome", "www.youtube.com", "Video segreto - YouTube"),
                new(giorno, s - 700, s - 500, ActivityState.Passive, "chrome.exe", "Google Chrome", "www.youtube.com"),
                new(giorno, s - 500, s - 400, ActivityState.Unused),
                new(giorno, s - 400, s - 200, ActivityState.Active, "winword.exe", "Microsoft Word", titleHint: "Documento riservato.docx"),
                new(giorno, s - 200, s - 100, ActivityState.Off),
                new(giorno, s - 100, s, ActivityState.Unused),
            };
            var resolver = new ActivityResolver(ActivityRule.Seed);
            var prepared = sync.PendingInvii(segments, [], resolver);
            Check("invii preparati per la giornata", prepared.Any(p => p.Giorno == giorno), Join(prepared.Select(p => p.Giorno)));
            var outcomes = new List<TransmitOutcome>();
            foreach (var p in prepared)
            {
                var o = TrackingSync.TransmitAsync(p, client, sync.ClockOffset).GetAwaiter().GetResult();
                sync.Finish(p, o, DateTimeOffset.UtcNow);
                outcomes.Add(o);
            }
            Check("POST /segmenti accettato dal server finto", outcomes.All(o => o.Kind == TransmitKind.Sent && o.Dropped == 0),
                  Join(outcomes.Select(o => $"{o.Kind}:{o.Message}")));

            var salvati = server.TrattiSalvati();
            int Secondi(string stato) => salvati.Where(x => (string?)x["stato"] == stato).Sum(x => (int)x["durataSec"]!);
            Check("attivo: Figma 40 + YouTube 250 + Word 100 = 390 s", Secondi("attivo") == 390, $"{Secondi("attivo")}");
            Check("passivo: YouTube fino alla pausa = 190 s (YouTube passivo resta passivo)", Secondi("passivo") == 190, $"{Secondi("passivo")}");
            Check("senza_utilizzo: solo l'ultimo tratto in sessione = 100 s (pausa esclusa)", Secondi("senza_utilizzo") == 100, $"{Secondi("senza_utilizzo")}");
            Check("fuori_sessione: solo la durata, prima della sessione = 100 s", Secondi("fuori_sessione") == 100, $"{Secondi("fuori_sessione")}");
            var nomi = salvati.Select(x => (string?)x["nome"]).OfType<string>().ToHashSet();
            Check("nomi: solo quelli aggregati", nomi.SetEquals(["Figma", "YouTube", "Microsoft Word", SyncPayloadBuilder.UnusedName]), Join(nomi));
            var post = server.RichiesteRicevute().Where(x => x.Percorso == "/api/agente/segmenti").ToList();
            var corpi = string.Concat(post.Select(x => Encoding.UTF8.GetString(x.Corpo))).ToLowerInvariant();
            foreach (var vietato in new[] { "youtube.com", "segreto", "riservato", ".docx", "chrome.exe", "figma.exe", "winword", "google chrome", "http" })
                Check($"nulla di «{vietato}» nei corpi inviati", !corpi.Contains(vietato));
            Check("ogni richiesta porta Bearer e cookie del gate",
                  server.RichiesteRicevute().Count(x => x.Intestazioni.GetValueOrDefault("authorization") == "Bearer " + token) >= post.Count
                  && post.All(x => x.Intestazioni.GetValueOrDefault("cookie", "").StartsWith("et_gate=", StringComparison.Ordinal)
                                   && x.Intestazioni.GetValueOrDefault("x-agente-versione") == "checks"));
            CheckT("niente di cambiato → niente da inviare", () => sync.PendingInvii(segments, [], resolver).Count == 0);

            // Chiusura d'ufficio anteriore alle letture (otto ore, mezzanotte UTC): il server rifiuta il tratto dopo
            // la chiusura, l'agente lo toglie, ritenta e accorcia il registro.
            server.ChiudiSessione(sid, T.AddSeconds(-250));
            store.ClearSyncDays();
            var dopo = new List<TransmitOutcome>();
            foreach (var p in sync.PendingInvii(segments, [], resolver))
            {
                var o = TrackingSync.TransmitAsync(p, client, sync.ClockOffset).GetAwaiter().GetResult();
                sync.Finish(p, o, DateTimeOffset.UtcNow);
                dopo.Add(o);
            }
            Check("422 «dopo la chiusura» → tratto tolto, reinvio accettato",
                  dopo.Any(o => o.Kind == TransmitKind.Sent && o.Dropped == 1 && o.Truncations.FirstOrDefault()?.Session == sid),
                  Join(dopo.Select(o => $"{o.Kind}:{o.Dropped}:{o.Message}")));
            Check("il nuovo invio sostituisce la giornata (rimossi = scritti prima)",
                  dopo.Any(o => o.Response is { } r && r.Giorno == giorno && r.Rimossi == r.Scritti + 1));
            Check("sul server non resta il tratto dopo la chiusura", !server.TrattiSalvati().Any(x => (string?)x["stato"] == "senza_utilizzo"));
            Check("registro accorciato all'inizio del tratto respinto",
                  sync.Ledger.Intervals.Where(x => x.SessionId == sid && x.Kind == "tracking").All(x => x.End <= s - 100 + 0.001));

            // Il filtro del server: un nome con la forma di un indirizzo fa respingere l'invio (e l'agente non lo manda mai).
            var urlish = new InvioSegmenti(giorno, [new SegmentoInvio(sid, "www.youtube.com/watch?v=x", 10, StatoAttivita.Attivo, SyncJson.Instant(T.AddSeconds(-900)))]);
            Check("nome con «/» → 422 con indice 0", Failure(() => client.InviaSegmentiAsync(urlish))
                  == new SyncFault(SyncErrorKind.Unprocessable, "il nome contiene «/»: solo nomi aggregati, mai indirizzi", 0));

            // Stop.
            server.ApriSessione("sess_finta_2", DateTimeOffset.UtcNow.AddSeconds(-60));
            var chiusa = client.StopAsync(null).GetAwaiter().GetResult();
            Check("POST /stop con una sessione aperta → chiusa", chiusa.Chiusa && server.GetSessione("sess_finta_2")?.Fine is not null);
            var ancora = client.StopAsync(null).GetAwaiter().GetResult();
            Check("POST /stop senza sessione aperta → niente da chiudere", !ancora.Chiusa);
            var dopoStop = client.SessioneAsync().GetAwaiter().GetResult().Risposta;
            Check("dopo lo stop GET /sessione dice: non tracciare", !dopoStop.Traccia && dopoStop.Sessione is null);
            store.Dispose();
            CoreChecks.Cleanup(store.Path);
        }
        catch (Exception e)
        {
            Check("giro col server finto senza errori", false, e.ToString());
        }
    }
}
