using ActivityTracker.Core;
using ActivityTracker.Windows.Sync;
using ActivityTracker.Windows.UI;

namespace ActivityTracker.Windows;

internal static class Program
{
    private const string MutexName = @"Local\" + AppIdentity.Identifier;
    private const string ShowEventName = @"Local\" + AppIdentity.Identifier + ".show";
    private const string QuitEventName = @"Local\" + AppIdentity.Identifier + ".quit";

    [STAThread]
    private static int Main(string[] args)
    {
        // Comandi per l'installer: chiusura ordinata della copia attiva (flush e ultimo invio) prima di aggiornare o
        // disinstallare, e rimozione delle credenziali alla disinstallazione completa.
        if (args.Contains("--quit")) return QuitRunningInstance();
        if (args.Contains("--forget-credentials"))
        {
            try { EquipeCredentials.Delete(new CredentialManagerStore()); } catch (Exception) { return 1; }
            return 0;
        }

        // Una sola copia per utente: una seconda apertura (menu Start, collegamento) mostra il report di quella già attiva.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (!first)
        {
            // Chi riceve il segnale non ha il diritto di prendersi il primo piano: glielo cede questa copia.
            Native.Win32.AllowSetForegroundWindow(Native.Win32.ASFW_ANY);
            Signal(ShowEventName);
            return 0;
        }

        Application.ThreadException += (_, e) => Log.Write("[Errore] " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("[Errore fatale] " + e.ExceptionObject);
        ApplicationConfiguration.Initialize();
        // Prima di qualunque await: le continuazioni del sync devono tornare su questo thread, come @MainActor sul Mac.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        var context = new TrayApp(autostart: args.Contains("--autostart"), showEvent, quitEvent);
        Application.Run(context);
        context.Dispose();
        // Rilasciato solo dopo flush e ultimo invio: chi aspetta (--quit, l'installer) sa che i dati sono al sicuro.
        mutex.ReleaseMutex();
        return 0;
    }

    private static void Signal(string name)
    {
        try
        {
            using var e = EventWaitHandle.OpenExisting(name);
            e.Set();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Chiede alla copia attiva di uscire e aspetta (al più 20 s) che lo faccia. 0 = nessuna copia attiva o uscita.</summary>
    private static int QuitRunningInstance()
    {
        if (!Mutex.TryOpenExisting(MutexName, out var existing)) return 0;
        using (existing)
        {
            Signal(QuitEventName);
            try
            {
                if (existing.WaitOne(TimeSpan.FromSeconds(20))) existing.ReleaseMutex();
                else return 1;
            }
            catch (AbandonedMutexException)
            {
            }
        }
        return 0;
    }
}
