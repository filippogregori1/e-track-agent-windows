using ActivityTracker.Core;
using ActivityTracker.Windows.Sync;
using Timer = System.Windows.Forms.Timer;

namespace ActivityTracker.Windows.UI;

/// <summary>
/// L'app vive come icona nella tray di Windows, senza finestra principale (l'equivalente di LSUIElement sul Mac).
/// Clic sinistro: report del giorno. Clic destro: menu con report, Impostazioni, Invia ora, Esci.
/// L'icona dice a colpo d'occhio se si sta tracciando (barre verdi) o no (barre grigie).
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly AppModel _model = new();
    private readonly NotifyIcon _tray = new();
    private readonly ReportForm _report;
    private readonly SettingsForm _settings;
    private readonly Timer _status = new() { Interval = 15_000 };
    private readonly SynchronizationContext _ui;
    private readonly RegisteredWaitHandle _showRegistration;
    private readonly RegisteredWaitHandle _quitRegistration;
    private readonly Icon _iconTracking;
    private readonly Icon _iconIdle;
    private bool _exiting;
    private DateTime _trayMouseDownAt;

    public TrayApp(bool autostart, EventWaitHandle showRequests, EventWaitHandle quitRequests)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var size = SystemInformation.SmallIconSize;
        _iconTracking = Icons.Load("tray-tracking.ico", size);
        _iconIdle = Icons.Load("tray-idle.ico", size);

        _model.Start();
        _report = new ReportForm(_model, OpenSettings, Quit);
        _settings = new SettingsForm(_model);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Mostra il report di oggi", null, (_, _) => _report.ShowNearTray());
        menu.Items.Add("Impostazioni…", null, (_, _) => OpenSettings());
        var sendNow = menu.Items.Add("Invia ora a equipe-track", null, (_, _) => _model.Sync.SendNow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Esci", null, (_, _) => Quit());
        menu.Opening += (_, _) => sendNow.Enabled = _model.Sync.HasToken;

        _tray.ContextMenuStrip = menu;
        _tray.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _trayMouseDownAt = DateTime.UtcNow;
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            // Premere sull'icona toglie il fuoco al report, che si nasconde da solo: se è successo da quando il tasto
            // è stato premuto, quel clic voleva chiuderlo (vale anche per un clic lungo o un tocco).
            if (_report.Visible) _report.Hide();
            else if (_report.LastAutoHide < _trayMouseDownAt.AddMilliseconds(-150)) _report.ShowNearTray();
        };
        _tray.Visible = true;

        _model.Sync.Changed += () => _ui.Post(_ => UpdateTray(), null);
        _status.Tick += (_, _) =>
        {
            _model.Sync.Refresh();
            UpdateTray();
        };
        _status.Start();
        UpdateTray();

        // Una seconda apertura dell'app (menu Start, collegamento sul desktop) mostra il report di questa.
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(showRequests,
            (_, _) => _ui.Post(_ => _report.ShowNearTray(TrayCorner()), null), null, Timeout.Infinite, executeOnlyOnce: false);
        // L'installer, prima di aggiornare o disinstallare, chiede un'uscita ordinata (activity-tracker.exe --quit).
        _quitRegistration = ThreadPool.RegisterWaitForSingleObject(quitRequests,
            (_, _) => _ui.Post(_ => Quit(), null), null, Timeout.Infinite, executeOnlyOnce: true);

        // Primo avvio a mano senza collegamento: si aprono le Impostazioni, come farebbe il Mac al primo uso.
        if (!autostart && !_model.Sync.HasToken)
        {
            _ui.Post(_ => OpenSettings(), null);
        }
        else if (!_model.Sync.HasToken)
        {
            _tray.ShowBalloonTip(8000, AppIdentity.DisplayName,
                "Non collegato a equipe-track: apri le Impostazioni dall'icona per inserire server e token.", ToolTipIcon.Info);
        }
    }

    /// <summary>Angolo dell'area di lavoro dove sta di solito la tray (in basso a destra), per aperture non da clic.</summary>
    private static Point TrayCorner()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;
        return new Point(area.Right - 1, area.Bottom - 1);
    }

    private void UpdateTray()
    {
        if (_exiting) return;
        var sync = _model.Sync;
        _tray.Icon = sync.State == SyncService.Link.Tracking ? _iconTracking : _iconIdle;
        var text = $"{AppIdentity.DisplayName} · {sync.StatusText}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
        if (_settings.Visible) _settings.RefreshSync();
    }

    private void OpenSettings()
    {
        _report.Hide();
        _settings.ShowAndActivate();
    }

    private void Quit()
    {
        if (_exiting) return;
        _exiting = true;
        _report.Hide();
        _settings.Hide();
        _tray.Visible = false;
        // Ultimo flush e ultimo invio; la sessione su equipe-track si chiude solo allo spegnimento (o con AT_STOP_ON_QUIT=1).
        try
        {
            _model.Shutdown(closeSession: AppSettings.StopOnQuit);
        }
        catch (Exception e)
        {
            Log.Write("[TrayApp] uscita: " + e.Message);
        }
        finally
        {
            // Qualunque cosa succeda all'invio finale, il processo esce: mai un agente invisibile e non chiudibile.
            ExitThread();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _showRegistration.Unregister(null);
            _quitRegistration.Unregister(null);
            _status.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _report.Dispose();
            _settings.Dispose();
            _model.Dispose();
            _iconTracking.Dispose();
            _iconIdle.Dispose();
        }
        base.Dispose(disposing);
    }
}
