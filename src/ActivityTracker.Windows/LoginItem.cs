using Microsoft.Win32;

namespace ActivityTracker.Windows;

/// <summary>
/// Avvio all'accesso a Windows: valore in <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> (per utente,
/// senza amministratore). L'installer lo scrive già; l'app lo riscrive al primo avvio e dalle Impostazioni.
/// Windows può disattivarlo da Impostazioni › App › Avvio (o Gestione attività): in quel caso il valore resta ma è
/// spento in <c>StartupApproved\Run</c>, e lo stato lo dice.
/// </summary>
public static class LoginItem
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = Core.AppIdentity.DisplayName;

    private static string Command => $"\"{Environment.ProcessPath}\" --autostart";

    public static bool IsEnabled => Status() == "Attivo";

    public static string Status()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string value) return "Non attivo";
            if (DisabledInStartupApps()) return "Disattivato da Windows (Impostazioni › App › Avvio)";
            return value.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase)
                ? "Attivo"
                : "Attivo, ma punta a un'altra copia dell'app";
        }
        catch (Exception e)
        {
            return "Errore: " + e.Message;
        }
    }

    public static string Enable()
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            run.SetValue(ValueName, Command);
            // Riattiva la voce se l'utente l'aveva spenta in «App di avvio» e ora la richiede esplicitamente.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception e)
        {
            return "Errore: " + e.Message;
        }
        return Status();
    }

    public static string Disable()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception e)
        {
            return "Errore: " + e.Message;
        }
        return Status();
    }

    /// <summary>In <c>StartupApproved\Run</c> il primo byte pari vuol dire attivo, dispari spento.</summary>
    private static bool DisabledInStartupApps()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return approved?.GetValue(ValueName) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
    }
}
