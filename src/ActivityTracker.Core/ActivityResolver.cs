using System.Text.RegularExpressions;

namespace ActivityTracker.Core;

/// <summary>Mappa un <see cref="Subject"/> grezzo al nome dell'attività applicando le regole (priorità: domain › title › app › ripiego).</summary>
public sealed partial class ActivityResolver
{
    public const string UnknownLabel = "Sconosciuto";

    private readonly List<ActivityRule> _domainRules;   // ordinate per pattern più lungo (più specifico) prima
    private readonly List<ActivityRule> _titleRules;
    private readonly Dictionary<string, string> _appRules; // app id (minuscolo) → attività

    public ActivityResolver(IEnumerable<ActivityRule> rules)
    {
        var list = rules.ToList();
        _domainRules = list.Where(r => r.Kind == RuleKind.Domain)
            .Select(r => r with { Pattern = NormalizeHost(r.Pattern) })
            .Where(r => r.Pattern.Length > 0)
            .OrderByDescending(r => r.Pattern.Length).ThenBy(r => r.Pattern, StringComparer.Ordinal)
            .ToList();
        _titleRules = list.Where(r => r.Kind == RuleKind.Title && r.Pattern.Trim().Length > 0)
            .OrderByDescending(r => r.Pattern.Length).ThenBy(r => r.Pattern, StringComparer.Ordinal)
            .ToList();
        _appRules = new Dictionary<string, string>();
        foreach (var r in list.Where(r => r.Kind == RuleKind.App))
        {
            var key = r.Pattern.Trim().ToLowerInvariant();
            if (key.Length > 0 && !_appRules.ContainsKey(key)) _appRules[key] = r.Activity;
        }
    }

    public string Activity(Subject? subject)
    {
        if (subject is null) return UnknownLabel;

        var host = subject.Domain is { } d ? NormalizeHost(d) : "";
        if (host.Length > 0)
        {
            foreach (var rule in _domainRules)
                if (HostMatches(host, rule.Pattern)) return rule.Activity;
        }
        if (!string.IsNullOrEmpty(subject.TitleHint))
        {
            var lower = subject.TitleHint.ToLowerInvariant();
            foreach (var rule in _titleRules)
                if (lower.Contains(rule.Pattern.ToLowerInvariant(), StringComparison.Ordinal)) return rule.Activity;
        }
        if (subject.AppId?.ToLowerInvariant() is { } app && _appRules.TryGetValue(app, out var activity))
            return activity;

        // Ripiego: dominio senza "www.", altrimenti nome app, altrimenti app id.
        if (host.Length > 0) return StripWww(host);
        if (!string.IsNullOrEmpty(subject.AppName)) return subject.AppName;
        if (!string.IsNullOrEmpty(subject.AppId)) return subject.AppId;
        return UnknownLabel;
    }

    // Helpers

    /// <summary>L'host corrisponde se è uguale al pattern o ne è sottodominio.</summary>
    public static bool HostMatches(string host, string pattern) =>
        host == pattern || host.EndsWith("." + pattern, StringComparison.Ordinal);

    public static string NormalizeHost(string raw) => raw.Trim().ToLowerInvariant().Trim('.');

    public static string StripWww(string host) => host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;

    /// <summary>Host di un URL http/https (minuscolo). Altri schemi (about:, chrome://, file:) → null.</summary>
    public static string? HostFromUrl(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        var host = uri.IdnHost;
        return string.IsNullOrEmpty(host) ? null : NormalizeHost(host);
    }

    /// <summary>
    /// Host dal testo della barra degli indirizzi di un browser Windows (letta con UI Automation): Chrome ed Edge
    /// nascondono «https://», quindi «youtube.com/watch?v=x» vale come https. Pagine non web (chrome://, edge://,
    /// about:, file) e testo che non è un indirizzo (una ricerca in corso di digitazione) → null.
    /// </summary>
    public static string? HostFromAddressBar(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.Any(char.IsWhiteSpace)) return null;
        if (t.Contains("://", StringComparison.Ordinal)) return HostFromUrl(t);
        // «about:blank», «mailto:x»: schema senza «//». «localhost:3000» e «host:porta» restano indirizzi.
        if (SchemeOnly().IsMatch(t)) return null;
        var host = HostFromUrl("https://" + t);
        if (host is null || !HostShape().IsMatch(host)) return null;
        if (!host.Contains('.') && host != "localhost") return null;
        return host;
    }

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*:(?!\d)")]
    private static partial Regex SchemeOnly();

    [GeneratedRegex(@"^[a-z0-9\-.\[\]:]+$")]
    private static partial Regex HostShape();
}
