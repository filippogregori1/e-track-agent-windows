namespace ActivityTracker.Core;

/// <summary>Riga del report: attività con tempo attivo + passivo, oppure "Attivo senza utilizzo".</summary>
/// <param name="Total">Attivo + passivo (secondi).</param>
/// <param name="PerMille">Quota in millesimi (es. 336 → 33,6%).</param>
public sealed record ReportRow(string Name, double Total, double Passive, int PerMille, bool IsUnused);

/// <param name="SessionTotal">Somma delle righe = sessione aperta.</param>
/// <param name="OffTotal">Tempo <c>off</c> registrato nel giorno (fuori dal 100%).</param>
public sealed record Report(string Day, IReadOnlyList<ReportRow> Rows, double SessionTotal, double OffTotal)
{
    public bool IsEmpty => Rows.Count == 0;
    public static readonly Report Empty = new("", [], 0, 0);
}

/// <summary>Aggrega i segmenti del giorno (più la coda provvisoria) in righe ordinate con percentuali esatte.</summary>
public sealed class ReportBuilder
{
    public const string UnusedLabel = "Attivo senza utilizzo";

    public ActivityResolver Resolver { get; }
    public DayClock DayClock { get; }

    public ReportBuilder(ActivityResolver resolver, DayClock? dayClock = null)
    {
        Resolver = resolver;
        DayClock = dayClock ?? new DayClock();
    }

    public Report Build(string day, IEnumerable<Segment> segments, IEnumerable<ProvisionalSlice>? provisional = null)
    {
        var totals = new Dictionary<string, (double Total, double Passive)>();
        double unused = 0, off = 0;

        void Add(string name, double d, bool passive)
        {
            totals.TryGetValue(name, out var e);
            e.Total += d;
            if (passive) e.Passive += d;
            totals[name] = e;
        }

        foreach (var seg in segments.Where(s => s.Day == day && s.Duration > 0))
        {
            switch (seg.State)
            {
                case ActivityState.Active: Add(Resolver.Activity(seg.Subject), seg.Duration, false); break;
                case ActivityState.Passive: Add(Resolver.Activity(seg.Subject), seg.Duration, true); break;
                case ActivityState.Unused: unused += seg.Duration; break;
                case ActivityState.Off: off += seg.Duration; break;
            }
        }
        // Coda non confermata: uso attivo provvisorio sull'app in primo piano, solo la parte del giorno richiesto.
        foreach (var slice in provisional ?? [])
            foreach (var piece in DayClock.Split(slice.Start, slice.End).Where(p => p.Day == day))
                Add(Resolver.Activity(slice.Foreground), (piece.End - piece.Start).TotalSeconds, false);

        var rows = totals.Where(kv => kv.Value.Total > 0)
            .Select(kv => new ReportRow(kv.Key, kv.Value.Total, kv.Value.Passive, 0, false))
            .ToList();
        rows.Sort((a, b) => a.Total != b.Total ? b.Total.CompareTo(a.Total) : string.CompareOrdinal(a.Name, b.Name));
        if (unused > 0) rows.Add(new ReportRow(UnusedLabel, unused, 0, 0, true));

        var shares = LargestRemainder.Apportion(rows.Select(r => new LargestRemainder.Entry(r.Total, r.Name)).ToList());
        for (var i = 0; i < rows.Count; i++) rows[i] = rows[i] with { PerMille = shares[i] };

        return new Report(day, rows, rows.Sum(r => r.Total), off);
    }
}
