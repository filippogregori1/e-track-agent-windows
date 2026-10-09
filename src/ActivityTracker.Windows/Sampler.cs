using ActivityTracker.Core;
using ActivityTracker.Windows.Signals;
using ActivityTracker.Windows.Native;
using Timer = System.Windows.Forms.Timer;

namespace ActivityTracker.Windows;

/// <summary>
/// Campionatore: ogni secondo compone l'<see cref="Observation"/> dai lettori di segnali, la passa al motore, e
/// persiste i segmenti ogni 10 s e sugli eventi (sospensione, blocco, cambio giorno, uscita). Gira sul thread
/// dell'interfaccia, come quello del Mac sul main run loop.
/// </summary>
public sealed class Sampler
{
    private const double FlushInterval = 10;

    private readonly ClassificationEngine _engine;
    private readonly SegmentCoalescer _coalescer;
    private readonly Store _store;
    private readonly SessionStateMonitor _monitor;
    private readonly BrowserUrlReader _browser;
    private readonly VideoSignalReader _video;
    private readonly DayClock _dayClock;
    private readonly ForegroundReader _foreground = new();

    private Timer? _timer;
    private DateTimeOffset _lastFlushAt = DateTimeOffset.UtcNow;
    private string _currentDay;
    private bool _stopped;

    /// <summary>Ultima osservazione prodotta (per lo stato live).</summary>
    public Observation? LastObservation { get; private set; }
    /// <summary>Ultimo errore di persistenza (mostrato nella finestra del report).</summary>
    public Exception? LastError { get; private set; }

    public Sampler(Store store, ClassificationEngine engine, SegmentCoalescer coalescer, SessionStateMonitor monitor,
                   BrowserUrlReader browser, VideoSignalReader video, DayClock dayClock)
    {
        _store = store;
        _engine = engine;
        _coalescer = coalescer;
        _monitor = monitor;
        _browser = browser;
        _video = video;
        _dayClock = dayClock;
        _currentDay = dayClock.DayString(DateTimeOffset.UtcNow);
    }

    public void Start()
    {
        if (_timer is not null) return;
        _monitor.SessionWillClose += CloseSession;
        _timer = new Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    /// <summary>Uscita ordinata: coda confermata attiva e flush finale.</summary>
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
        _engine.Finish(DateTimeOffset.UtcNow);
        Flush();
    }

    public void Tick()
    {
        if (_stopped) return;
        _monitor.Poll();
        var now = DateTimeOffset.UtcNow;
        var observation = MakeObservation(now);
        LastObservation = observation;
        _engine.Observe(observation);

        var day = _dayClock.DayString(now);
        if (day != _currentDay || (now - _lastFlushAt).TotalSeconds >= FlushInterval)
        {
            _currentDay = day;
            Flush();
        }
    }

    public void Flush()
    {
        try
        {
            _coalescer.Flush(_store);
            LastError = null;
        }
        catch (Exception e)
        {
            LastError = e;
            Log.Write("[Sampler] flush fallito: " + e.Message);
        }
        _lastFlushAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Sospensione / blocco / schermo spento: chiude subito la coda e persiste.</summary>
    private void CloseSession()
    {
        if (_stopped) return;
        var now = DateTimeOffset.UtcNow;
        var observation = new Observation(now, false, LastInputAt(now), null, null, _monitor.IsDisplayAsleep);
        LastObservation = observation;
        _engine.Observe(observation);
        Flush();
    }

    private static DateTimeOffset LastInputAt(DateTimeOffset now) => now.AddMilliseconds(-Win32.MillisecondsSinceLastInput());

    // Composizione dell'osservazione

    private Observation MakeObservation(DateTimeOffset now)
    {
        var lastInputAt = LastInputAt(now);
        if (!_monitor.IsSessionOpen)
            return new Observation(now, false, lastInputAt, null, null, _monitor.IsDisplayAsleep);

        var front = _foreground.Current();
        var foreground = front is null ? null : Subject(front, forVideo: false);
        Subject? video = null;
        if (_video.Current(front?.AppId, front?.AppName) is { } owner)
        {
            if (front is not null && foreground is not null && owner.AppId == front.AppId)
            {
                // Come sul Mac: per il video di un browser, se la scheda non è ancora stata letta vale l'ultimo dominio noto.
                video = foreground.Domain is null && BrowserUrlReader.IsSupported(front.AppId) && _browser.LastKnownDomain(front.AppId) is { } last
                    ? foreground with { Domain = last, TitleHint = null }
                    : foreground;
            }
            else
            {
                // Il video è di un'altra app (visibile, non in primo piano): la sua finestra principale.
                var window = ProcessNames.VisibleWindows(owner.AppId).Select(ForegroundReader.Describe).FirstOrDefault(w => w is not null);
                video = window is not null
                    ? Subject(window, forVideo: true)
                    : new Subject(owner.AppId, owner.AppName, BrowserUrlReader.IsSupported(owner.AppId) ? _browser.LastKnownDomain(owner.AppId) : null);
            }
        }
        return new Observation(now, true, lastInputAt, foreground, video);
    }

    private Subject Subject(AppWindow app, bool forVideo)
    {
        string? domain = null;
        if (BrowserUrlReader.IsSupported(app.AppId))
        {
            domain = _browser.ActiveTabDomain(app.AppId, app.Hwnd);
            if (domain is null && forVideo) domain = _browser.LastKnownDomain(app.AppId);
        }
        var title = domain is null && app.Title.Length > 0 ? app.Title : null;
        return new Subject(app.AppId, app.AppName, domain, title);
    }
}
