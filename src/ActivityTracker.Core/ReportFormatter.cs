namespace ActivityTracker.Core;

/// <summary>
/// Formato testuale italiano del report, identico al Mac:
/// <code>
/// YouTube   33,6%  2h35  (di cui passivo 1h50)
/// Attivo senza utilizzo   22,0%  1h42
/// Totale 100,0% · Fuori sessione 3h10
/// </code>
/// Spaziatura fissa: nome + 3 spazi + percentuale + 2 spazi + durata [+ 2 spazi + nota passivo].
/// </summary>
public static class ReportFormatter
{
    public const string EmptyMessage = "Nessun dato per oggi";

    /// <summary>336 → "33,6%"</summary>
    public static string Percent(int perMille)
    {
        var p = Math.Max(0, perMille);
        return $"{p / 10},{p % 10}%";
    }

    /// <summary>≥ 1 h → "2h05"; &lt; 1 h → "35m"; &lt; 1 min → "&lt;1m".</summary>
    public static string Duration(double seconds)
    {
        var total = (long)Math.Floor(Math.Max(0, seconds));
        if (total < 60) return "<1m";
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        return hours >= 1 ? $"{hours}h{minutes:00}" : $"{minutes}m";
    }

    /// <summary>"(di cui passivo 1h50)" solo se passivo ≥ 1 min, altrimenti null.</summary>
    public static string? PassiveNote(double seconds) => seconds >= 60 ? $"(di cui passivo {Duration(seconds)})" : null;

    public static string Line(ReportRow row)
    {
        var s = $"{row.Name}   {Percent(row.PerMille)}  {Duration(row.Total)}";
        if (!row.IsUnused && PassiveNote(row.Passive) is { } note) s += "  " + note;
        return s;
    }

    public static string TotalLine(Report report)
    {
        var s = "Totale " + Percent(report.Rows.Sum(r => r.PerMille));
        if (report.OffTotal > 0) s += " · Fuori sessione " + Duration(report.OffTotal);
        return s;
    }

    /// <summary>Report completo in testo.</summary>
    public static string Text(Report report)
    {
        if (report.IsEmpty)
        {
            var s = EmptyMessage;
            if (report.OffTotal > 0) s += "\nFuori sessione " + Duration(report.OffTotal);
            return s;
        }
        var lines = report.Rows.Select(Line).ToList();
        lines.Add(TotalLine(report));
        return string.Join("\n", lines);
    }
}
