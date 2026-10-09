namespace ActivityTracker.Core;

/// <summary>Stato di un secondo misurato. Ogni secondo cade in uno solo di questi.</summary>
public enum ActivityState
{
    Active,
    Passive,
    Unused,
    Off,
}

public static class ActivityStates
{
    public static readonly ActivityState[] All = [ActivityState.Active, ActivityState.Passive, ActivityState.Unused, ActivityState.Off];

    /// <summary>Valore salvato nella colonna <c>state</c> (identico all'agente Mac).</summary>
    public static string Raw(this ActivityState s) => s switch
    {
        ActivityState.Active => "active",
        ActivityState.Passive => "passive",
        ActivityState.Unused => "unused",
        _ => "off",
    };

    public static ActivityState Parse(string raw) => raw switch
    {
        "active" => ActivityState.Active,
        "passive" => ActivityState.Passive,
        "unused" => ActivityState.Unused,
        _ => ActivityState.Off,
    };
}

/// <summary>
/// Dati grezzi di ciò che era in primo piano (o teneva acceso lo schermo).
/// L'attività (es. "YouTube") viene calcolata solo al momento del report.
/// <para><c>AppId</c> è l'identità dell'app: sul Mac il bundle id, su Windows il nome dell'eseguibile in minuscolo
/// (es. <c>chrome.exe</c>). Nel database resta nella colonna <c>bundle_id</c>, come sul Mac.</para>
/// </summary>
public sealed record Subject(string? AppId = null, string? AppName = null, string? Domain = null, string? TitleHint = null);

/// <summary>
/// Osservazione grezza raccolta dal campionatore a ogni tick.
/// <c>SessionOpen</c>: PC sveglio, schermo acceso, utente sbloccato, niente salvaschermo, sessione console attiva.
/// <c>LastInputAt</c>: istante dell'ultimo input (tastiera/mouse/touch). <c>Video</c>: chi tiene acceso lo schermo
/// con un video, se c'è. <c>DisplayAsleep</c>: schermo spento; se è questo a chiudere la sessione, il tratto
/// dall'ultimo input è inattivo anche sotto soglia (vedi <see cref="ClassificationEngine"/>).
/// </summary>
public sealed record Observation(
    DateTimeOffset Timestamp,
    bool SessionOpen,
    DateTimeOffset LastInputAt,
    Subject? Foreground,
    Subject? Video,
    bool DisplayAsleep = false);

/// <summary>Intervallo confermato dal motore, già spezzato in modo da appartenere a un solo giorno locale.</summary>
public sealed record ConfirmedSlice(string Day, DateTimeOffset Start, DateTimeOffset End, ActivityState State, Subject? Subject)
{
    public double Duration => (End - Start).TotalSeconds;
}

/// <summary>Secondo in coda, non ancora confermato (dall'ultimo input in poi).</summary>
public sealed record ProvisionalSlice(DateTimeOffset Start, DateTimeOffset End, Subject? Foreground, Subject? Video)
{
    public double Duration => (End - Start).TotalSeconds;
}

/// <summary>Destinazione degli intervalli confermati.</summary>
public interface ISegmentSink
{
    void Append(ConfirmedSlice slice);
}

/// <summary>Conversioni fra istanti ed epoch in secondi (la forma dei campi <c>start</c>/<c>end</c> nel database).</summary>
public static class Epoch
{
    public static double Seconds(DateTimeOffset d) =>
        (d.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;

    public static DateTimeOffset FromSeconds(double s) =>
        new(DateTimeOffset.UnixEpoch.UtcTicks + (long)Math.Round(s * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero), TimeSpan.Zero);

    public static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    public static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <summary>Arrotondamento di Swift <c>.rounded()</c>: metà lontano da zero.</summary>
    public static long Round(double x) => (long)Math.Round(x, MidpointRounding.AwayFromZero);
}
