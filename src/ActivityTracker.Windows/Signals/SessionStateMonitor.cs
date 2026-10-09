using System.Runtime.InteropServices;
using ActivityTracker.Windows.Native;

namespace ActivityTracker.Windows.Signals;

/// <summary>
/// Stato della sessione: sveglio ∧ schermo acceso ∧ sbloccato ∧ niente salvaschermo ∧ sessione utente attiva —
/// la stessa definizione del Mac. Una finestra nascosta riceve i messaggi di Windows:
/// <list type="bullet">
/// <item>sospensione / ripresa: <c>WM_POWERBROADCAST</c> (<c>PBT_APMSUSPEND</c>, <c>PBT_APMRESUME*</c>);</item>
/// <item>schermo spento / acceso: <c>GUID_CONSOLE_DISPLAY_STATE</c> (0 spento, 1 acceso, 2 attenuato = acceso);</item>
/// <item>blocco / sblocco, cambio utente, Desktop remoto: <c>WM_WTSSESSION_CHANGE</c>;</item>
/// <item>spegnimento / disconnessione: <c>WM_QUERYENDSESSION</c> / <c>WM_ENDSESSION</c>.</item>
/// </list>
/// Il salvaschermo non ha un evento: lo si legge a ogni tick (<see cref="Poll"/>).
/// </summary>
public sealed class SessionStateMonitor : NativeWindow, IDisposable
{
    public bool IsAsleep { get; private set; }
    public bool IsDisplayAsleep { get; private set; }
    public bool IsLocked { get; private set; }
    public bool IsScreensaverActive { get; private set; }
    public bool IsConsoleActive { get; private set; } = true;

    /// <summary>Chiamato appena la sessione si chiude (sospensione, blocco, schermo spento, salvaschermo, cambio utente).</summary>
    public event Action? SessionWillClose;
    /// <summary>Chiamato quando la sessione torna aperta.</summary>
    public event Action? SessionDidOpen;
    /// <summary>Ripresa dalla sospensione.</summary>
    public event Action? DidWake;
    /// <summary>Sblocco dello schermo.</summary>
    public event Action? DidUnlock;
    /// <summary>Spegnimento, riavvio o disconnessione: l'ultima occasione per salvare e inviare. L'argomento è falso
    /// quando a chiudere l'app è Restart Manager (un installer, un aggiornamento): il PC non si spegne e la sessione su
    /// equipe-track resta aperta.</summary>
    public event Action<bool>? WillPowerOff;

    private IntPtr _displayNotification;

    public SessionStateMonitor()
    {
        CreateHandle(new CreateParams { Caption = "activity-tracker-session", Parent = IntPtr.Zero });
        var guid = Win32.GUID_CONSOLE_DISPLAY_STATE;
        _displayNotification = Win32.RegisterPowerSettingNotification(Handle, ref guid, 0);
        Win32.WTSRegisterSessionNotification(Handle, Win32.NOTIFY_FOR_THIS_SESSION);
        IsLocked = Win32.IsSessionLocked() ?? false;
        IsConsoleActive = Win32.IsSessionActive() ?? true;
        IsScreensaverActive = Win32.IsScreensaverRunning();
    }

    public bool IsSessionOpen => !IsAsleep && !IsDisplayAsleep && !IsLocked && !IsScreensaverActive && IsConsoleActive;

    public string Summary =>
        IsAsleep ? "in sospensione"
        : IsDisplayAsleep ? "schermo spento"
        : IsLocked ? "schermo bloccato"
        : IsScreensaverActive ? "salvaschermo"
        : !IsConsoleActive ? "altro utente o sessione scollegata"
        : "aperta";

    /// <summary>Stati senza evento, letti a ogni tick.</summary>
    public void Poll()
    {
        var screensaver = Win32.IsScreensaverRunning();
        if (screensaver != IsScreensaverActive) Update(() => IsScreensaverActive = screensaver);
    }

    /// <summary>Esito dell'ultimo spegnimento gestito: la finestra può rilasciare il blocco solo dopo l'invio.</summary>
    private bool _shutdownHandled;

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Win32.WM_POWERBROADCAST:
                HandlePower(m);
                break;
            case Win32.WM_WTSSESSION_CHANGE:
                switch (m.WParam.ToInt32())
                {
                    case Win32.WTS_SESSION_LOCK: Update(() => IsLocked = true); break;
                    case Win32.WTS_SESSION_UNLOCK:
                        Update(() => IsLocked = false);
                        DidUnlock?.Invoke();
                        break;
                    case Win32.WTS_CONSOLE_CONNECT or Win32.WTS_REMOTE_CONNECT or Win32.WTS_CONSOLE_DISCONNECT or Win32.WTS_REMOTE_DISCONNECT:
                        var active = Win32.IsSessionActive() ?? true;
                        Update(() => IsConsoleActive = active);
                        break;
                }
                break;
            case Win32.WM_QUERYENDSESSION:
                // Si lascia spegnere; il motivo compare se l'invio finale richiede qualche secondo.
                Win32.ShutdownBlockReasonCreate(Handle, "Invio degli ultimi dati a equipe-track…");
                m.Result = 1;
                return;
            case Win32.WM_ENDSESSION:
                if (m.WParam != IntPtr.Zero && !_shutdownHandled)
                {
                    _shutdownHandled = true;
                    const long ENDSESSION_CLOSEAPP = 0x1;
                    WillPowerOff?.Invoke(((long)m.LParam & ENDSESSION_CLOSEAPP) == 0);
                }
                Win32.ShutdownBlockReasonDestroy(Handle);
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    private void HandlePower(Message m)
    {
        switch (m.WParam.ToInt32())
        {
            case Win32.PBT_APMSUSPEND:
                Update(() => IsAsleep = true);
                break;
            case Win32.PBT_APMRESUMESUSPEND or Win32.PBT_APMRESUMEAUTOMATIC:
                if (IsAsleep)
                {
                    Update(() => IsAsleep = false);
                    DidWake?.Invoke();
                }
                break;
            case Win32.PBT_POWERSETTINGCHANGE when m.LParam != IntPtr.Zero:
                var setting = Marshal.PtrToStructure<Win32.POWERBROADCAST_SETTING>(m.LParam);
                if (setting.PowerSetting == Win32.GUID_CONSOLE_DISPLAY_STATE)
                {
                    var off = setting.Data == 0;
                    if (off != IsDisplayAsleep) Update(() => IsDisplayAsleep = off);
                }
                break;
        }
    }

    private void Update(Action change)
    {
        var wasOpen = IsSessionOpen;
        change();
        var isOpen = IsSessionOpen;
        if (wasOpen && !isOpen) SessionWillClose?.Invoke();
        if (!wasOpen && isOpen) SessionDidOpen?.Invoke();
    }

    public void Dispose()
    {
        if (_displayNotification != IntPtr.Zero) Win32.UnregisterPowerSettingNotification(_displayNotification);
        _displayNotification = IntPtr.Zero;
        if (Handle != IntPtr.Zero)
        {
            Win32.WTSUnRegisterSessionNotification(Handle);
            DestroyHandle();
        }
    }
}
