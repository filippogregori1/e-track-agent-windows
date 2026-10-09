namespace ActivityTracker.Core;

/// <summary>Riga della tabella <c>segments</c>: intervallo contiguo con la stessa chiave (state, bundle_id, domain, title_hint).</summary>
public sealed class Segment
{
    public long? Id { get; set; }
    public string Day { get; set; }
    /// <summary>Epoch in secondi.</summary>
    public double Start { get; set; }
    public double End { get; set; }
    public ActivityState State { get; set; }
    /// <summary>Colonna <c>bundle_id</c>: su Windows il nome dell'eseguibile in minuscolo.</summary>
    public string? AppId { get; set; }
    public string? AppName { get; set; }
    public string? Domain { get; set; }
    public string? TitleHint { get; set; }

    public Segment(string day, double start, double end, ActivityState state, string? appId = null, string? appName = null,
                   string? domain = null, string? titleHint = null, long? id = null)
    {
        Id = id;
        Day = day;
        Start = start;
        End = end;
        State = state;
        AppId = appId;
        AppName = appName;
        Domain = domain;
        TitleHint = titleHint;
    }

    public static Segment From(ConfirmedSlice slice) =>
        new(slice.Day, Epoch.Seconds(slice.Start), Epoch.Seconds(slice.End), slice.State,
            slice.Subject?.AppId, slice.Subject?.AppName, slice.Subject?.Domain, slice.Subject?.TitleHint);

    public Segment Copy() => new(Day, Start, End, State, AppId, AppName, Domain, TitleHint, Id);

    public double Duration => Math.Max(0, End - Start);
    public DateTimeOffset StartDate => Epoch.FromSeconds(Start);
    public DateTimeOffset EndDate => Epoch.FromSeconds(End);

    public Subject? Subject =>
        AppId is null && AppName is null && Domain is null && TitleHint is null
            ? null
            : new Subject(AppId, AppName, Domain, TitleHint);

    /// <summary>Chiave di coalescenza (state, bundle_id, domain, title_hint; il giorno non può differire).</summary>
    public bool SameKey(ConfirmedSlice slice) =>
        Day == slice.Day
        && State == slice.State
        && AppId == slice.Subject?.AppId
        && Domain == slice.Subject?.Domain
        && TitleHint == slice.Subject?.TitleHint;
}
