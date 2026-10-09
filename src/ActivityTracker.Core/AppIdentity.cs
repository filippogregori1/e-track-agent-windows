namespace ActivityTracker.Core;

/// <summary>
/// Unico punto da cambiare (insieme alle variabili in testa a <c>scripts/build.sh</c> e a <c>installer/activity-tracker.nsi</c>)
/// per rinominare l'applicazione. Nome e identificativo sono quelli dell'agente Mac.
/// </summary>
public static class AppIdentity
{
    /// <summary>Nome visualizzato (icona nella tray, finestre, cartella di installazione).</summary>
    public const string DisplayName = "activity-tracker";
    /// <summary>Identificativo dell'agente: prefisso delle credenziali e del soggetto «Non tracciato».</summary>
    public const string Identifier = "it.equipe.activitytracker";
    /// <summary>Cartella dentro <c>%LOCALAPPDATA%</c>.</summary>
    public const string DataFolderName = "activity-tracker";
    /// <summary>Nome del file SQLite dentro la cartella dati.</summary>
    public const string DatabaseFileName = "activity.sqlite";
    /// <summary>Versione dichiarata a equipe-track (<c>X-Agente-Versione</c>): quella del Mac più la piattaforma.</summary>
    public const string AgentVersion = "0.3.0-windows";

    /// <summary><c>%LOCALAPPDATA%\activity-tracker</c></summary>
    public static string DataDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir)) baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        return Path.Combine(baseDir, DataFolderName);
    }

    /// <summary>Percorso completo del database.</summary>
    public static string DatabasePath() => Path.Combine(DataDirectory(), DatabaseFileName);
}
