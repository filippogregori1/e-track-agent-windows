namespace ActivityTracker.Core;

/// <summary>
/// Motore puro e deterministico: trasforma la sequenza di <see cref="Observation"/> in intervalli confermati
/// (<see cref="ConfirmedSlice"/>) applicando la soglia N con retroattività (docs/DESIGN.md, "Retroattività" e
/// "Tempo e buchi"). Riscrittura riga per riga di <c>ClassificationEngine.swift</c> dell'agente Mac.
///
/// Convenzione: l'osservazione al tempo <c>now</c> descrive l'intervallo reale <c>[lastTick, now)</c>.
/// </summary>
public sealed class ClassificationEngine
{
    public const double MinimumThreshold = 30;
    public const double DefaultThreshold = 180;

    private double _threshold;
    /// <summary>Soglia di inattività N (secondi). Modificabile a runtime; minimo 30 s.</summary>
    public double Threshold
    {
        get => _threshold;
        set => _threshold = Math.Max(value, MinimumThreshold);
    }

    /// <summary>Oltre questo buco tra due tick, l'intero buco è <c>off</c>.</summary>
    public double GapTolerance { get; }
    /// <summary>Un "nuovo input" è tale solo se più recente dell'ultimo noto di almeno questo margine
    /// (assorbe il jitter tra orologio di parete e contatore degli eventi).</summary>
    public double InputTolerance { get; }
    public DayClock DayClock { get; }

    private readonly ISegmentSink _sink;

    // Stato interno
    private DateTimeOffset? _lastTick;
    private bool _sessionOpen;
    private DateTimeOffset? _knownLastInputAt;
    private List<ProvisionalSlice> _queue = [];
    /// <summary>True quando la soglia è scattata per l'inattività corrente: da qui ogni secondo inattivo è confermato subito.</summary>
    private bool _thresholdFired;

    public ClassificationEngine(ISegmentSink sink, double threshold = DefaultThreshold, DayClock? dayClock = null,
                                double gapTolerance = 5, double inputTolerance = 0.5)
    {
        _sink = sink;
        _threshold = Math.Max(threshold, MinimumThreshold);
        DayClock = dayClock ?? new DayClock();
        GapTolerance = gapTolerance;
        InputTolerance = inputTolerance;
    }

    // Stato esposto (report live)

    /// <summary>Coda aperta: secondi dall'ultimo input non ancora confermati (mostrati come uso attivo provvisorio).</summary>
    public IReadOnlyList<ProvisionalSlice> ProvisionalSlices => _queue.ToList();
    public bool IsSessionOpen => _sessionOpen;
    public bool IsIdleConfirmed => _thresholdFired;
    public DateTimeOffset? LastInputAt => _knownLastInputAt;

    public double IdleDuration(DateTimeOffset now) =>
        _knownLastInputAt is { } last ? Math.Max(0, (now - last).TotalSeconds) : 0;

    // Ingresso

    public void Observe(Observation o)
    {
        var now = o.Timestamp;
        if (_lastTick is not { } prev)
        {
            Start(o);
            return;
        }
        if (now <= prev)
        {
            // Orologio fermo o tornato indietro: non si attribuisce nulla, si riparte da qui.
            if (now < prev) _lastTick = now;
            return;
        }

        if ((now - prev).TotalSeconds > GapTolerance)
        {
            // Timer sospeso / sospensione non notificata: la coda si chiude come a fine sessione, il buco è off.
            CloseQueueAsActive();
            Emit(ActivityState.Off, null, prev, now);
            Start(o);
            return;
        }

        if (!o.SessionOpen)
        {
            if (_sessionOpen)
            {
                // Schermo spento: segnale di assenza, vale come soglia scattata. Il tratto dall'ultimo input
                // è riclassificato inattivo con la stessa logica retroattiva, qualunque sia la sua durata.
                // Blocco e sospensione restano invariati: coda < N → attiva.
                if (o.DisplayAsleep) ConfirmQueueAsInactive(); else CloseQueueAsActive();
            }
            Emit(ActivityState.Off, null, prev, now);
            _sessionOpen = false;
            _knownLastInputAt = o.LastInputAt;
            _queue = [];
            _thresholdFired = false;
            _lastTick = now;
            return;
        }

        if (!_sessionOpen)
        {
            // Riapertura: si riparte dall'ultimo input dichiarato, l'intervallo appartiene alla sessione.
            _sessionOpen = true;
            _knownLastInputAt = o.LastInputAt;
            _queue = [];
            _thresholdFired = (now - o.LastInputAt).TotalSeconds >= Threshold;
        }

        var known = _knownLastInputAt ?? o.LastInputAt;
        var inputAt = o.LastInputAt;
        var hadInput = (inputAt - known).TotalSeconds > InputTolerance;

        if (hadInput)
        {
            var splitAt = Epoch.Min(Epoch.Max(inputAt, prev), now);
            if (_thresholdFired)
            {
                EmitInactive(prev, splitAt, o);
            }
            else
            {
                AppendToQueue(prev, splitAt, o);
                CloseQueueAsActive();
            }
            _thresholdFired = false;
            _knownLastInputAt = inputAt;
            _queue = [];
            AppendToQueue(splitAt, now, o);
        }
        else
        {
            if (inputAt > known) _knownLastInputAt = inputAt; // aggiornamento silenzioso entro la tolleranza
            if (_thresholdFired)
            {
                EmitInactive(prev, now, o);
            }
            else
            {
                AppendToQueue(prev, now, o);
                if (IdleDuration(now) >= Threshold)
                {
                    ConfirmQueueAsInactive();
                    _thresholdFired = true;
                }
            }
        }
        _lastTick = now;
    }

    /// <summary>Chiusura ordinata (uscita dell'agente): la soglia non è scattata, la coda è attiva.</summary>
    public void Finish(DateTimeOffset now)
    {
        CloseQueueAsActive();
        _lastTick = now;
    }

    // Interni

    private void Start(Observation o)
    {
        _lastTick = o.Timestamp;
        _sessionOpen = o.SessionOpen;
        _knownLastInputAt = o.LastInputAt;
        _queue = [];
        _thresholdFired = o.SessionOpen && (o.Timestamp - o.LastInputAt).TotalSeconds >= Threshold;
    }

    private void AppendToQueue(DateTimeOffset start, DateTimeOffset end, Observation o)
    {
        if (end <= start) return;
        _queue.Add(new ProvisionalSlice(start, end, o.Foreground, o.Video));
    }

    private void CloseQueueAsActive()
    {
        foreach (var slice in _queue) Emit(ActivityState.Active, slice.Foreground, slice.Start, slice.End);
        _queue = [];
    }

    private void ConfirmQueueAsInactive()
    {
        foreach (var slice in _queue)
        {
            if (slice.Video is { } video) Emit(ActivityState.Passive, video, slice.Start, slice.End);
            else Emit(ActivityState.Unused, null, slice.Start, slice.End);
        }
        _queue = [];
    }

    private void EmitInactive(DateTimeOffset start, DateTimeOffset end, Observation o)
    {
        if (o.Video is { } video) Emit(ActivityState.Passive, video, start, end);
        else Emit(ActivityState.Unused, null, start, end);
    }

    private void Emit(ActivityState state, Subject? subject, DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start) return;
        foreach (var piece in DayClock.Split(start, end))
            _sink.Append(new ConfirmedSlice(piece.Day, piece.Start, piece.End, state, subject));
    }
}
