using System.Globalization;
using ActivityTracker.Core;
using Timer = System.Windows.Forms.Timer;

namespace ActivityTracker.Windows.Sync;

/// <summary>Ciò che il sync legge dall'app: store, regole correnti, segmenti con la coda in memoria sovrapposta (come
/// il report live), la coda provvisoria del motore e il cancello fra motore e coalescer. Implementato da <c>AppModel</c>.</summary>
public interface ISyncDataSource
{
    Store? SyncStore { get; }
    IReadOnlyList<ActivityRule> SyncRules { get; }
    SessionGate SyncGate { get; }
    List<Segment> SyncSegments(DateTimeOffset from, DateTimeOffset to);
    IReadOnlyList<ProvisionalSlice> SyncProvisionalSlices { get; }
}

/// <summary>
/// Sync con equipe-track, riscrittura di <c>SyncService.swift</c>. Legge <c>GET /api/agente/sessione</c> ogni minuto e
/// apre o chiude il cancello del contenuto; ogni 5 minuti, e quando la sessione si chiude o va in pausa, manda i tratti
/// delle giornate cambiate; allo spegnimento del PC fa un ultimo invio e chiude la sessione (<c>POST /api/agente/stop</c>).
/// Tutti i metodi vanno chiamati dal thread dell'interfaccia; la rete è asincrona e lo stato si aggiorna solo lì.
/// </summary>
public sealed class SyncService
{
    public enum Link
    {
        NotConfigured,
        /// <summary>Configurato, ma nessuna lettura recente del server: non si traccia.</summary>
        Waiting,
        Tracking,
        Paused,
        NoSession,
        TokenRejected,
        GateRejected,
    }

    public const int PollIntervalMs = 60_000;
    public const int SendIntervalMs = 5 * 60_000;
    public const int WakeDelayMs = 8_000;
    public const int UnlockDelayMs = 3_000;
    /// <summary>Quanto si aspetta la rete allo spegnimento prima di lasciar andare il PC.</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(8);

    public Link State { get; private set; } = Link.NotConfigured;
    public string ServerUrl { get; private set; } = "";
    public bool HasGateSecret { get; private set; }
    public string? SessionGiorno { get; private set; }
    public DateTimeOffset? LastSendAt { get; private set; }
    public string? LastError { get; private set; }
    public bool IsBusy { get; private set; }

    /// <summary>Lo stato è cambiato (per aggiornare icona e finestre).</summary>
    public event Action? Changed;

    private readonly ISyncDataSource _source;
    private readonly ITokenStore _secrets;
    private readonly IHttpTransport _transport;
    private readonly bool _allowInsecureLocalhost;

    private EquipeCredentials? _credentials;
    private TrackingSync? _core;
    private Timer? _pollTimer;
    private Timer? _sendTimer;
    private bool _queuedCycle;
    private bool _queuedSend;
    private bool _started;
    private bool _shutDown;

    public SyncService(ISyncDataSource source, ITokenStore secrets, IHttpTransport? transport = null, bool? allowInsecureLocalhost = null)
    {
        _source = source;
        _secrets = secrets;
        _transport = transport ?? new HttpClientTransport();
        _allowInsecureLocalhost = allowInsecureLocalhost ?? SyncClient.EnvironmentAllowsInsecureLocalhost();
    }

    // Ciclo di vita

    /// <summary>Da chiamare una volta all'avvio: carica le credenziali e, se ci sono, comincia a leggere.</summary>
    public void Start()
    {
        if (_started || _source.SyncStore is not { } store) return;
        _started = true;
        try
        {
            _core = new TrackingSync(store);
            LastSendAt = store.LastSuccessfulSyncAt();
        }
        catch (Exception e)
        {
            LastError = "Registro delle sessioni illeggibile: " + e.Message;
        }
        try
        {
            _credentials = EquipeCredentials.Load(_secrets);
        }
        catch (Exception e)
        {
            LastError = "Gestione credenziali: " + e.Message;
        }
        ServerUrl = _credentials?.ServerUrl ?? "";
        HasGateSecret = !string.IsNullOrEmpty(_credentials?.GateSecret);
        RefreshGate();
        if (_credentials is null)
        {
            State = Link.NotConfigured;
            Notify();
            return;
        }
        State = Link.Waiting;
        ScheduleTimers();
        RequestCycle(send: true);
    }

    /// <summary>Ripresa dalla sospensione e sblocco: lettura poco dopo (la rete può non essere ancora pronta).</summary>
    public void SystemDidWake() => RequestCycleLater(WakeDelayMs);
    public void SystemDidUnlock() => RequestCycleLater(UnlockDelayMs);

    // Credenziali

    public bool HasToken => _credentials is not null;

    /// <summary>Salva le credenziali. Token o segreto vuoti = si tiene quello già salvato. Un URL o un token diversi da
    /// prima ricominciano da zero (registro ed esiti dimenticati: potrebbe essere un'altra persona).</summary>
    public bool SaveCredentials(string rawUrl, string rawToken, string rawSecret)
    {
        var (url, error) = SyncClient.Validate(rawUrl, _allowInsecureLocalhost);
        if (url is null)
        {
            LastError = Message(error!);
            Notify();
            return false;
        }
        var token = rawToken.Trim();
        var newToken = token.Length == 0 ? _credentials?.Token ?? "" : token;
        if (newToken.Length == 0)
        {
            LastError = "Inserisci il token dell'agente";
            Notify();
            return false;
        }
        if (!SyncClient.IsWellFormedToken(newToken))
        {
            LastError = "Token non valido: 20–200 caratteri fra lettere, cifre, «-» e «_»";
            Notify();
            return false;
        }
        var secret = rawSecret.Trim();
        var updated = new EquipeCredentials(SyncClient.Text(url), newToken, secret.Length == 0 ? _credentials?.GateSecret ?? "" : secret);
        var identityChanged = _credentials?.ServerUrl != updated.ServerUrl || _credentials?.Token != updated.Token;
        try
        {
            updated.Save(_secrets);
        }
        catch (Exception e)
        {
            LastError = "Gestione credenziali: " + e.Message;
            Notify();
            return false;
        }
        _credentials = updated;
        ServerUrl = updated.ServerUrl;
        HasGateSecret = updated.GateSecret.Length > 0;
        LastError = null;
        if (identityChanged)
        {
            try { _core?.Reset(); } catch (Exception e) { Log.Write("[Sync] azzeramento del registro fallito: " + e.Message); }
            LastSendAt = null;
            SessionGiorno = null;
        }
        State = Link.Waiting;
        RefreshGate();
        ScheduleTimers();
        RequestCycle(send: true);
        Log.Write("[Sync] credenziali salvate per " + updated.ServerUrl);
        Notify();
        return true;
    }

    /// <summary>Toglie le credenziali e smette di tracciare.</summary>
    public void Disconnect()
    {
        StopTimers();
        try { EquipeCredentials.Delete(_secrets); } catch (Exception e) { Log.Write("[Sync] rimozione delle credenziali fallita: " + e.Message); }
        try { _core?.Reset(); } catch (Exception e) { Log.Write("[Sync] azzeramento del registro fallito: " + e.Message); }
        _credentials = null;
        ServerUrl = "";
        HasGateSecret = false;
        SessionGiorno = null;
        LastSendAt = null;
        LastError = null;
        State = Link.NotConfigured;
        RefreshGate();
        Notify();
    }

    /// <summary>Lettura della sessione e invio subito.</summary>
    public void SendNow() => RequestCycle(send: true);

    // Testo per l'interfaccia

    public string StatusText => State switch
    {
        Link.NotConfigured => "Non configurato: non traccia",
        Link.Waiting => "In attesa di equipe-track: non traccia",
        Link.Tracking => "Sessione aperta: traccia",
        Link.Paused => "Sessione in pausa: non traccia",
        Link.NoSession => "Nessuna sessione aperta: non traccia",
        Link.TokenRejected => "Token rifiutato: non traccia",
        _ => "Segreto del gate rifiutato: non traccia",
    };

    public string LastSendText => LastSendAt is { } t ? "Ultimo invio: " + TimeText(t) : "Ultimo invio: mai";

    /// <summary>Riga discreta per la finestra del report.</summary>
    public string MenuLine
    {
        get
        {
            var s = "equipe-track: " + char.ToLowerInvariant(StatusText[0]) + StatusText[1..];
            if (State == Link.Tracking && LastSendAt is { } t) s += " · ultimo invio " + TimeText(t);
            if (LastError is { } e && State != Link.TokenRejected && State != Link.GateRejected) s += " · " + e;
            return s;
        }
    }

    public bool NeedsAttention => State is Link.TokenRejected or Link.GateRejected || (State != Link.NotConfigured && LastError is not null);

    // Pianificazione

    private void ScheduleTimers()
    {
        if (_pollTimer is null)
        {
            _pollTimer = new Timer { Interval = PollIntervalMs };
            _pollTimer.Tick += (_, _) => RequestCycle(send: false);
            _pollTimer.Start();
        }
        if (_sendTimer is null)
        {
            _sendTimer = new Timer { Interval = SendIntervalMs };
            _sendTimer.Tick += (_, _) => RequestCycle(send: true);
            _sendTimer.Start();
        }
    }

    private void StopTimers()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        _sendTimer?.Dispose();
        _sendTimer = null;
    }

    private void RequestCycleLater(int delayMs)
    {
        var t = new Timer { Interval = delayMs };
        t.Tick += (_, _) =>
        {
            t.Dispose();
            RequestCycle(send: false);
        };
        t.Start();
    }

    private async void RequestCycle(bool send)
    {
        if (_shutDown || _credentials is null || _core is null || State is Link.TokenRejected or Link.GateRejected) return;
        if (IsBusy)
        {
            _queuedCycle = true;
            _queuedSend = _queuedSend || send;
            return;
        }
        IsBusy = true;
        Notify();
        try
        {
            await Cycle(send);
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Log.Write("[Sync] errore inatteso: " + e.Message);
        }
        IsBusy = false;
        Notify();
        if (_queuedCycle)
        {
            var again = _queuedSend;
            _queuedCycle = false;
            _queuedSend = false;
            RequestCycle(again);
        }
    }

    // Esecuzione

    /// <summary>Una lettura della sessione e, se richiesto o se il tracciamento si è appena fermato, un invio.</summary>
    private async Task Cycle(bool send)
    {
        if (MakeClient() is not { } client || _core is not { } core) return;
        var mustSend = send;
        try
        {
            var (r, sentAt, receivedAt) = await client.SessioneAsync();
            var change = core.Apply(r, sentAt, receivedAt);
            // Chiusura o pausa della sessione: i tratti fino a qui partono subito.
            if (change.Previous?.IsTracking == true && !change.Current.IsTracking) mustSend = true;
            SessionGiorno = r.Sessione?.Giorno;
            LastError = null;
        }
        catch (Exception e)
        {
            Handle(e);
        }
        RefreshGate();
        if (!mustSend || State is Link.TokenRejected or Link.GateRejected) return;
        await SendPending(client, core);
    }

    private async Task SendPending(SyncClient client, TrackingSync core)
    {
        List<PreparedInvio> prepared;
        try
        {
            prepared = PrepareInvii(core);
        }
        catch (Exception e)
        {
            LastError = "Lettura dei dati locali fallita: " + e.Message;
            return;
        }
        var rejected = new List<string>();
        foreach (var p in prepared)
        {
            var outcome = await TrackingSync.TransmitAsync(p, client, core.ClockOffset);
            try { core.Finish(p, outcome, DateTimeOffset.UtcNow); } catch (Exception e) { Log.Write("[Sync] registrazione dell'esito fallita: " + e.Message); }
            switch (outcome.Kind)
            {
                case TransmitKind.Sent:
                    LastSendAt = DateTimeOffset.UtcNow;
                    Log.Write($"[Sync] {outcome.Response!.Giorno}: {outcome.Response.Scritti} tratti scritti, {outcome.Response.Rimossi} rimossi"
                              + (outcome.Dropped > 0 ? $", {outcome.Dropped} tolti dopo un rifiuto" : ""));
                    break;
                case TransmitKind.Rejected:
                    rejected.Add($"giornata {p.Giorno} respinta: {outcome.Message}");
                    Log.Write($"[Sync] giornata {p.Giorno} respinta: {outcome.Message}");
                    break;
                case TransmitKind.Failed:
                    Handle(new SyncException(outcome.Fault!));
                    RefreshGate();
                    return;
            }
        }
        LastError = rejected.Count switch
        {
            0 => null,
            1 => rejected[0],
            _ => $"{rejected[0]} (+{rejected.Count - 1})",
        };
        RefreshGate();
    }

    private List<PreparedInvio> PrepareInvii(TrackingSync core)
    {
        if (core.DataRange is not { } range) return [];
        var segments = _source.SyncSegments(range.Start, range.End.AddSeconds(1));
        return core.PendingInvii(segments, _source.SyncProvisionalSlices, new ActivityResolver(_source.SyncRules));
    }

    /// <summary>Allo spegnimento (o all'uscita): conferma lo stato corrente fino a ora, manda le giornate cambiate e, se
    /// richiesto e se c'è una sessione aperta, la chiude. Blocca al massimo <see cref="ShutdownTimeout"/>.</summary>
    public void Shutdown(bool closeSession)
    {
        if (_shutDown) return;
        _shutDown = true;
        StopTimers();
        if (MakeClient() is not { } client || _core is not { } core || State is Link.TokenRejected or Link.GateRejected) return;
        var now = DateTimeOffset.UtcNow;
        var state = core.CurrentState(now);
        if (state is not null)
        {
            try { core.Record(state, now); } catch (Exception) { }
        }
        List<PreparedInvio> prepared;
        try { prepared = PrepareInvii(core); } catch (Exception) { prepared = []; }
        var offset = core.ClockOffset;
        var shouldStop = closeSession && state?.Kind is ServerStateKind.Tracking or ServerStateKind.Paused;

        var outcomes = new List<(PreparedInvio, TransmitOutcome)>();
        RispostaStop? stop = null;
        // Fuori dal thread dell'interfaccia: qui lo si blocca, e le continuazioni non devono tornarci.
        var work = Task.Run(async () =>
        {
            foreach (var p in prepared)
            {
                var o = await TrackingSync.TransmitAsync(p, client, offset, maxRetries: 2).ConfigureAwait(false);
                lock (outcomes) outcomes.Add((p, o));
            }
            if (shouldStop)
            {
                try { stop = await client.StopAsync(null).ConfigureAwait(false); } catch (Exception) { }
            }
        });
        bool finished;
        try { finished = work.Wait(ShutdownTimeout); }
        catch (AggregateException e)
        {
            finished = true;
            Log.Write("[Sync] uscita: " + e.InnerException?.Message);
        }
        List<(PreparedInvio, TransmitOutcome)> done;
        lock (outcomes) done = outcomes.ToList();
        foreach (var (p, o) in done)
        {
            try { core.Finish(p, o, DateTimeOffset.UtcNow); } catch (Exception) { }
        }
        var stopText = !shouldStop ? "non richiesto" : stop is null ? "non riuscito" : stop.Chiusa ? "riuscito (sessione chiusa)" : "riuscito (nulla di aperto)";
        Log.Write($"[Sync] uscita: {done.Count}/{prepared.Count} giornate inviate, stop {stopText}{(finished ? "" : " (tempo scaduto)")}");
    }

    // Aiuti

    private void Handle(Exception error)
    {
        if (error is not SyncException { Fault: var fault })
        {
            LastError = error.Message;
            return;
        }
        switch (fault.Kind)
        {
            case SyncErrorKind.Unauthorized:
                State = Link.TokenRejected;
                StopTimers();
                break;
            case SyncErrorKind.Gate:
                State = Link.GateRejected;
                StopTimers();
                break;
        }
        LastError = Message(fault);
        Log.Write("[Sync] errore: " + LastError);
    }

    /// <summary>Cancello e stato visibile dall'ultima lettura: senza una lettura recente il contenuto non si conserva.</summary>
    private void RefreshGate()
    {
        var rejected = State is Link.TokenRejected or Link.GateRejected;
        _source.SyncGate.Windows = _credentials is not null && !rejected ? _core?.ContentWindows(DateTimeOffset.UtcNow) ?? [] : [];
        if (_credentials is null)
        {
            State = Link.NotConfigured;
            return;
        }
        if (rejected) return;
        State = _core?.CurrentState(DateTimeOffset.UtcNow)?.Kind switch
        {
            ServerStateKind.Tracking => Link.Tracking,
            ServerStateKind.Paused => Link.Paused,
            ServerStateKind.None => Link.NoSession,
            _ => Link.Waiting,
        };
    }

    /// <summary>Da chiamare periodicamente (ogni tick del report): la lettura del server invecchia anche senza eventi.</summary>
    public void Refresh()
    {
        var before = State;
        RefreshGate();
        if (State != before) Notify();
    }

    private SyncClient? MakeClient()
    {
        if (_credentials is not { } c || SyncClient.Validate(c.ServerUrl, _allowInsecureLocalhost).Url is not { } url) return null;
        return new SyncClient(url, c.Token, c.GateSecret.Length == 0 ? null : c.GateSecret, AppIdentity.AgentVersion, _transport);
    }

    private void Notify() => Changed?.Invoke();

    public static string Message(SyncFault e) => e.Kind switch
    {
        SyncErrorKind.InsecureUrl => "URL non sicuro: serve https:// (http solo per localhost con AT_ALLOW_INSECURE_LOCALHOST=1)",
        SyncErrorKind.InvalidUrl => "URL del server non valido",
        SyncErrorKind.Unauthorized => "Token rifiutato da equipe-track: emettine uno nuovo (npm run agente:token) e salvalo qui",
        SyncErrorKind.Gate => "Il gate di equipe-track ha rifiutato la richiesta: controlla il segreto (ACCESS_SECRET)",
        SyncErrorKind.Forbidden => "Operazione non consentita (403)",
        SyncErrorKind.BadRequest or SyncErrorKind.Unprocessable => "Richiesta rifiutata dal server: " + e.Detail,
        SyncErrorKind.RateLimited => "Server occupato (429)",
        SyncErrorKind.Server => $"Errore del server ({e.Status})",
        SyncErrorKind.Network => "Rete: " + e.Detail,
        _ => "Risposta del server non valida",
    };

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public static string TimeText(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm", Italian) : local.ToString("d MMM, HH:mm", Italian);
    }
}
