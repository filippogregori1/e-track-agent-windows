using ActivityTracker.Core;
using ActivityTracker.Windows.Sync;
using Timer = System.Windows.Forms.Timer;

namespace ActivityTracker.Windows.UI;

/// <summary>
/// Impostazioni, le stesse sezioni del Mac: soglia, avvio all'accesso, collegamento a equipe-track (server, token,
/// segreto del gate, salvati in Gestione credenziali), regole attività, esclusioni video, segnali, cartella dati.
/// Token e segreto restano nei campi solo fino al salvataggio, poi i campi si svuotano.
///
/// Impaginazione: sezioni di larghezza fissa una sotto l'altra in un pannello che scorre; dentro ogni sezione una
/// griglia a una colonna, i campi si allargano per ancoraggio. Misure in pixel logici, scalate con <see cref="S"/>.
/// </summary>
public sealed class SettingsForm : Form
{
    private const int SectionWidth = 640;
    private const int InnerWidth = SectionWidth - 24;

    private readonly AppModel _model;
    private readonly Timer _refresh = new() { Interval = 2000 };

    private readonly NumericUpDown _threshold = new() { Minimum = AppSettings.MinThresholdMinutes, Maximum = AppSettings.MaxThresholdMinutes };
    private readonly CheckBox _login = new() { Text = "Avvia all'accesso a Windows", AutoSize = true };
    private readonly Label _loginStatus;

    private readonly Label _syncStatus = new() { AutoSize = true, Font = Fonts.BodyStrong, UseMnemonic = false };
    private readonly Label _syncDetail;
    private readonly TextBox _server = new() { PlaceholderText = "https://track.esempio.it" };
    private readonly TextBox _token = new() { UseSystemPasswordChar = true };
    private readonly TextBox _gate = new() { UseSystemPasswordChar = true };
    private readonly Button _save = new() { Text = "Salva nelle credenziali di Windows", AutoSize = true };
    private readonly Button _sendNow = new() { Text = "Invia ora", AutoSize = true };
    private readonly Button _disconnect = new() { Text = "Scollega", AutoSize = true };
    private readonly Label _syncError;

    private readonly DataGridView _rules = new();
    private readonly Label _rulesError;
    private readonly TextBox _exclusions = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly Label _signals;
    private bool _loading;

    public SettingsForm(AppModel model)
    {
        _model = model;
        Text = "Impostazioni — " + AppIdentity.DisplayName;
        Icon = Icons.Load("activity-tracker.ico", new Size(32, 32));
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        Font = Fonts.Body;
        var width = S(SectionWidth) + S(32) + SystemInformation.VerticalScrollBarWidth;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, width, S(760));
        ClientSize = new Size(width, Math.Min(S(760), area.Height - S(60)));
        MinimumSize = new Size(Width, S(420));
        MaximumSize = new Size(Width, int.MaxValue);
        MaximizeBox = false;

        _loginStatus = Caption("");
        _syncDetail = Caption("");
        _syncError = Caption("");
        _rulesError = Caption("");
        _signals = Caption("");

        var page = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = Pad(16, 12, 16, 16),
        };
        page.Controls.AddRange([MeasureSection(), StartupSection(), EquipeSection(), RulesSection(), ExclusionsSection(), SignalsSection(), DataSection()]);
        Controls.Add(page);

        _refresh.Tick += (_, _) => RefreshLive();
        LoadValues();
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding Pad(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    // Sezioni

    private GroupBox MeasureSection()
    {
        _threshold.Width = S(64);
        var row = Row(new Label { Text = "Soglia di inattività", AutoSize = true, Margin = Pad(0, 6, 8, 0) }, _threshold,
                      new Label { Text = "min", AutoSize = true, Margin = Pad(4, 6, 0, 0) });
        _threshold.ValueChanged += (_, _) =>
        {
            if (!_loading) _model.ThresholdMinutes = (int)_threshold.Value;
        };
        return Section("Misura", row,
            Caption("Senza input per questo tempo, i minuti dall'ultimo input diventano passivi (video) o «Attivo senza utilizzo»."));
    }

    private GroupBox StartupSection()
    {
        _login.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _model.SetLaunchAtLogin(_login.Checked);
            _loginStatus.Text = _model.LoginStatus;
        };
        return Section("Avvio", _login, _loginStatus);
    }

    private GroupBox EquipeSection()
    {
        var header = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = true, Margin = Pad(0, 0, 0, 6) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var status = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        status.Controls.AddRange([_syncStatus, _syncDetail]);
        _sendNow.Anchor = _disconnect.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.Add(status, 0, 0);
        header.Controls.Add(_sendNow, 1, 0);
        header.Controls.Add(_disconnect, 2, 0);
        _sendNow.Click += (_, _) =>
        {
            _model.Sync.SendNow();
            RefreshSync();
        };
        _disconnect.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Togliere server, token e segreto da questo PC? L'agente smette di tracciare.", Text,
                                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            _model.Sync.Disconnect();
            _server.Text = "";
            RefreshSync();
        };

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Field(string label, TextBox field)
        {
            var r = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Right, Margin = Pad(0, 4, 8, 4) }, 0, r);
            field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            field.Margin = Pad(0, 3, 0, 3);
            grid.Controls.Add(field, 1, r);
        }
        Field("Server", _server);
        Field("Token agente", _token);
        Field("Segreto gate", _gate);
        _gate.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) Save();
        };
        _save.Click += (_, _) => Save();
        _server.TextChanged += (_, _) => UpdateSaveEnabled();
        _token.TextChanged += (_, _) => UpdateSaveEnabled();
        _save.Anchor = AnchorStyles.Right;
        _save.Margin = Pad(0, 6, 0, 0);
        _syncError.ForeColor = Color.Firebrick;
        return Section("equipe-track", header, grid, _save, _syncError,
            Caption("Si traccia solo con una sessione aperta e non in pausa su equipe-track. Escono solo nomi già aggregati " +
                    "(es. «YouTube»), stato e durata: mai indirizzi, titoli di finestra o nomi dei programmi."));
    }

    private GroupBox RulesSection()
    {
        _rules.AllowUserToAddRows = false;
        _rules.AllowUserToDeleteRows = false;
        _rules.AllowUserToResizeRows = false;
        _rules.RowHeadersVisible = false;
        _rules.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _rules.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _rules.Height = S(240);
        _rules.BackgroundColor = SystemColors.Window;
        _rules.BorderStyle = BorderStyle.FixedSingle;
        _rules.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "Tipo", Name = "kind", FillWeight = 30, FlatStyle = FlatStyle.Flat,
            DataSource = RuleKinds.All.Select(k => new KindItem(k.Raw(), k.Label())).ToList(),
            ValueMember = nameof(KindItem.Value), DisplayMember = nameof(KindItem.Text),
        });
        _rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Pattern", Name = "pattern", FillWeight = 40 });
        _rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Attività", Name = "activity", FillWeight = 30 });
        _rules.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_rules.CurrentCell is DataGridViewComboBoxCell) _rules.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _rules.CellValueChanged += (_, e) => SaveRule(e.RowIndex);
        _rules.DataError += (_, e) => e.ThrowException = false;

        var add = new Button { Text = "Aggiungi regola", AutoSize = true };
        var delete = new Button { Text = "Elimina selezionata", AutoSize = true };
        add.Click += (_, _) =>
        {
            _model.AddRule();
            LoadRules();
            var index = _model.Rules.FindIndex(r => r.Pattern.StartsWith("nuovo-", StringComparison.Ordinal));
            if (index >= 0)
            {
                _rules.CurrentCell = _rules.Rows[index].Cells["pattern"];
                _rules.BeginEdit(selectAll: true);
            }
        };
        delete.Click += (_, _) =>
        {
            if (_rules.CurrentRow?.Tag is not ActivityRule rule) return;
            _model.DeleteRule(rule);
            LoadRules();
        };
        _rulesError.ForeColor = Color.Firebrick;
        return Section("Regole attività", _rules, Row(add, delete), _rulesError,
            Caption("Priorità: dominio › titolo › app. Dominio = uguale o sottodominio (m.youtube.com rientra in youtube.com); " +
                    "titolo = contiene (senza maiuscole/minuscole); app = nome dell'eseguibile (es. zoom.exe). " +
                    "Le regole valgono anche sul passato."));
    }

    private sealed record KindItem(string Value, string Text);

    private GroupBox ExclusionsSection()
    {
        _exclusions.Height = S(120);
        var save = new Button { Text = "Salva", AutoSize = true };
        var reset = new Button { Text = "Ripristina", AutoSize = true };
        save.Click += (_, _) =>
        {
            _model.VideoExclusions = _exclusions.Lines;
            _exclusions.Lines = _model.VideoExclusions.ToArray();
        };
        reset.Click += (_, _) =>
        {
            _model.ResetExclusions();
            _exclusions.Lines = _model.VideoExclusions.ToArray();
        };
        return Section("Esclusioni video", _exclusions, Row(save, reset),
            Caption("Un nome per riga (processo o app, con o senza «.exe»). Lettori solo audio e programmi che tengono acceso " +
                    "lo schermo senza video: la loro riproduzione non conta come «passivo»."));
    }

    private GroupBox SignalsSection() =>
        Section("Segnali (diagnostica)", _signals,
            Caption("Cosa vede l'agente adesso: serve per le prove a mano (TEST_SCENARIOS.md). Si aggiorna ogni 2 secondi."));

    private GroupBox DataSection()
    {
        var open = new Button { Text = "Apri cartella dati", AutoSize = true };
        open.Click += (_, _) => _model.OpenDataFolder();
        return Section("Dati", Caption(_model.DataFolderPath), open);
    }

    // Comportamento

    private void LoadValues()
    {
        _loading = true;
        _threshold.Value = _model.ThresholdMinutes;
        _model.RefreshLoginStatus();
        _login.Checked = LoginItem.IsEnabled;
        _loginStatus.Text = _model.LoginStatus;
        _exclusions.Lines = _model.VideoExclusions.ToArray();
        _server.Text = _model.Sync.ServerUrl;
        _loading = false;
        LoadRules();
        RefreshLive();
    }

    public void ShowAndActivate()
    {
        LoadValues();
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        _refresh.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            _token.Text = "";
            _gate.Text = "";
            _refresh.Stop();
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    private void RefreshLive()
    {
        _signals.Text = _model.SignalsText();
        RefreshSync();
    }

    public void RefreshSync()
    {
        var sync = _model.Sync;
        _syncStatus.Text = sync.StatusText;
        _syncStatus.ForeColor = sync.State == SyncService.Link.Tracking ? Color.SeaGreen
            : sync.NeedsAttention ? Color.DarkGoldenrod : SystemColors.ControlText;
        var detail = new List<string>();
        if (sync.ServerUrl.Length > 0) detail.Add(sync.ServerUrl + (sync.SessionGiorno is { } g ? " · giornata " + g : ""));
        detail.Add(sync.LastSendText);
        _syncDetail.Text = string.Join(Environment.NewLine, detail);
        _sendNow.Visible = sync.HasToken;
        _sendNow.Enabled = !sync.IsBusy;
        _disconnect.Visible = sync.HasToken;
        _token.PlaceholderText = sync.HasToken ? "salvato in Gestione credenziali (vuoto = non cambiarlo)" : "da npm run agente:token";
        _gate.PlaceholderText = sync.HasGateSecret ? "salvato in Gestione credenziali (vuoto = non cambiarlo)" : "ACCESS_SECRET (cookie et_gate), se te l'hanno dato";
        _syncError.Text = sync.LastError ?? "";
        _syncError.Visible = sync.LastError is not null;
        UpdateSaveEnabled();
    }

    private void UpdateSaveEnabled() =>
        _save.Enabled = _server.Text.Trim().Length > 0 && (_model.Sync.HasToken || _token.Text.Length > 0);

    private void Save()
    {
        if (!_save.Enabled) return;
        if (_model.Sync.SaveCredentials(_server.Text, _token.Text, _gate.Text)) _server.Text = _model.Sync.ServerUrl;
        _token.Text = "";
        _gate.Text = "";
        RefreshSync();
    }

    private void LoadRules()
    {
        _loading = true;
        _model.ReloadRules();
        _rules.Rows.Clear();
        foreach (var rule in _model.Rules)
        {
            var i = _rules.Rows.Add(rule.Kind.Raw(), rule.Pattern, rule.Activity);
            _rules.Rows[i].Tag = rule;
        }
        _rulesError.Text = _model.RulesError ?? "";
        _rulesError.Visible = _model.RulesError is not null;
        _loading = false;
    }

    /// <summary>Salvataggio immediato a ogni modifica, come sul Mac; campi vuoti = non si salva.</summary>
    private void SaveRule(int rowIndex)
    {
        if (_loading || rowIndex < 0 || _rules.Rows[rowIndex].Tag is not ActivityRule rule) return;
        var cells = _rules.Rows[rowIndex].Cells;
        var kind = RuleKinds.Parse(cells["kind"].Value as string ?? rule.Kind.Raw());
        var pattern = (cells["pattern"].Value as string ?? "").Trim();
        var activity = (cells["activity"].Value as string ?? "").Trim();
        if (pattern.Length == 0 || activity.Length == 0) return;
        var updated = rule with { Kind = kind, Pattern = pattern, Activity = activity };
        if (updated == rule) return;
        var ok = _model.UpdateRule(updated);
        // Fuori dall'evento della griglia: ricaricarla mentre notifica una modifica non è ammesso.
        BeginInvoke(() =>
        {
            if (ok && rowIndex < _rules.Rows.Count) _rules.Rows[rowIndex].Tag = updated;
            else LoadRules();
            _rulesError.Text = _model.RulesError ?? "";
            _rulesError.Visible = !ok;
        });
    }

    // Aiuti di impaginazione

    private Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Fonts.Caption,
        ForeColor = SystemColors.GrayText,
        MaximumSize = new Size(S(InnerWidth), 0),
        Margin = Pad(0, 4, 0, 2),
        UseMnemonic = false,
    };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    /// <summary>Riquadro largo <see cref="SectionWidth"/>; i figli uno sotto l'altro. Caselle di testo, griglie e
    /// pannelli a griglia si allargano a tutta la riga; etichette e pulsanti restano della loro misura.</summary>
    private GroupBox Section(string title, params Control[] children)
    {
        var inner = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(S(10), S(22)),
            MinimumSize = new Size(S(InnerWidth), 0),
            MaximumSize = new Size(S(InnerWidth), 0),
            Margin = Padding.Empty,
        };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var child in children)
        {
            var r = inner.RowCount++;
            inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (child is TextBox or DataGridView or TableLayoutPanel)
                child.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            else if (child.Anchor == (AnchorStyles.Top | AnchorStyles.Left))
                child.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            inner.Controls.Add(child, 0, r);
        }
        var box = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = Pad(10, 6, 10, 10),
            Margin = Pad(0, 0, 0, 10),
            MinimumSize = new Size(S(SectionWidth), 0),
            MaximumSize = new Size(S(SectionWidth), 0),
        };
        box.Controls.Add(inner);
        return box;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refresh.Dispose();
        base.Dispose(disposing);
    }
}
