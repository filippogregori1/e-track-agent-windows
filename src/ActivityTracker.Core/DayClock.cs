using System.Globalization;

namespace ActivityTracker.Core;

/// <summary>Fuso orario iniettabile: definisce il "giorno locale" e spezza gli intervalli a mezzanotte.</summary>
public sealed class DayClock
{
    public TimeZoneInfo TimeZone { get; }

    public DayClock(TimeZoneInfo? timeZone = null)
    {
        TimeZone = timeZone ?? TimeZoneInfo.Local;
    }

    /// <summary>Fuso per id IANA (es. <c>Europe/Rome</c>), con ripiego sull'id Windows equivalente.</summary>
    public static TimeZoneInfo FindZone(string ianaId, string windowsId)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(ianaId); }
        catch (Exception) { }
        try { return TimeZoneInfo.FindSystemTimeZoneById(windowsId); }
        catch (Exception) { }
        return TimeZoneInfo.Local;
    }

    public static readonly TimeZoneInfo Rome = FindZone("Europe/Rome", "W. Europe Standard Time");

    /// <summary>'yyyy-MM-dd' nel fuso del calendario.</summary>
    public string DayString(DateTimeOffset date) =>
        TimeZoneInfo.ConvertTime(date, TimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public DateTimeOffset StartOfDay(DateTimeOffset date) =>
        Midnight(TimeZoneInfo.ConvertTime(date, TimeZone).Date);

    /// <summary>Prima mezzanotte strettamente successiva a <paramref name="date"/>.</summary>
    public DateTimeOffset NextMidnight(DateTimeOffset date) =>
        Midnight(TimeZoneInfo.ConvertTime(date, TimeZone).Date.AddDays(1));

    /// <summary>L'istante della mezzanotte locale del giorno dato (se per l'ora legale non esiste, il primo istante
    /// valido dopo).</summary>
    private DateTimeOffset Midnight(DateTime localDate)
    {
        var d = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
        while (TimeZone.IsInvalidTime(d)) d = d.AddMinutes(30);
        return new DateTimeOffset(d, TimeZone.GetUtcOffset(d));
    }

    /// <summary>Spezza <c>[start, end)</c> in pezzi che non attraversano la mezzanotte. Intervalli vuoti → nessun pezzo.</summary>
    public List<(string Day, DateTimeOffset Start, DateTimeOffset End)> Split(DateTimeOffset start, DateTimeOffset end)
    {
        var pieces = new List<(string, DateTimeOffset, DateTimeOffset)>();
        var cursor = start;
        while (cursor < end)
        {
            var pieceEnd = Epoch.Min(NextMidnight(cursor), end);
            pieces.Add((DayString(cursor), cursor, pieceEnd));
            cursor = pieceEnd;
        }
        return pieces;
    }
}
