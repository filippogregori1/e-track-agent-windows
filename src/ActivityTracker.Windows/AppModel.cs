using System.Diagnostics;
using System.Globalization;
using ActivityTracker.Core;
using ActivityTracker.Windows.Signals;
using ActivityTracker.Windows.Sync;

namespace ActivityTracker.Windows;

/// <summary>Stato condiviso da icona, finestra del report e Impostazioni. Possiede store, motore e campionatore.
/// Riscrittura di <c>AppModel.swift</c>: stessi pezzi, stesso ordine (motore → cancello → coalescer → SQLite).</summary>
public sealed class AppModel : ISyncDataSource, IDisposable
{
    public AppSettings Settings { get; } = new();
    public DayClock DayClock { get; } = new();
    public ClassificationEngine Engine { get; }
    public SegmentCoalescer Coalescer { get; } = new();
    /// <summary>Fra motore e coalescer: fuori dalle sessioni di equipe-track toglie app, dominio e titolo.</summary>
    public SessionGate Gate { get; }
    public SessionStateMonitor Monitor { get; }
    public BrowserUrlReader Browser { get; }
    public VideoSignalReader Video { get; }
    public SyncService Sync { get; }
    public Store? Store { get; private set; }
    public Sampler? Sampler { get; private set; }

    // Report live
    public Report Report { get; private set; } = Report.Empty;
    public string StatusLine { get; private set; } = "Avvio…";
    public string DateTitle { get; private set; } = "";
    public string? StartupError { get; private set; }
    public string? StorageError { get; private set; }

    public List<ActivityRule> Rules { get; private set; } = [];
    public string? RulesError { get; private set; }
    public string LoginStatus { get; private set; } = "";

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public AppModel()
    {
        Gate = new SessionGate(Coalescer);
        Engine = new ClassificationEngine(Gate, Settings.ThresholdSeconds, DayClock);
        Monitor = new SessionStateMonitor();
        Browser = new BrowserUrlReader(!AppSettings.SkipBrowserUrl);
        Video = new VideoSignalReader(Settings.VideoExclusions);
        Sync = new SyncService(this, new CredentialManagerStore());
    }

    // Ciclo di vita

    public void Start()
    {
        try
        {
            Store = AppSettings.DatabasePathOverride is { } path ? new Store(path) : Store.Default();
            Rules = Store.Rules();
            Sampler = new Sampler(Store, Engine, Coalescer, Monitor, Browser, Video, DayClock);
            Sampler.Start();
        }
        catch (Exception e)
        {
            StartupError = "Impossibile aprire il database: " + e.Message;
            Log.Write("[AppModel] " + StartupError);
        }
        Monitor.DidWake += Sync.SystemDidWake;
        Monitor.DidUnlock += Sync.SystemDidUnlock;
        // Spegnimento, riavvio o disconnessione: flush, ultimo invio e chiusura della sessione su equipe-track.
        Monitor.WillPowerOff += powerOff => Shutdown(closeSession: powerOff);
        RegisterLoginItemIfFirstLaunch();
        RefreshReport();
        if (Store is not null) Sync.Start();
        Log.Write($"[AppModel] avvio, soglia {Settings.ThresholdMinutes} min, dati in {DataFolderPath}");
    }

    private bool _shutdown;

    /// <summary>Uscita: coda confermata attiva, flush finale, ultimo invio. La sessione su equipe-track si chiude solo
    /// allo spegnimento del PC (o con <c>AT_STOP_ON_QUIT=1</c>).</summary>
    public void Shutdown(bool closeSession)
    {
        if (_shutdown) return;
        _shutdown = true;
        try { Sampler?.Stop(); } catch (Exception e) { Log.Write("[AppModel] flush finale fallito: " + e.Message); }
        try { Sync.Shutdown(closeSession); } catch (Exception e) { Log.Write("[AppModel] invio finale fallito: " + e.Message); }
    }

    public void Dispose()
    {
        Browser.Dispose();
        Video.Dispose();
        Monitor.Dispose();
        Store?.Dispose();
    }

    private void RegisterLoginItemIfFirstLaunch()
    {
        if (!AppSettings.SkipLoginItem && !Settings.LoginItemAttempted)
        {
            Settings.LoginItemAttempted = true;
            LoginStatus = LoginItem.Enable();
        }
        else
        {
            LoginStatus = LoginItem.Status();
        }
    }

    // Report

    public void RefreshReport()
    {
        var now = DateTimeOffset.UtcNow;
        var day = DayClock.DayString(now);
        var title = DateTimeOffset.Now.ToString("dddd d MMMM yyyy", Italian);
        DateTitle = char.ToUpper(title[0], Italian) + title[1..];

        var segments = new List<Segment>();
        if (Store is not null)
        {
            try
            {
                segments = Store.Segments(day);
                StorageError = Sampler?.LastError is { } e ? "Salvataggio fallito: " + e.Message : null;
            }
            catch (Exception e)
            {
                StorageError = "Lettura fallita: " + e.Message;
            }
        }
        var merged = Coalescer.Overlay(segments);
        var builder = new ReportBuilder(new ActivityResolver(Rules), DayClock);
        Report = builder.Build(day, merged, Engine.ProvisionalSlices);
        StatusLine = MakeStatusLine(now);
        Sync.Refresh();
    }

    private string MakeStatusLine(DateTimeOffset now)
    {
        if (Sampler is null) return "Non attivo";
        if (!Engine.IsSessionOpen) return $"Ora: fuori sessione ({Monitor.Summary})";
        var resolver = new ActivityResolver(Rules);
        var observation = Sampler.LastObservation;
        var idle = Engine.IdleDuration(now);
        if (Engine.IsIdleConfirmed)
        {
            var s = "Ora: inattivo da " + ShortDuration(idle);
            if (observation?.Video is { } video) s += " · video: " + resolver.Activity(video);
            return s;
        }
        var line = "Ora: attivo su " + resolver.Activity(observation?.Foreground);
        if (idle >= 60) line += " · fermo da " + ShortDuration(idle);
        return line;
    }

    public string ReportText => ReportFormatter.Text(Report);

    // Impostazioni: soglia ed esclusioni

    public int ThresholdMinutes
    {
        get => Settings.ThresholdMinutes;
        set
        {
            if (value == Settings.ThresholdMinutes) return;
            Settings.ThresholdMinutes = value;
            Engine.Threshold = Settings.ThresholdSeconds;
        }
    }

    public IReadOnlyList<string> VideoExclusions
    {
        get => Settings.VideoExclusions;
        set
        {
            var clean = value.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            Settings.VideoExclusions = clean;
            Video.Exclusions = clean;
        }
    }

    public void ResetExclusions() => VideoExclusions = AppSettings.DefaultVideoExclusions;

    // Regole

    public void ReloadRules()
    {
        if (Store is null) return;
        try
        {
            Rules = Store.Rules();
            RulesError = null;
        }
        catch (Exception e)
        {
            RulesError = e.Message;
        }
    }

    public void AddRule()
    {
        if (Store is null) return;
        var n = Rules.Count + 1;
        while (Rules.Any(r => r.Kind == RuleKind.Domain && r.Pattern == $"nuovo-{n}.example")) n++;
        try
        {
            Store.InsertRule(new ActivityRule(RuleKind.Domain, $"nuovo-{n}.example", "Nuova attività"));
            ReloadRules();
        }
        catch (Exception e)
        {
            RulesError = e.Message;
        }
    }

    public bool UpdateRule(ActivityRule rule)
    {
        if (Store is null || rule.Id is null) return false;
        try
        {
            Store.UpdateRule(rule);
            ReloadRules();
            return true;
        }
        catch (Exception)
        {
            RulesError = $"Regola non salvata ({rule.Kind.Label()} «{rule.Pattern}»): pattern già presente?";
            return false;
        }
    }

    public void DeleteRule(ActivityRule rule)
    {
        if (Store is null || rule.Id is not { } id) return;
        try
        {
            Store.DeleteRule(id);
            ReloadRules();
        }
        catch (Exception e)
        {
            RulesError = e.Message;
        }
    }

    // Avvio all'accesso

    public void SetLaunchAtLogin(bool enabled) => LoginStatus = enabled ? LoginItem.Enable() : LoginItem.Disable();

    public void RefreshLoginStatus() => LoginStatus = LoginItem.Status();

    // Cartella dati

    public string DataFolderPath => Path.GetDirectoryName(Store?.Path ?? AppIdentity.DatabasePath())!;

    public void OpenDataFolder()
    {
        Directory.CreateDirectory(DataFolderPath);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataFolderPath}\"") { UseShellExecute = true });
    }

    /// <summary>Diagnostica dei segnali per le prove a mano (Impostazioni › Segnali).</summary>
    public string SignalsText()
    {
        var lines = new List<string>
        {
            $"Sessione: {Monitor.Summary}",
            $"Schermo tenuto acceso (richiesta di sistema): {(Video.LastDisplayRequired ? "sì" : "no")}",
            "Sessioni multimediali in riproduzione: " + (Video.LastPlaying.Count == 0 ? "nessuna"
                : string.Join(", ", Video.LastPlaying.Select(p => $"{p.AppName ?? p.AppId}{(p.HasVisibleWindow ? "" : " (nessuna finestra visibile)")}"))),
            "Video attribuito a: " + (Video.LastChoice is { } c ? $"{c.AppName ?? c.AppId} ({c.Source})" : "nessuno"),
            "Barra degli indirizzi: " + (!Browser.IsEnabled ? "lettura disattivata (AT_NO_BROWSER_URL)" : Browser.LastError ?? "lettura attiva"),
        };
        if (Video.MediaError is { } m) lines.Add(m);
        return string.Join(Environment.NewLine, lines);
    }

    public static string ShortDuration(double seconds)
    {
        var s = (int)seconds;
        if (s < 60) return $"{s}s";
        if (s < 3600) return $"{s / 60}m";
        return ReportFormatter.Duration(seconds);
    }

    // Dati per il sync: gli stessi del report live. Campionatore e motore non cambiano.

    public Store? SyncStore => Store;
    public IReadOnlyList<ActivityRule> SyncRules => Rules;
    public SessionGate SyncGate => Gate;
    public IReadOnlyList<ProvisionalSlice> SyncProvisionalSlices => Engine.ProvisionalSlices;

    public List<Segment> SyncSegments(DateTimeOffset from, DateTimeOffset to) =>
        Store is null ? [] : Coalescer.Overlay(Store.Segments(from, to));
}
