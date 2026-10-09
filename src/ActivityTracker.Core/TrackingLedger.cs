namespace ActivityTracker.Core;

public enum ServerStateKind
{
    /// <summary>Sessione aperta e attiva: si traccia, e i tratti portano id e giorno della sessione.</summary>
    Tracking,
    /// <summary>Sessione aperta ma in pausa (o comunque <c>traccia: false</c>): non si traccia e non si manda nulla,
    /// nemmeno <c>fuori_sessione</c>, perché quel tempo è dentro la sessione.</summary>
    Paused,
    /// <summary>Nessuna sessione aperta: si manda solo per quanto il PC era in uso (<c>fuori_sessione</c>), mai cosa.</summary>
    None,
}

/// <summary>Che cosa ha risposto <c>GET /api/agente/sessione</c> in un istante.</summary>
public sealed record ServerState(ServerStateKind Kind, string? Session = null, string? Giorno = null)
{
    public static readonly ServerState None = new(ServerStateKind.None);
    public static ServerState Tracking(string session, string giorno) => new(ServerStateKind.Tracking, session, giorno);
    public static ServerState Paused(string session, string giorno) => new(ServerStateKind.Paused, session, giorno);

    public static ServerState From(RispostaSessione r) =>
        r.Sessione is not { } s ? None : r.Traccia ? Tracking(s.Id, s.Giorno) : Paused(s.Id, s.Giorno);

    public bool IsTracking => Kind == ServerStateKind.Tracking;
}

/// <summary>
/// Riga della tabella <c>tracking_ledger</c>: un tratto di tempo in cui le letture consecutive del server hanno dato
/// lo stesso stato. Il tempo fra due letture discordi, o dopo un buco di letture (PC sospeso, rete assente), non
/// appartiene a nessun intervallo: lì non si sa, e non si manda nulla.
/// </summary>
public sealed record LedgerInterval(string Kind, string? SessionId, string? Giorno, double Start, double End)
{
    public static LedgerInterval Of(ServerState state, double start, double end) => state.Kind switch
    {
        ServerStateKind.Tracking => new("tracking", state.Session, state.Giorno, start, end),
        ServerStateKind.Paused => new("paused", state.Session, state.Giorno, start, end),
        _ => new("none", null, null, start, end),
    };

    public ServerState State => Kind switch
    {
        "tracking" => ServerState.Tracking(SessionId ?? "", Giorno ?? ""),
        "paused" => ServerState.Paused(SessionId ?? "", Giorno ?? ""),
        _ => ServerState.None,
    };

    public DateTimeOffset StartDate => Epoch.FromSeconds(Start);
    public DateTimeOffset EndDate => Epoch.FromSeconds(End);
}

/// <summary>Registro delle letture di <c>GET /api/agente/sessione</c>. Logica pura: la persistenza la fa <see cref="TrackingSync"/>.</summary>
public sealed class TrackingLedger
{
    /// <summary>Oltre questo buco fra due letture lo stato non si considera continuo (letture ogni 60 s).</summary>
    public const double MaxGap = 150;
    /// <summary>Quanto indietro si tiene il registro (e quindi quali giornate si possono ancora reinviare).</summary>
    public const double Retention = 8 * 86_400;

    private List<LedgerInterval> _intervals;
    public IReadOnlyList<LedgerInterval> Intervals => _intervals;

    public TrackingLedger(IEnumerable<LedgerInterval>? intervals = null)
    {
        _intervals = (intervals ?? []).OrderBy(i => i.Start).ToList();
    }

    public TrackingLedger Clone() => new(_intervals);

    /// <summary>Registra lo stato letto all'istante <paramref name="t"/>: allunga l'ultimo intervallo se lo stato è lo
    /// stesso e il buco non supera <see cref="MaxGap"/>, altrimenti ne apre uno nuovo (lungo zero, finché la lettura
    /// dopo non lo allunga). Un istante anteriore all'ultima lettura (orologio tornato indietro) viene ignorato.</summary>
    public void Record(ServerState state, DateTimeOffset t)
    {
        var ts = Epoch.Seconds(t);
        if (_intervals.Count > 0)
        {
            var last = _intervals[^1];
            if (ts < last.End) return;
            if (last.State == state && ts - last.End <= MaxGap)
            {
                _intervals[^1] = last with { End = ts };
                return;
            }
        }
        _intervals.Add(LedgerInterval.Of(state, ts, ts));
    }

    /// <summary>Lo stato dell'ultima lettura, se è abbastanza recente da valere ancora; altrimenti null (non si sa).</summary>
    public ServerState? Current(DateTimeOffset now)
    {
        if (_intervals.Count == 0) return null;
        var last = _intervals[^1];
        return Epoch.Seconds(now) - last.End <= MaxGap ? last.State : null;
    }

    /// <summary>Finestre in cui il contenuto (app, dominio, titolo) può arrivare sul disco: gli intervalli
    /// <c>tracking</c>, con l'ultimo prolungato di <see cref="MaxGap"/> se è ancora quello corrente.</summary>
    public List<TimeWindow> ContentWindows(DateTimeOffset now)
    {
        var output = new List<TimeWindow>();
        for (var i = 0; i < _intervals.Count; i++)
        {
            var iv = _intervals[i];
            if (!iv.State.IsTracking) continue;
            var end = iv.End;
            if (i == _intervals.Count - 1 && Epoch.Seconds(now) - iv.End <= MaxGap) end += MaxGap;
            output.Add(new TimeWindow(iv.StartDate, Epoch.FromSeconds(Math.Max(end, iv.Start))));
        }
        return output;
    }

    /// <summary>Il server ha detto che <paramref name="session"/> era già chiusa a <paramref name="t"/> (chiusura
    /// d'ufficio anteriore alle letture): il tracciamento di quella sessione finisce lì. True se il registro è cambiato.</summary>
    public bool Truncate(string session, DateTimeOffset t)
    {
        var ts = Epoch.Seconds(t);
        var changed = false;
        var kept = new List<LedgerInterval>();
        foreach (var iv in _intervals)
        {
            if (iv.State.IsTracking && iv.SessionId == session && iv.End > ts)
            {
                changed = true;
                if (iv.Start < ts) kept.Add(iv with { End = ts });
                continue;
            }
            kept.Add(iv);
        }
        _intervals = kept;
        return changed;
    }

    /// <summary>Toglie gli intervalli finiti prima di <paramref name="date"/>.</summary>
    public void Prune(DateTimeOffset date)
    {
        var ts = Epoch.Seconds(date);
        _intervals.RemoveAll(i => i.End < ts);
    }

    /// <summary>Le giornate di cui il registro sa qualcosa: quella della sessione per <c>tracking</c>/<c>paused</c>, e
    /// quelle (nell'orologio del server) attraversate dagli intervalli <c>none</c>.</summary>
    public HashSet<string> Giorni(DayClock clock)
    {
        var output = new HashSet<string>();
        foreach (var iv in _intervals)
        {
            var s = iv.State;
            if (s.Kind is ServerStateKind.Tracking or ServerStateKind.Paused)
            {
                output.Add(s.Giorno ?? "");
            }
            else
            {
                output.Add(clock.DayString(iv.StartDate));
                foreach (var piece in clock.Split(iv.StartDate, iv.EndDate)) output.Add(piece.Day);
            }
        }
        return output;
    }
}
