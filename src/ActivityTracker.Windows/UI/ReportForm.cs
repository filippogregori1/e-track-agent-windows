using ActivityTracker.Core;
using ActivityTracker.Windows.Sync;
using Timer = System.Windows.Forms.Timer;

namespace ActivityTracker.Windows.UI;

/// <summary>
/// La finestrella del report, aperta dall'icona nella tray (l'equivalente del menu in barra del Mac): data, righe del
/// report (nome · % · durata · nota passivo), totale, stato corrente, riga di equipe-track, Impostazioni ed Esci.
/// Si aggiorna da sola ogni 5 s mentre è aperta e si chiude quando perde il fuoco.
/// Le misure sono in pixel logici (96 DPI) e passano da <see cref="S"/>: i caratteri scalano da soli, il resto no.
/// </summary>
public sealed class ReportForm : Form
{
    private const int ContentWidth = 400;
    private const int NameWidth = 170;

    private readonly AppModel _model;
    private readonly Action _openSettings;
    private readonly Action _quit;
    private readonly Timer _refresh = new() { Interval = 5000 };
    private readonly ToolTip _tips = new();
    private Palette _palette = Palette.Current;
    private Point _anchor;
    private bool _anchorBottom;

    private readonly Label _date = new();
    private readonly TableLayoutPanel _rows = new();
    private readonly Label _empty = new();
    private readonly Label _total = new();
    private readonly Label _status = new();
    private readonly Label _storage = new();
    private readonly Label _sync = new();
    private readonly FlowLayoutPanel _stack = new();
    private readonly Button _settings = new();
    private readonly Button _exit = new();
    private readonly List<Panel> _rules = [];

    /// <summary>Istante dell'ultima chiusura per perdita di fuoco: un clic sull'icona subito dopo non deve riaprirla.</summary>
    public DateTime LastAutoHide { get; private set; }

    public ReportForm(AppModel model, Action openSettings, Action quit)
    {
        _model = model;
        _openSettings = openSettings;
        _quit = quit;

        Text = AppIdentity.DisplayName;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        KeyPreview = true;
        Padding = new Padding(1);

        _stack.FlowDirection = FlowDirection.TopDown;
        _stack.WrapContents = false;
        _stack.AutoSize = true;
        _stack.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _stack.Padding = Pad(16, 14, 16, 12);
        _stack.Margin = Padding.Empty;
        _stack.Location = new Point(1, 1); // dentro il filo di 1 px disegnato in OnPaint

        Setup(_date, Fonts.Title, Pad(0, 0, 0, 10));
        _stack.Controls.Add(_date);
        _stack.Controls.Add(Rule());

        _rows.AutoSize = true;
        _rows.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _rows.ColumnCount = 4;
        for (var i = 0; i < 4; i++) _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _rows.Margin = Pad(0, 8, 0, 8);
        _rows.MinimumSize = new Size(S(ContentWidth), 0);
        _stack.Controls.Add(_rows);

        Setup(_empty, Fonts.Body, Pad(0, 10, 0, 10));
        _empty.Text = ReportFormatter.EmptyMessage;
        _stack.Controls.Add(_empty);

        _stack.Controls.Add(Rule());
        Setup(_total, Fonts.BodyStrong, Pad(0, 10, 0, 4));
        Setup(_status, Fonts.Caption, Pad(0, 0, 0, 2));
        Setup(_storage, Fonts.Caption, Pad(0, 0, 0, 2));
        Setup(_sync, Fonts.Caption, Pad(0, 0, 0, 10));
        _stack.Controls.AddRange([_total, _status, _storage, _sync]);
        _stack.Controls.Add(Rule());

        var buttons = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = Pad(0, 10, 0, 0),
            MinimumSize = new Size(S(ContentWidth), 0), MaximumSize = new Size(S(ContentWidth), 0),
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        StyleButton(_settings, "Impostazioni…");
        StyleButton(_exit, "Esci");
        _settings.Anchor = AnchorStyles.Left;
        _exit.Anchor = AnchorStyles.Right;
        _settings.Click += (_, _) =>
        {
            Hide();
            _openSettings();
        };
        _exit.Click += (_, _) => _quit();
        buttons.Controls.Add(_settings, 0, 0);
        buttons.Controls.Add(_exit, 1, 0);
        _stack.Controls.Add(buttons);

        Controls.Add(_stack);
        _refresh.Tick += (_, _) => RefreshContent();
        ApplyPalette();
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding Pad(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            cp.ExStyle |= 0x00000080;    // WS_EX_TOOLWINDOW: niente Alt+Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Dwm.RoundCorners(Handle);
    }

    /// <summary>Mostra la finestra vicino all'icona (dove si è cliccato), dentro l'area di lavoro.</summary>
    public void ShowNearTray(Point? anchor = null)
    {
        _palette = Palette.Current;
        ApplyPalette();
        _anchor = anchor ?? Cursor.Position;
        var area = Screen.FromPoint(_anchor).WorkingArea;
        _anchorBottom = _anchor.Y > area.Top + area.Height / 2;
        RefreshContent();
        Reposition();
        Show();
        Activate();
        _refresh.Start();
    }

    /// <summary>Accanto all'icona: sopra la barra se è in basso, sotto se è in alto; mai fuori dall'area di lavoro.</summary>
    private void Reposition()
    {
        var area = Screen.FromPoint(_anchor).WorkingArea;
        var margin = S(8);
        var x = Math.Clamp(_anchor.X - Width / 2, area.Left + margin, Math.Max(area.Left + margin, area.Right - Width - margin));
        var y = _anchorBottom ? Math.Max(area.Top + margin, area.Bottom - Height - margin) : area.Top + margin;
        Location = new Point(x, y);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // L'aggiornamento ogni 5 s può cambiare il numero di righe: la finestra resta attaccata alla barra.
        if (Visible) Reposition();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        LastAutoHide = DateTime.UtcNow;
        Hide();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) _refresh.Stop();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(_palette.Hairline);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    public void RefreshContent()
    {
        _model.RefreshReport();
        var report = _model.Report;
        _date.Text = _model.DateTitle;

        _rows.SuspendLayout();
        var old = _rows.Controls.Cast<Control>().ToList();
        _rows.Controls.Clear();
        foreach (var c in old) c.Dispose();
        _rows.RowStyles.Clear();
        _rows.RowCount = 0;
        if (_model.StartupError is { } startup)
        {
            _empty.Text = startup;
            _empty.ForeColor = _palette.Error;
        }
        else
        {
            _empty.Text = ReportFormatter.EmptyMessage;
            _empty.ForeColor = _palette.Mute;
        }
        _empty.Visible = report.IsEmpty || _model.StartupError is not null;
        _rows.Visible = !_empty.Visible;
        foreach (var row in report.Rows)
        {
            var r = _rows.RowCount++;
            _rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _rows.Controls.Add(NameCell(row.Name, row.IsUnused ? _palette.Mute : _palette.Ink), 0, r);
            _rows.Controls.Add(Cell(ReportFormatter.Percent(row.PerMille), _palette.Body, Fonts.Body, AnchorStyles.Right), 1, r);
            _rows.Controls.Add(Cell(ReportFormatter.Duration(row.Total), _palette.Body, Fonts.Body, AnchorStyles.Right), 2, r);
            var note = row.IsUnused ? "" : ReportFormatter.PassiveNote(row.Passive) ?? "";
            _rows.Controls.Add(Cell(note, _palette.Mute, Fonts.Caption, AnchorStyles.Left), 3, r);
        }
        _rows.ResumeLayout();

        _total.Text = ReportFormatter.TotalLine(report);
        _status.Text = _model.StatusLine;
        _storage.Text = _model.StorageError ?? "";
        _storage.Visible = _model.StorageError is not null;
        _sync.Text = _model.Sync.MenuLine;
        _sync.ForeColor = _model.Sync.NeedsAttention ? _palette.Attention
            : _model.Sync.State == SyncService.Link.Tracking ? _palette.Accent : _palette.Mute;
    }

    // Costruzione

    private void Setup(Label label, Font font, Padding margin)
    {
        label.AutoSize = true;
        label.MaximumSize = new Size(S(ContentWidth), 0);
        label.Font = font;
        label.Margin = margin;
        label.UseMnemonic = false;
    }

    private Label Cell(string text, Color color, Font font, AnchorStyles anchor) => new()
    {
        Text = text,
        ForeColor = color,
        Font = font,
        AutoSize = true,
        UseMnemonic = false,
        Margin = Pad(0, 3, 12, 3),
        Anchor = anchor,
    };

    /// <summary>Il nome dell'attività, accorciato con «…» se non ci sta (il nome intero nel suggerimento).</summary>
    private Label NameCell(string name, Color color)
    {
        var max = S(NameWidth);
        var text = name;
        if (TextRenderer.MeasureText(text, Fonts.Body).Width > max)
        {
            while (text.Length > 1 && TextRenderer.MeasureText(text + "…", Fonts.Body).Width > max) text = text[..^1];
            text = text.TrimEnd() + "…";
        }
        var label = Cell(text, color, Fonts.Body, AnchorStyles.Left);
        label.MinimumSize = new Size(max, 0);
        if (text != name) _tips.SetToolTip(label, name);
        return label;
    }

    private Panel Rule()
    {
        var p = new Panel { Height = 1, Width = S(ContentWidth), Margin = Padding.Empty };
        _rules.Add(p);
        return p;
    }

    private void StyleButton(Button b, string text)
    {
        b.Text = text;
        b.AutoSize = true;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.Padding = Pad(10, 2, 10, 2);
        b.Font = Fonts.Body;
        b.Cursor = Cursors.Hand;
        b.UseMnemonic = false;
    }

    private void ApplyPalette()
    {
        BackColor = _palette.Canvas;
        _stack.BackColor = _palette.Canvas;
        _date.ForeColor = _palette.Ink;
        _total.ForeColor = _palette.Ink;
        _status.ForeColor = _palette.Mute;
        _storage.ForeColor = _palette.Error;
        foreach (var r in _rules) r.BackColor = _palette.Hairline;
        foreach (var b in new[] { _settings, _exit })
        {
            b.BackColor = _palette.Surface;
            b.ForeColor = _palette.Ink;
            b.FlatAppearance.BorderColor = _palette.Hairline;
            b.FlatAppearance.MouseOverBackColor = _palette.ButtonHover;
            b.FlatAppearance.MouseDownBackColor = _palette.Hairline;
        }
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refresh.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
