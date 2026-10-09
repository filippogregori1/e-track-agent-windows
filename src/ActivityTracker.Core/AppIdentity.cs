namespace ActivityTracker.Core;

/// <summary>
/// Unico punto da cambiare (insieme alle variabili in testa a <c>scripts/build.sh</c> e a <c>installer/activity-tracker.nsi</c>)
/// per rinominare l'applicazione. Identificativo e nome interno sono quelli dell'agente Mac; il nome visibile su
/// Windows è «e-track agent».
/// </summary>
public static class AppIdentity
{
    /// <summary>Nome mostrato all'utente: icona nella tray, finestre, installer, menu Start, «App installate».</summary>
    public const string DisplayName = "e-track agent";
    /// <summary>Nome interno, mai mostrato come nome del prodotto: eseguibile (<c>activity-tracker.exe</c>), cartella di
    /// installazione, valore di avvio automatico, chiave di disinstallazione. Resta quello dell'agente Mac, così un
    /// aggiornamento ritrova l'installazione esistente.</summary>
    public const string InternalName = "activity-tracker";
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
