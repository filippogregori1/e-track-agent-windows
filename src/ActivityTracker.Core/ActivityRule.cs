namespace ActivityTracker.Core;

public enum RuleKind
{
    Domain,
    App,
    Title,
}

public static class RuleKinds
{
    public static readonly RuleKind[] All = [RuleKind.Domain, RuleKind.App, RuleKind.Title];

    public static string Raw(this RuleKind k) => k switch
    {
        RuleKind.Domain => "domain",
        RuleKind.App => "app",
        _ => "title",
    };

    public static RuleKind Parse(string raw) => raw switch
    {
        "domain" => RuleKind.Domain,
        "app" => RuleKind.App,
        _ => RuleKind.Title,
    };

    public static string Label(this RuleKind k) => k switch
    {
        RuleKind.Domain => "Dominio",
        RuleKind.App => "App (eseguibile)",
        _ => "Titolo",
    };
}

/// <summary>Riga della tabella <c>activity_rules</c>.</summary>
public sealed record ActivityRule(RuleKind Kind, string Pattern, string Activity, long? Id = null)
{
    /// <summary>
    /// Regole iniziali: le stesse del Mac. Le regole «app» del Mac usano bundle id; qui l'equivalente è il nome
    /// dell'eseguibile (FaceTime non esiste su Windows).
    /// </summary>
    public static readonly IReadOnlyList<ActivityRule> Seed =
    [
        new(RuleKind.Domain, "youtube.com", "YouTube"),
        new(RuleKind.Domain, "youtu.be", "YouTube"),
        new(RuleKind.Domain, "youtube-nocookie.com", "YouTube"),
        new(RuleKind.Domain, "netflix.com", "Netflix"),
        new(RuleKind.Domain, "primevideo.com", "Prime Video"),
        new(RuleKind.Domain, "twitch.tv", "Twitch"),
        new(RuleKind.Domain, "meet.google.com", "Google Meet"),
        new(RuleKind.Domain, "mail.google.com", "Gmail"),
        new(RuleKind.Domain, "docs.google.com", "Google Docs"),
        new(RuleKind.Domain, "github.com", "GitHub"),
        new(RuleKind.Domain, "claude.ai", "Claude"),
        new(RuleKind.Domain, "chatgpt.com", "ChatGPT"),
        new(RuleKind.Domain, "instagram.com", "Instagram"),
        new(RuleKind.Domain, "facebook.com", "Facebook"),
        new(RuleKind.Domain, "x.com", "X"),
        new(RuleKind.Domain, "twitter.com", "X"),
        new(RuleKind.Domain, "linkedin.com", "LinkedIn"),
        new(RuleKind.Domain, "web.whatsapp.com", "WhatsApp"),
        new(RuleKind.App, "zoom.exe", "Zoom"),
        new(RuleKind.App, "ms-teams.exe", "Teams"),
        new(RuleKind.App, "teams.exe", "Teams"),
        new(RuleKind.Title, "YouTube", "YouTube"),
    ];
}
