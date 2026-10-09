namespace ActivityTracker.Core;

/// <summary>Intervallo di tempo chiuso a sinistra, aperto a destra.</summary>
public readonly record struct TimeWindow(DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// Sta fra il motore di classificazione e il coalescer: fuori dalle finestre in cui una sessione di lavoro è aperta
/// e attiva, toglie a ogni intervallo confermato il contenuto (app, dominio, titolo) prima che arrivi sul disco.
/// Stato e durata passano invariati: la misura non cambia, cambia solo cosa si conserva.
///
/// Le finestre arrivano da <see cref="TrackingLedger.ContentWindows"/> dopo ogni lettura di <c>GET /api/agente/sessione</c>;
/// senza letture (agente non configurato, server muto) non c'è finestra e nessun contenuto viene conservato.
/// </summary>
public sealed class SessionGate : ISegmentSink
{
    /// <summary>Il soggetto che sostituisce quello vero fuori sessione: nel report locale compare come «Non tracciato».
    /// Ha un id suo perché il coalescer non lo fonda con un'app vera priva di id, dominio e titolo.</summary>
    public static readonly Subject UntrackedSubject = new(AppIdentity.Identifier + ".untracked", "Non tracciato");

    private readonly ISegmentSink _next;
    private List<TimeWindow> _windows = [];

    /// <summary>Finestre in cui il contenuto si conserva, in ordine di inizio.</summary>
    public IReadOnlyList<TimeWindow> Windows
    {
        get => _windows;
        set => _windows = value.OrderBy(w => w.Start).ToList();
    }

    public SessionGate(ISegmentSink next)
    {
        _next = next;
    }

    public void Append(ConfirmedSlice slice)
    {
        if (slice.End <= slice.Start || slice.Subject is null)
        {
            _next.Append(slice);
            return;
        }
        var cursor = slice.Start;
        foreach (var w in _windows.Where(w => w.End > slice.Start && w.Start < slice.End))
        {
            var a = Epoch.Max(w.Start, cursor);
            var b = Epoch.Min(w.End, slice.End);
            if (b <= a) continue;
            if (a > cursor) Forward(slice, cursor, a, keep: false);
            Forward(slice, a, b, keep: true);
            cursor = b;
        }
        if (cursor < slice.End) Forward(slice, cursor, slice.End, keep: false);
    }

    private void Forward(ConfirmedSlice slice, DateTimeOffset start, DateTimeOffset end, bool keep) =>
        _next.Append(slice with { Start = start, End = end, Subject = keep ? slice.Subject : UntrackedSubject });
}
