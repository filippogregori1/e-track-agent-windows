using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ActivityTracker.Core;
using static ActivityTracker.Checks.H;

namespace ActivityTracker.Checks;

/// <summary>Trasporto finto: registra le richieste (copiate al momento dell'invio) e risponde con risposte preparate.</summary>
public sealed class MockTransport : IHttpTransport
{
    public sealed record Sent(string Method, string Url, Dictionary<string, string> Headers, byte[] Body);
    public sealed record Canned(int Status, string Body, Exception? Error = null, Dictionary<string, string>? Headers = null);

    public List<Sent> Requests { get; } = [];
    public Queue<Canned> Responses { get; } = new();

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Requests.Add(new Sent(request.Method.Method, request.RequestUri!.ToString(), headers, body));
        var canned = Responses.Count > 0 ? Responses.Dequeue() : new Canned(500, "");
        if (canned.Error is not null) throw canned.Error;
        var response = new HttpResponseMessage((HttpStatusCode)canned.Status) { Content = new StringContent(canned.Body, Encoding.UTF8, "application/json") };
        foreach (var (k, v) in canned.Headers ?? []) response.Headers.TryAddWithoutValidation(k, v);
        return response;
    }

    public Sent? Last => Requests.Count > 0 ? Requests[^1] : null;
    public JsonElement? LastBodyJson() => Last is { Body.Length: > 0 } s ? JsonDocument.Parse(s.Body).RootElement : null;
}

public static class SyncChecks
{
    public const string Sess1 = "sess_prova_1";
    public static readonly ServerState Tracking1 = ServerState.Tracking(Sess1, "2024-03-10");
    public static readonly ServerState Paused1 = ServerState.Paused(Sess1, "2024-03-10");

    /// <summary>Registro da tuple (stato, inizio, fine) in secondi da T0.</summary>
    public static TrackingLedger LedgerFrom(params (ServerState State, double Start, double End)[] items) =>
        new(items.Select(i => LedgerInterval.Of(i.State, Epoch.Seconds(T0) + i.Start, Epoch.Seconds(T0) + i.End)));

    /// <summary>"stato:nome:inizio-fine" (secondi da T0) per confronti leggibili.</summary>
    public static List<string> Describe(IEnumerable<Tratto> tratti) =>
        tratti.Select(t => $"{t.Stato.Raw()}:{t.Nome ?? "-"}:{Epoch.Round((t.Start - T0).TotalSeconds)}-{Epoch.Round((t.End - T0).TotalSeconds)}").ToList();

    public static readonly Regex IstanteUtc = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,3})?Z$");

    /// <summary>Tutte le chiavi di un oggetto JSON, ricorsivamente.</summary>
    public static HashSet<string> JsonKeys(JsonElement e)
    {
        var keys = new HashSet<string>();
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                keys.Add(p.Name);
                keys.UnionWith(JsonKeys(p.Value));
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in e.EnumerateArray()) keys.UnionWith(JsonKeys(v));
        }
        return keys;
    }

    public static void Run()
    {
        Tratti();
        Ledger();
        Gate();
        Validation();
        Client();
        Migrations();
        VideoRule();
    }

    private static void Tratti()
    {
        Section("equipe-track: tratti dal registro delle sessioni");
        var builder = new SyncPayloadBuilder(new ActivityResolver(ActivityRule.Seed), RomeClock);
        var s = Epoch.Seconds(T0);
        var segments = new List<Segment>
        {
            new(Today, s, s + 600, ActivityState.Active, "chrome.exe", "Google Chrome", "www.youtube.com"),
            new(Today, s + 600, s + 1000.4, ActivityState.Active, "chrome.exe", "Google Chrome", "m.youtube.com"),
            new(Today, s + 1000.4, s + 1600, ActivityState.Active, "msedge.exe", "Microsoft Edge", "www.youtube.com"),
            new(Today, s + 1600, s + 2200, ActivityState.Passive, "chrome.exe", "Google Chrome", "www.youtube.com"),
            new(Today, s + 2200, s + 2500, ActivityState.Active, "winword.exe", "Microsoft Word", titleHint: "Segreto - Documento riservato"),
            new(Today, s + 2500, s + 2600, ActivityState.Active, "noname.exe"),
            new(Today, s + 2600, s + 2900, ActivityState.Unused),
            new(Today, s + 2900, s + 3200, ActivityState.Off),
            new(Today, s + 3200, s + 3500, ActivityState.Active, "figma.exe", "Figma"),
            new(Today, s + 3500, s + 3600, ActivityState.Active, "msedge.exe", "Microsoft Edge", "github.com"),
        };
        // nessuna sessione 0–600 · buco 600–700 · sessione 700–3000 · pausa 3100–3400 · sessione 3400–3600
        var ledger = LedgerFrom((ServerState.None, 0, 600), (Tracking1, 700, 3000), (Paused1, 3100, 3400), (Tracking1, 3400, 3600));
        var byDay = builder.Tratti(segments, null, ledger);
        var tratti = byDay.TryGetValue("2024-03-10", out var t) ? t : [];
        Check("una sola giornata (quella della sessione)", byDay.Keys.SequenceEqual(["2024-03-10"]), Join(byDay.Keys));
        Check("tratti attesi: fuori sessione senza nome, poi le attività della sessione",
              Describe(tratti).SequenceEqual(["fuori_sessione:-:0-600", "attivo:YouTube:700-1600", "passivo:YouTube:1600-2200",
                                              "attivo:Microsoft Word:2200-2500", "attivo:Sconosciuto:2500-2600",
                                              "senza_utilizzo:Senza utilizzo:2600-2900", "attivo:Figma:3400-3500", "attivo:GitHub:3500-3600"]),
              Join(Describe(tratti)));
        Check("buco fra letture discordi (600–700): niente", !tratti.Any(x => x.Start < T0.AddSeconds(700) && x.End > T0.AddSeconds(600)));
        Check("pausa (3100–3400) e PC spento (off): niente", !tratti.Any(x => x.Start < T0.AddSeconds(3400) && x.End > T0.AddSeconds(3000)));
        Check("fuori_sessione: né sessione né nome", tratti.Where(x => x.Stato == StatoAttivita.FuoriSessione).All(x => x.Sessione is null && x.Nome is null));
        Check("in sessione: id della sessione e un nome", tratti.Where(x => x.Stato != StatoAttivita.FuoriSessione).All(x => x.Sessione == Sess1 && x.Nome is not null));
        Check("YouTube da Chrome ed Edge, m. e www., fuso in un tratto", tratti.Count(x => x.Nome == "YouTube" && x.Stato == StatoAttivita.Attivo) == 1);

        var invio = SyncPayloadBuilder.Invio("2024-03-10", tratti);
        Check("durate in secondi interi", invio.Segmenti.Select(x => x.DurataSec).SequenceEqual([600, 900, 600, 300, 100, 300, 100, 100]), Join(invio.Segmenti.Select(x => x.DurataSec)));
        Check("inizio UTC con millisecondi e Z", invio.Segmenti[0].Inizio == "2024-03-10T10:00:00.000Z" && invio.Segmenti.All(x => IstanteUtc.IsMatch(x.Inizio)), invio.Segmenti[0].Inizio);
        var shifted = SyncPayloadBuilder.Invio("2024-03-10", tratti, 30);
        Check("scarto d'orologio applicato agli inizi (+30 s)", shifted.Segmenti[0].Inizio == "2024-03-10T10:00:30.000Z");

        var data = SyncJson.Encode(invio);
        var json = Encoding.UTF8.GetString(data);
        Check("JSON: sessione e nome null espliciti su fuori_sessione", json.Contains("\"nome\":null") && json.Contains("\"sessione\":null"));
        Check("JSON: stati del contratto", json.Contains("\"stato\":\"fuori_sessione\"") && json.Contains("\"stato\":\"senza_utilizzo\"")
              && json.Contains("\"stato\":\"passivo\"") && json.Contains("\"stato\":\"attivo\""));
        var keys = JsonKeys(JsonDocument.Parse(data).RootElement);
        Check("JSON: solo le chiavi del contratto", keys.SetEquals(["giorno", "segmenti", "sessione", "nome", "durataSec", "stato", "inizio"]), Join(keys));
        foreach (var forbidden in new[] { "Segreto", "riservato", "chrome.exe", "msedge.exe", "winword.exe", "noname.exe", "figma.exe",
                                          "m.youtube.com", "www.youtube.com", "github.com", "Google Chrome", "Microsoft Edge", "://" })
            Check($"JSON non contiene «{forbidden}»", !json.Contains(forbidden));
        var back = SyncJson.Decode<InvioSegmenti>(data);
        Check("roundtrip JSON", back.Giorno == invio.Giorno && back.Segmenti.SequenceEqual(invio.Segmenti));

        // Coda provvisoria: attivo sull'app in primo piano, solo dentro il registro.
        var live = builder.Tratti([], [new ProvisionalSlice(T0.AddSeconds(3550), T0.AddSeconds(3700), AppA, null)], ledger);
        var liveDay = live.TryGetValue("2024-03-10", out var l) ? l : [];
        Check("coda provvisoria → attivo «App A» fino all'ultima lettura (50 s)", Describe(liveDay).SequenceEqual(["attivo:App A:3550-3600"]), Join(Describe(liveDay)));

        // Mezzanotte di Roma (T0 = 11:00 a Roma): fuori sessione si divide fra le due giornate.
        var midnight = (RomeClock.NextMidnight(T0) - T0).TotalSeconds;
        var night = builder.Tratti([new Segment(Today, s + midnight - 300, s + midnight + 300, ActivityState.Active, appName: "Terminale")],
                                   null, LedgerFrom((ServerState.None, midnight - 400, midnight + 400)));
        Check("fuori sessione a cavallo della mezzanotte di Roma → 300 s per giornata",
              night.TryGetValue("2024-03-10", out var n1) && n1.Select(x => (int)x.Duration).SequenceEqual([300])
              && night.TryGetValue("2024-03-11", out var n2) && n2.Select(x => (int)x.Duration).SequenceEqual([300]),
              Join(night.Select(kv => $"{kv.Key}:{Join(kv.Value.Select(x => (int)x.Duration))}")));

        // Due tratti uguali separati da un buco non si fondono; sotto il secondo spariscono.
        var gappedAll = builder.Tratti([
            new Segment(Today, s + 800, s + 900, ActivityState.Active, appName: "Figma"),
            new Segment(Today, s + 905, s + 1000, ActivityState.Active, appName: "Figma"),
            new Segment(Today, s + 1000, s + 1000.4, ActivityState.Active, appName: "Breve"),
        ], null, ledger);
        var gapped = gappedAll.TryGetValue("2024-03-10", out var g) ? g : [];
        Check("buco di 5 s: due tratti; tratto sotto il secondo omesso", Describe(gapped).SequenceEqual(["attivo:Figma:800-900", "attivo:Figma:905-1000"]), Join(Describe(gapped)));

        // Hash
        var h1 = SyncPayloadBuilder.Hash(tratti);
        Check("hash stabile e indipendente dall'ordine", h1 == SyncPayloadBuilder.Hash(Enumerable.Reverse(tratti)) && h1.Length == 64);
        var longer = tratti.ToList();
        longer[1] = longer[1] with { End = longer[1].End.AddSeconds(1) };
        Check("un secondo in più → hash diverso", SyncPayloadBuilder.Hash(longer) != h1);
        Check("nessun tratto → hash definito e diverso", SyncPayloadBuilder.Hash([]) != h1);

        // Argine sui nomi (filtro privacy)
        var weird = new ActivityResolver([new(RuleKind.Domain, "a.test", "Docs/Note?v=1#x @casa"),
                                          new(RuleKind.Domain, "b.test", new string('x', 120)),
                                          new(RuleKind.Domain, "c.test", "Tab\tNew\nline")]);
        var wb = new SyncPayloadBuilder(weird, RomeClock);
        Check("sanitizzazione: / ? # @ → spazi", wb.ActivityName(new Subject(Domain: "a.test")) == "Docs Note v=1 x casa", wb.ActivityName(new Subject(Domain: "a.test")));
        Check("sanitizzazione: troncato a 80", wb.ActivityName(new Subject(Domain: "b.test")).Length == 80);
        Check("sanitizzazione: caratteri di controllo → spazi", wb.ActivityName(new Subject(Domain: "c.test")) == "Tab New line");
        Check("sanitizzazione: vuoto → Sconosciuto", SyncPayloadBuilder.SanitizedActivity("  /// ") == ActivityResolver.UnknownLabel);
        Check("sanitizzazione: «://» → spazio", SyncPayloadBuilder.SanitizedActivity("https://x") == "https x");
        Check("dominio senza regola → host senza www, mai l'URL", wb.ActivityName(new Subject(Domain: "www.example.org")) == "example.org");
        Check("filtro del server: nomi con / ? @ :// rifiutati",
              PrivacyFilter.RejectReason("youtube.com/watch") is not null && PrivacyFilter.RejectReason("a?b") is not null
              && PrivacyFilter.RejectReason("io@casa") is not null && PrivacyFilter.RejectReason("http://x") is not null
              && PrivacyFilter.RejectReason("YouTube") is null && PrivacyFilter.RejectReason("Senza utilizzo") is null);
    }

    private static void Ledger()
    {
        Section("equipe-track: registro delle letture");
        var l = new TrackingLedger();
        l.Record(ServerState.None, T0);
        l.Record(ServerState.None, T0.AddSeconds(60));
        l.Record(ServerState.None, T0.AddSeconds(120));
        Check("letture uguali entro 150 s → un intervallo [0, 120]", l.Intervals.Count == 1 && l.Intervals[0].End - l.Intervals[0].Start == 120);
        l.Record(ServerState.None, T0.AddSeconds(400));
        Check("buco di 280 s (PC sospeso) → intervallo nuovo", l.Intervals.Count == 2);
        l.Record(Tracking1, T0.AddSeconds(460));
        l.Record(Tracking1, T0.AddSeconds(520));
        Check("cambio di stato → intervallo nuovo", l.Intervals.Count == 3 && l.Intervals[2].State == Tracking1);
        l.Record(ServerState.None, T0.AddSeconds(500));
        Check("lettura anteriore all'ultima (orologio indietro) ignorata", l.Intervals.Count == 3 && l.Intervals[2].End == Epoch.Seconds(T0) + 520);
        Check("stato corrente entro 150 s dall'ultima lettura", l.Current(T0.AddSeconds(600)) == Tracking1);
        Check("oltre 150 s senza letture: non si sa", l.Current(T0.AddSeconds(700)) is null);
        var w = l.ContentWindows(T0.AddSeconds(530));
        Check("finestra del contenuto: la sessione, prolungata di 150 s finché è corrente",
              w.Count == 1 && w[0].Start == T0.AddSeconds(460) && w[0].End == T0.AddSeconds(670));
        var stale = l.ContentWindows(T0.AddSeconds(900));
        Check("lettura vecchia: niente prolungamento", stale.Count == 1 && stale[0].End == T0.AddSeconds(520));
        var p = l.Clone();
        p.Record(Paused1, T0.AddSeconds(580));
        Check("in pausa: nessuna finestra oltre l'ultima lettura attiva", p.ContentWindows(T0.AddSeconds(590)).Select(x => x.End).SequenceEqual([T0.AddSeconds(520)]));
        Check("in pausa: lo stato corrente dice pausa", p.Current(T0.AddSeconds(590)) == Paused1);
        var tr = LedgerFrom((Tracking1, 0, 1000), (ServerState.None, 1060, 1200), (Tracking1, 1300, 2000));
        Check("chiusura scoperta a 500: la sessione finisce lì, gli intervalli dopo spariscono",
              tr.Truncate(Sess1, T0.AddSeconds(500)) && tr.Intervals.Select(x => x.Kind).SequenceEqual(["tracking", "none"])
              && tr.Intervals[0].End == Epoch.Seconds(T0) + 500);
        Check("troncare un'altra sessione non cambia nulla", !tr.Truncate("altra", T0));
        Check("giornate: quella della sessione e quella (di Roma) degli intervalli senza sessione",
              LedgerFrom((Tracking1, 0, 10), (ServerState.None, 20, 30)).Giorni(RomeClock).SetEquals(["2024-03-10"]));
        var pr = LedgerFrom((ServerState.None, 0, 10), (ServerState.None, 1000, 2000));
        pr.Prune(T0.AddSeconds(500));
        Check("potatura: via gli intervalli finiti prima", pr.Intervals.Count == 1);
        static RispostaSessione R(bool traccia, bool? pausa) => new()
        {
            Ok = true, Traccia = traccia, Adesso = "",
            Sessione = pausa is { } ip ? new RispostaSessione.SessioneInfo { Id = "a", Giorno = "g", Tipo = "ordinaria", Inizio = "x", InPausa = ip } : null,
        };
        Check("stato dalla risposta: traccia → tracking, in pausa → paused, senza sessione → none",
              ServerState.From(R(true, false)) == ServerState.Tracking("a", "g") && ServerState.From(R(false, true)) == ServerState.Paused("a", "g")
              && ServerState.From(R(false, null)) == ServerState.None);
    }

    private static void Gate()
    {
        Section("equipe-track: cancello del contenuto");
        var sink = new RecordingSink();
        var gate = new SessionGate(sink) { Windows = [new TimeWindow(T0.AddSeconds(100), T0.AddSeconds(200))] };
        gate.Append(new ConfirmedSlice(Today, T0, T0.AddSeconds(300), ActivityState.Active, ChromeYouTube));
        Check("tratto a cavallo: diviso in tre", sink.Slices.Count == 3);
        Check("dentro la finestra il contenuto resta", sink.Slices[1].Subject == ChromeYouTube && Approx(sink.Slices[1].Duration, 100));
        Check("fuori dalla finestra solo «Non tracciato»", sink.Slices[0].Subject == SessionGate.UntrackedSubject && sink.Slices[2].Subject == SessionGate.UntrackedSubject);
        Check("stato e durata invariati", sink.Slices.All(x => x.State == ActivityState.Active) && Approx(sink.All, 300));
        gate.Append(new ConfirmedSlice(Today, T0.AddSeconds(300), T0.AddSeconds(400), ActivityState.Unused, null));
        Check("intervallo senza soggetto (unused) passa intatto", sink.Slices.Count == 4 && sink.Slices[3].Subject is null);
        gate.Windows = [];
        gate.Append(new ConfirmedSlice(Today, T0, T0.AddSeconds(50), ActivityState.Passive, ChromeYouTube));
        Check("senza finestre (nessuna sessione, agente non configurato): mai contenuto", sink.Slices[^1].Subject == SessionGate.UntrackedSubject);

        // La misura non cambia: stessi secondi per stato con e senza cancello.
        RecordingSink RunWith(Func<ISegmentSink, ISegmentSink> makeSink)
        {
            var rec = new RecordingSink();
            var engine = new ClassificationEngine(makeSink(rec), 180, Utc);
            var sim = new Simulation(engine, T0, foreground: AppA);
            sim.Run(300, true, AppA);
            sim.Run(400, false, AppA, ChromeYouTube);
            sim.Run(200, false, AppA);
            sim.Run(60, true, AppB, sessionOpen: false);
            engine.Finish(sim.Now);
            return rec;
        }
        var plain = RunWith(x => x);
        var gated = RunWith(x => new SessionGate(x) { Windows = [new TimeWindow(T0.AddSeconds(150), T0.AddSeconds(450))] });
        Check("misura invariata: stessi secondi attivi/passivi/unused/off col cancello",
              ActivityStates.All.All(st => Approx(plain.Total(st), gated.Total(st))),
              string.Join(", ", ActivityStates.All.Select(st => $"{st.Raw()} {plain.Total(st)}/{gated.Total(st)}")));
        Check("YouTube passivo resta passivo col cancello (dentro la finestra)", gated.Total(ActivityState.Passive, x => x.Subject == ChromeYouTube) > 0);
    }

    private static void Validation()
    {
        Section("equipe-track: validazione di URL, token e cookie");
        string? Ok(string u, bool flag = false) => SyncClient.Validate(u, flag) is { Url: { } url } ? SyncClient.Text(url) : null;
        SyncErrorKind? Err(string u, bool flag = false) => SyncClient.Validate(u, flag).Error?.Kind;
        Check("https ok, barra finale tolta", Ok("https://track.esempio.it/") == "https://track.esempio.it", Ok("https://track.esempio.it/") ?? "null");
        Check("https con percorso", Ok("https://esempio.it/track/") == "https://esempio.it/track");
        Check("http://localhost:3000 rifiutato senza flag", Err("http://localhost:3000") == SyncErrorKind.InsecureUrl);
        Check("http://localhost:3000 accettato con flag", Ok("http://localhost:3000", true) == "http://localhost:3000");
        Check("http://esempio.it rifiutato anche col flag", Err("http://esempio.it", true) == SyncErrorKind.InsecureUrl);
        Check("http://localhost.evil.com rifiutato anche col flag", Err("http://localhost.evil.com", true) == SyncErrorKind.InsecureUrl);
        Check("credenziali, query o frammento nell'URL rifiutati",
              Err("https://u:p@esempio.it") == SyncErrorKind.InvalidUrl && Err("https://esempio.it/?x=1") == SyncErrorKind.InvalidUrl && Err("https://esempio.it/#f") == SyncErrorKind.InvalidUrl);
        Check("stringa vuota o senza schema → URL non valido", Err("") == SyncErrorKind.InvalidUrl && Err("track.esempio.it") == SyncErrorKind.InvalidUrl);
        Check("flag da ambiente", SyncClient.EnvironmentAllowsInsecureLocalhost(new Dictionary<string, string> { ["AT_ALLOW_INSECURE_LOCALHOST"] = "1" })
              && !SyncClient.EnvironmentAllowsInsecureLocalhost(new Dictionary<string, string> { ["AT_ALLOW_INSECURE_LOCALHOST"] = "0" })
              && !SyncClient.EnvironmentAllowsInsecureLocalhost(new Dictionary<string, string>()));
        Check("token: forma del server (20–200, lettere cifre - _)",
              SyncClient.IsWellFormedToken(new string('a', 20)) && SyncClient.IsWellFormedToken("Ab_9-" + new string('x', 40))
              && !SyncClient.IsWellFormedToken("corto") && !SyncClient.IsWellFormedToken(new string('a', 30) + "!")
              && !SyncClient.IsWellFormedToken(new string('a', 201)));
        Check("cookie del gate: caratteri speciali in %XX", SyncClient.GateCookieValue("a b;c=d%") == "a%20b%3Bc%3Dd%25" && SyncClient.GateCookieValue("Abc-1_2.3~") == "Abc-1_2.3~");

        var creds = new InMemoryTokenStore();
        Check("credenziali assenti → nil", EquipeCredentials.Load(creds) is null);
        new EquipeCredentials("http://localhost:3000", "tok", "segreto").Save(creds);
        Check("credenziali: roundtrip delle tre voci", EquipeCredentials.Load(creds) == new EquipeCredentials("http://localhost:3000", "tok", "segreto"));
        new EquipeCredentials("http://localhost:3000", "tok", "").Save(creds);
        Check("segreto vuoto → voce tolta", creds.Token(EquipeCredentials.Account.GateSecret) is null && creds.Count == 2);
        EquipeCredentials.Delete(creds);
        Check("scollega: nessuna voce resta", creds.Count == 0);
        Check("le credenziali non si stampano mai", !new EquipeCredentials("https://x.it", "tok_segretissimo_123456789", "pw").ToString().Contains("segretissimo"));
    }

    private static void Client()
    {
        Section("equipe-track: client con trasporto finto");
        var transport = new MockTransport();
        var token = "tok_" + new string('A', 32);
        var client = new SyncClient(new Uri("https://track.esempio.it"), token, "s3greto;x", "9.9.9", transport);

        transport.Responses.Enqueue(new(200, """{"ok":true,"traccia":true,"sessione":{"id":"s1","giorno":"2024-03-10","tipo":"ordinaria","inizio":"2024-03-10T08:00:00.000Z","inPausa":false},"adesso":"2024-03-10T10:00:00.000Z"}"""));
        var (r, _, _) = client.SessioneAsync().GetAwaiter().GetResult();
        var req = transport.Last!;
        Check("sessione: GET /api/agente/sessione", req.Method == "GET" && req.Url == "https://track.esempio.it/api/agente/sessione", req.Url);
        Check("Authorization: Bearer <token>", req.Headers.GetValueOrDefault("Authorization") == "Bearer " + token);
        Check("Cookie: et_gate=<segreto codificato>", req.Headers.GetValueOrDefault("Cookie") == "et_gate=s3greto%3Bx");
        Check("versione dell'agente in X-Agente-Versione", req.Headers.GetValueOrDefault("X-Agente-Versione") == "9.9.9");
        Check("sessione: risposta decodificata", r.Traccia && r.Sessione?.Id == "s1" && r.Sessione?.Giorno == "2024-03-10" && r.Sessione?.InPausa == false);
        Check("versione predefinita: quella del Mac più la piattaforma", AppIdentity.AgentVersion == "0.3.0-windows");

        var invio = new InvioSegmenti("2024-03-10", [new SegmentoInvio("s1", "YouTube", 60, StatoAttivita.Attivo, "2024-03-10T09:00:00.000Z")]);
        transport.Responses.Enqueue(new(200, """{"ok":true,"giorno":"2024-03-10","scritti":1,"rimossi":0}"""));
        var sent = client.InviaSegmentiAsync(invio).GetAwaiter().GetResult();
        var sentBody = SyncJson.Decode<InvioSegmenti>(transport.Last!.Body);
        Check("segmenti: POST /api/agente/segmenti col corpo dell'invio", transport.Last.Method == "POST" && transport.Last.Url.EndsWith("/api/agente/segmenti", StringComparison.Ordinal)
              && sentBody.Giorno == invio.Giorno && sentBody.Segmenti.SequenceEqual(invio.Segmenti));
        Check("segmenti: risposta decodificata", sent.Scritti == 1 && sent.Giorno == "2024-03-10");

        transport.Responses.Enqueue(new(200, """{"ok":true,"chiusa":true}"""));
        var stopped = client.StopAsync(T0).GetAwaiter().GetResult();
        Check("stop: POST /api/agente/stop con «alle» UTC", transport.Last!.Url.EndsWith("/api/agente/stop", StringComparison.Ordinal)
              && transport.LastBodyJson()?.GetProperty("alle").GetString() == "2024-03-10T10:00:00.000Z" && stopped.Chiusa);
        transport.Responses.Enqueue(new(200, """{"ok":true,"chiusa":false}"""));
        client.StopAsync(null).GetAwaiter().GetResult();
        Check("stop senza istante: corpo {} (il server usa adesso)", Encoding.UTF8.GetString(transport.Last!.Body) == "{}");

        var noGate = new SyncClient(new Uri("https://track.esempio.it"), token, null, transport: transport);
        transport.Responses.Enqueue(new(200, """{"ok":true,"traccia":false,"sessione":null,"adesso":"2024-03-10T10:00:00Z"}"""));
        var (none, _, _) = noGate.SessioneAsync().GetAwaiter().GetResult();
        Check("senza segreto: nessun cookie; adesso senza millisecondi accettato", !transport.Last!.Headers.ContainsKey("Cookie")
              && none.Sessione is null && SyncJson.ParseInstant(none.Adesso) is not null);

        SyncFault? Failure(MockTransport.Canned canned)
        {
            transport.Responses.Enqueue(canned);
            try { client.InviaSegmentiAsync(invio).GetAwaiter().GetResult(); return null; }
            catch (SyncException e) { return e.Fault; }
        }
        SyncFault? Failure401(Dictionary<string, string> headers, string body)
        {
            var t = new MockTransport();
            t.Responses.Enqueue(new(401, body, Headers: headers));
            var c = new SyncClient(new Uri("https://track.esempio.it"), token, "x", transport: t);
            try { c.SessioneAsync().GetAwaiter().GetResult(); return null; }
            catch (SyncException e) { return e.Fault; }
        }
        Check("401 della rotta (WWW-Authenticate: Bearer) → token rifiutato",
              Failure401(new() { ["WWW-Authenticate"] = "Bearer" }, """{"ok":false,"errore":"token mancante, revocato o non valido"}""")?.Kind == SyncErrorKind.Unauthorized);
        Check("401 del gate (testo «Accesso negato») → segreto del gate", Failure401([], "Accesso negato")?.Kind == SyncErrorKind.Gate);
        Check("422 → errore e indice del tratto", Failure(new(422, """{"ok":false,"errore":"tratti sovrapposti","indice":3}""")) == new SyncFault(SyncErrorKind.Unprocessable, "tratti sovrapposti", 3));
        Check("422 senza indice", Failure(new(422, """{"ok":false,"errore":"giorno nel futuro"}""")) == new SyncFault(SyncErrorKind.Unprocessable, "giorno nel futuro"));
        Check("400 → badRequest col messaggio", Failure(new(400, """{"ok":false,"errore":"corpo non JSON"}""")) == new SyncFault(SyncErrorKind.BadRequest, "corpo non JSON"));
        Check("413 → badRequest", Failure(new(413, """{"ok":false,"errore":"corpo troppo grande"}""")) == new SyncFault(SyncErrorKind.BadRequest, "corpo troppo grande"));
        Check("500 → server(500)", Failure(new(500, "")) == new SyncFault(SyncErrorKind.Server, Status: 500));
        Check("timeout → rete «tempo scaduto»", Failure(new(0, "", new TaskCanceledException("timeout"))) == new SyncFault(SyncErrorKind.Network, "tempo scaduto"));
        Check("200 con JSON non conforme → decoding", Failure(new(200, """{"nope":1}"""))?.Kind == SyncErrorKind.Decoding);
        var bodies = transport.Requests.Select(x => Encoding.UTF8.GetString(x.Body)).ToList();
        Check("token e segreto non compaiono mai nei corpi", !bodies.Any(b => b.Contains(token) || b.Contains("s3greto")));
    }

    private static void Migrations()
    {
        Section("Store: migrazioni v2_sync_days e v3_tracking_ledger");
        var fresh = Store.Temporary();
        CheckT("DB nuovo: tutte le migrazioni", () => fresh.AppliedMigrations().SequenceEqual(["v1_schema", "v1_seed_rules", "v2_sync_days", "v3_tracking_ledger"]),
               () => Join(fresh.AppliedMigrations()));

        // DB di una versione precedente (fino a v2), con righe, riaperto dalla versione nuova
        var dir = Path.Combine(Path.GetTempPath(), "activity-tracker-checks-v2-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "activity.sqlite");
        using (var v2 = new Store(path, "v2_sync_days"))
        {
            CheckT("DB v2: senza tracking_ledger", () => !v2.TableExists("tracking_ledger"));
            v2.Save(new Segment(Today, Epoch.Seconds(T0), Epoch.Seconds(T0) + 60, ActivityState.Active, appName: "Vecchia app"));
            v2.SaveSyncDay(new SyncDayRecord(Today, "h", Epoch.Seconds(T0)));
        }
        using var store = new Store(path);
        CheckT("riapertura: v3 applicata, segmenti ed esiti restano",
               () => store.TableExists("tracking_ledger") && store.Segments(Today).Count == 1 && store.SyncDays().Count == 1);

        var intervals = LedgerFrom((ServerState.None, 0, 60), (Tracking1, 120, 600), (Paused1, 660, 700)).Intervals;
        store.ReplaceLedger(intervals);
        CheckT("registro: roundtrip", () => store.LedgerIntervals().SequenceEqual(intervals));
        store.ReplaceLedger(intervals.Take(1));
        CheckT("registro: sostituzione intera", () => store.LedgerIntervals().Count == 1);

        store.Save(new Segment(Today, Epoch.Seconds(T0) + 100, Epoch.Seconds(T0) + 200, ActivityState.Active, appName: "A"));
        store.Save(new Segment(Today, Epoch.Seconds(T0) + 300, Epoch.Seconds(T0) + 400, ActivityState.Active, appName: "B"));
        CheckT("segmenti per intervallo di tempo (sovrapposizione)",
               () => store.Segments(T0.AddSeconds(150), T0.AddSeconds(310)).Select(x => x.AppName).SequenceEqual(["A", "B"]));
        CheckT("segmenti per intervallo: estremi esclusi", () => store.Segments(T0.AddSeconds(200), T0.AddSeconds(300)).Count == 0);
        store.SaveSyncDays([new SyncDayRecord("2024-03-01", "x", Epoch.Seconds(T0))]);
        store.DeleteSyncDays("2024-03-05");
        CheckT("esiti vecchi potati", () => store.SyncDays().Select(x => x.Day).SequenceEqual([Today]));
        CheckT("ultimo invio riuscito", () => store.LastSuccessfulSyncAt() == T0);
        store.Dispose();
        fresh.Dispose();
        CoreChecks.Cleanup(path);
        CoreChecks.Cleanup(fresh.Path);
    }

    private static void VideoRule()
    {
        Section("Windows: la regola del video (sessioni multimediali + richiesta «schermo acceso»)");
        string[] exclusions = ["Spotify.exe", "PowerToys.Awake.exe", "caffeine64"];
        var chrome = new MediaPlayback("chrome.exe", "Google Chrome", true);
        var spotify = new MediaPlayback("spotify.exe", "Spotify", true);
        var vlc = new MediaPlayback("vlc.exe", "VLC media player", true);
        VideoChoice? Choose(bool display, MediaPlayback[] playing, string? fg = "notepad.exe", string[]? running = null) =>
            VideoOwnerPolicy.Choose(display, playing, fg, fg is null ? null : "Blocco note", running ?? [], exclusions, "activity-tracker.exe");

        Check("scenario 1: YouTube in Chrome in riproduzione → video di Chrome", Choose(true, [chrome], "chrome.exe")?.AppId == "chrome.exe");
        Check("Chrome riproduce anche se la richiesta di sistema non si vede → video di Chrome", Choose(false, [chrome], "chrome.exe")?.AppId == "chrome.exe");
        Check("scenario 9 (variante): video in Chrome, Blocco note in primo piano → il video resta di Chrome", Choose(true, [chrome])?.AppId == "chrome.exe");
        Check("scenario 5: Spotify (escluso) riproduce, nessuna richiesta schermo → nessun video", Choose(false, [spotify]) is null);
        Check("Spotify escluso anche se qualcuno tiene acceso lo schermo… → ripiego sul primo piano", Choose(true, [spotify])?.AppId == "notepad.exe");
        Check("più app riproducono: vince quella in primo piano", Choose(true, [chrome, vlc], "vlc.exe")?.AppId == "vlc.exe");
        Check("più app riproducono, nessuna in primo piano: la prima per nome", Choose(true, [vlc, chrome])?.AppId == "chrome.exe");
        Check("app ridotta a icona: la sua riproduzione non è video", Choose(false, [chrome with { HasVisibleWindow = false }]) is null);
        Check("scenario 9: videochiamata senza sessione multimediale → richiesta schermo, va al primo piano",
              Choose(true, [], "zoom.exe")?.AppId == "zoom.exe" && Choose(true, [], "zoom.exe")?.Source == "schermo tenuto acceso");
        Check("scenario 2: niente riproduzione, niente richiesta schermo → nessun video", Choose(false, []) is null);
        Check("scenario 5 (variante): PowerToys Awake in esecuzione → la richiesta schermo non conta",
              Choose(true, [], running: ["explorer.exe", "PowerToys.Awake.exe"]) is null);
        Check("esclusione senza «.exe» e senza maiuscole", Choose(true, [], running: ["CAFFEINE64.EXE"]) is null);
        Check("l'agente stesso non è mai il video", Choose(true, [], "activity-tracker.exe") is null);
        Check("nessuna app in primo piano: la sola richiesta schermo non basta", Choose(true, [], fg: null) is null);
    }
}
