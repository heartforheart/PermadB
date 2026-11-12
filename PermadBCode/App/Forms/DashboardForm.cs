using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PermadB.Audio;
using PermadB.Config;
using PermadB.Tray;

namespace PermadB.Forms;

public class DashboardForm : Form
{
    private readonly ConfigManager _config;
    private readonly AudioEngine _engine;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    // Window drag win32 interop
    [DllImport("user32.dll")]
    public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HT_CAPTION = 0x2;

    // Drop shadow
    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x20000;
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    // Colors
    private readonly Color _bgDark = Color.FromArgb(15, 23, 42);      // Slate 900
    private readonly Color _bgCard = Color.FromArgb(30, 41, 59);      // Slate 800
    private readonly Color _bgCardHover = Color.FromArgb(41, 55, 78); // Slate 750
    private readonly Color _borderDark = Color.FromArgb(51, 65, 85);  // Slate 700
    private readonly Color _accentSky = Color.FromArgb(56, 189, 248);  // Sky 400
    private readonly Color _accentGreen = Color.FromArgb(34, 197, 94); // Emerald 500
    private readonly Color _accentAmber = Color.FromArgb(245, 158, 11); // Amber 500
    private readonly Color _accentRed = Color.FromArgb(239, 68, 68);   // Red 500
    private readonly Color _textWhite = Color.FromArgb(248, 250, 252); // Slate 50
    private readonly Color _textMuted = Color.FromArgb(148, 163, 184); // Slate 400

    // Cached Fonts for Row Population (Zero GDI churn)
    private readonly Font _fontAppName = new("Segoe UI", 9.0f, FontStyle.Bold);
    private readonly Font _fontAppStatus = new("Segoe UI", 7.5f, FontStyle.Regular);
    private readonly Font _fontAppButton = new("Segoe UI", 7.5f, FontStyle.Bold);

    // UI Controls
    private Panel _headerPanel = null!;
    private Label _titleLabel = null!;
    private Label _subtitleLabel = null!;
    private Button _closeButton = null!;

    private Panel _powerCard = null!;
    private Label _powerStatusLabel = null!;
    private Label _powerSubLabel = null!;
    private Button _powerToggleButton = null!;

    private DecibelMeterControl _meterControl = null!;

    private Panel _presetContainer = null!;
    private PresetCardControl _cardSafe = null!;
    private PresetCardControl _cardNight = null!;
    private PresetCardControl _cardStudio = null!;
    private PresetCardControl _cardCustom1 = null!;
    private PresetCardControl _cardCustom2 = null!;

    private Panel _customCeilingCard = null!;
    private Label _customCeilingLabel = null!;
    private Button _saveCustom1Button = null!;
    private Button _saveCustom2Button = null!;
    private TrackBar _customCeilingSlider = null!;

    private Panel _settingsCard = null!;
    private Label _deviceInfoLabel = null!;
    private CheckBox _chkAllDevices = null!;
    private CheckBox _chkStartup = null!;
    private CheckBox _chkAppFiltering = null!;

    // Dual View Panels & Controls (In-window view swap)
    private Panel _mainViewPanel = null!;
    private Panel _appManagerViewPanel = null!;
    private Button _backButton = null!;
    private CheckBox _chkAppMode = null!;
    private TextBox _txtAppSearch = null!;
    private Button _btnAddApp = null!;
    private FlowLayoutPanel _appsScrollPanel = null!;

    private bool _isUpdatingUi = false;
    private string _lastKnownPreset = string.Empty;

    public DashboardForm(ConfigManager config, AudioEngine engine)
    {
        _config = config;
        _engine = engine;

        InitializeComponent();

        // Ensure Win32 window handle is created immediately so BeginInvoke/Invoke never throws
        _ = this.Handle;

        _refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = 33 // ~30 FPS
        };
        _refreshTimer.Tick += (s, e) => RefreshLiveMetrics();
        // Timer starts only when form becomes visible (OnVisibleChanged) to eliminate background CPU/memory churn

        UpdatePresetCards();
    }

    private void InitializeComponent()
    {
        this.SuspendLayout();

        // Form settings
        this.FormBorderStyle = FormBorderStyle.None;
        this.StartPosition = FormStartPosition.Manual;
        this.ShowInTaskbar = false;
        this.TopMost = true;
        this.Size = new Size(420, 640);
        this.BackColor = _bgDark;
        this.ForeColor = _textWhite;
        this.Font = new Font("Segoe UI", 9.0f, FontStyle.Regular);
        this.DoubleBuffered = true;

        // 1. Header Panel
        _headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 46,
            BackColor = Color.FromArgb(20, 30, 48),
            Padding = new Padding(14, 0, 8, 0)
        };
        _headerPanel.MouseDown += HeaderPanel_MouseDown;

        _titleLabel = new Label
        {
            Text = "🛡️ PermadB",
            Font = new Font("Segoe UI", 12.0f, FontStyle.Bold),
            ForeColor = _textWhite,
            AutoSize = true,
            Location = new Point(12, 11),
            Cursor = Cursors.SizeAll
        };
        _titleLabel.MouseDown += HeaderPanel_MouseDown;

        _subtitleLabel = new Label
        {
            Text = "Decibel Guard & Limiter",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            ForeColor = _textMuted,
            AutoSize = true,
            Location = new Point(122, 15),
            Cursor = Cursors.SizeAll
        };
        _subtitleLabel.MouseDown += HeaderPanel_MouseDown;

        _closeButton = new Button
        {
            Text = "✕",
            Font = new Font("Segoe UI", 10.0f, FontStyle.Bold),
            ForeColor = _textMuted,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(32, 32),
            Location = new Point(376, 7),
            Cursor = Cursors.Hand
        };
        _closeButton.FlatAppearance.BorderSize = 0;
        _closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(64, 75, 96);
        _closeButton.Click += (s, e) => this.Hide();

        var donateButton = new Button
        {
            Text = "☕ Donate",
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            Size = new Size(68, 24),
            Location = new Point(300, 11),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(255, 94, 91),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        donateButton.FlatAppearance.BorderSize = 0;
        donateButton.Click += (s, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://ko-fi.com/unsever") { UseShellExecute = true });
            }
            catch { }
        };

        _backButton = new Button
        {
            Text = "← Back",
            Font = new Font("Segoe UI", 8.0f, FontStyle.Bold),
            Size = new Size(58, 24),
            Location = new Point(10, 11),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(51, 65, 85),
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Visible = false
        };
        _backButton.FlatAppearance.BorderSize = 0;
        _backButton.Click += (s, e) => ShowMainView();

        _headerPanel.Controls.Add(_backButton);
        _headerPanel.Controls.Add(_titleLabel);
        _headerPanel.Controls.Add(_subtitleLabel);
        _headerPanel.Controls.Add(donateButton);
        _headerPanel.Controls.Add(_closeButton);
        this.Controls.Add(_headerPanel);

        // Main View Panel (Holds primary dashboard cards)
        _mainViewPanel = new Panel
        {
            Location = new Point(0, 46),
            Size = new Size(420, 594),
            BackColor = Color.Transparent
        };

        // 2. Power / Status Banner
        _powerCard = new Panel
        {
            Location = new Point(14, 10),
            Size = new Size(392, 54),
            BackColor = _bgCard
        };
        _powerCard.Paint += (s, e) => DrawCardBorder(e.Graphics, _powerCard.ClientRectangle);

        _powerStatusLabel = new Label
        {
            Text = "Protection: ACTIVE",
            Font = new Font("Segoe UI", 10.0f, FontStyle.Bold),
            ForeColor = _accentGreen,
            Location = new Point(12, 8),
            AutoSize = true
        };

        _powerSubLabel = new Label
        {
            Text = "Windows Volume: 100% • Limiter: Armed",
            Font = new Font("Segoe UI", 8.0f, FontStyle.Regular),
            ForeColor = _textMuted,
            Location = new Point(13, 29),
            AutoSize = true
        };

        _powerToggleButton = new Button
        {
            Text = "ACTIVE",
            Font = new Font("Segoe UI", 9.0f, FontStyle.Bold),
            Size = new Size(76, 32),
            Location = new Point(304, 11),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(22, 101, 52),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        _powerToggleButton.FlatAppearance.BorderSize = 0;
        _powerToggleButton.Click += PowerToggleButton_Click;

        _powerCard.Controls.Add(_powerStatusLabel);
        _powerCard.Controls.Add(_powerSubLabel);
        _powerCard.Controls.Add(_powerToggleButton);
        _mainViewPanel.Controls.Add(_powerCard);

        // 3. Live Decibel & Limiter Meter
        _meterControl = new DecibelMeterControl
        {
            Location = new Point(14, 70),
            Size = new Size(392, 116),
            BackColor = _bgCard
        };
        _mainViewPanel.Controls.Add(_meterControl);

        // 4. Limiter Ceiling Presets
        var presetHeaderLabel = new Label
        {
            Text = "LIMITER CEILING PRESETS",
            Font = new Font("Segoe UI", 8.0f, FontStyle.Bold),
            ForeColor = _textMuted,
            Location = new Point(16, 194),
            AutoSize = true
        };
        _mainViewPanel.Controls.Add(presetHeaderLabel);

        _presetContainer = new Panel
        {
            Location = new Point(14, 212),
            Size = new Size(392, 94),
            BackColor = Color.Transparent
        };

        _cardSafe = new PresetCardControl(
            title: "Safe",
            ceilingText: "65%",
            tagText: "75 dB • Safe",
            onClick: () => ApplyPreset("safe")
        )
        {
            Location = new Point(0, 0),
            Size = new Size(72, 94)
        };

        _cardNight = new PresetCardControl(
            title: "Night",
            ceilingText: "50%",
            tagText: "68 dB • Quiet",
            onClick: () => ApplyPreset("night")
        )
        {
            Location = new Point(80, 0),
            Size = new Size(72, 94)
        };

        _cardStudio = new PresetCardControl(
            title: "Studio",
            ceilingText: "85%",
            tagText: "82 dB • Loud",
            onClick: () => ApplyPreset("studio")
        )
        {
            Location = new Point(160, 0),
            Size = new Size(72, 94)
        };

        float initCustom1Val = _config.Settings.Custom1PresetPercent;
        float initCustom1Spl = 50.0f + (initCustom1Val / 100.0f) * 38.0f;
        _cardCustom1 = new PresetCardControl(
            title: "Cust 1",
            ceilingText: $"{initCustom1Val:F0}%",
            tagText: $"{initCustom1Spl:F0} dB • Lowest",
            onClick: () => ApplyPreset("custom1"),
            onRightClick: () => SaveCurrentAsCustom(1)
        )
        {
            Location = new Point(240, 0),
            Size = new Size(72, 94)
        };

        float initCustom2Val = _config.Settings.Custom2PresetPercent;
        float initCustom2Spl = 50.0f + (initCustom2Val / 100.0f) * 38.0f;
        _cardCustom2 = new PresetCardControl(
            title: "Cust 2",
            ceilingText: $"{initCustom2Val:F0}%",
            tagText: $"{initCustom2Spl:F0} dB • Highest",
            onClick: () => ApplyPreset("custom2"),
            onRightClick: () => SaveCurrentAsCustom(2)
        )
        {
            Location = new Point(320, 0),
            Size = new Size(72, 94)
        };

        _presetContainer.Controls.Add(_cardSafe);
        _presetContainer.Controls.Add(_cardNight);
        _presetContainer.Controls.Add(_cardStudio);
        _presetContainer.Controls.Add(_cardCustom1);
        _presetContainer.Controls.Add(_cardCustom2);
        _mainViewPanel.Controls.Add(_presetContainer);

        // 5. Custom Limiter Ceiling Slider Card
        _customCeilingCard = new Panel
        {
            Location = new Point(14, 314),
            Size = new Size(392, 70),
            BackColor = _bgCard
        };
        _customCeilingCard.Paint += (s, e) => DrawCardBorder(e.Graphics, _customCeilingCard.ClientRectangle);

        _customCeilingLabel = new Label
        {
            Text = "Custom Ceiling: 65% (75 dBA)",
            Font = new Font("Segoe UI", 9.0f, FontStyle.Bold),
            ForeColor = _textWhite,
            Location = new Point(12, 8),
            AutoSize = true
        };

        _saveCustom1Button = new Button
        {
            Text = "💾 Cust 1",
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            Size = new Size(64, 22),
            Location = new Point(252, 5),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(41, 55, 78),
            ForeColor = _accentSky,
            Cursor = Cursors.Hand
        };
        _saveCustom1Button.FlatAppearance.BorderColor = _borderDark;
        _saveCustom1Button.FlatAppearance.BorderSize = 1;
        _saveCustom1Button.Click += (s, e) => SaveCurrentAsCustom(1);

        _saveCustom2Button = new Button
        {
            Text = "💾 Cust 2",
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            Size = new Size(64, 22),
            Location = new Point(320, 5),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(41, 55, 78),
            ForeColor = _accentSky,
            Cursor = Cursors.Hand
        };
        _saveCustom2Button.FlatAppearance.BorderColor = _borderDark;
        _saveCustom2Button.FlatAppearance.BorderSize = 1;
        _saveCustom2Button.Click += (s, e) => SaveCurrentAsCustom(2);

        _customCeilingSlider = new TrackBar
        {
            Minimum = 10,
            Maximum = 100,
            TickFrequency = 5,
            Value = (int)Math.Clamp(_engine.GetActiveProfile().SafeCeilingPercent, 10, 100),
            Location = new Point(8, 28),
            Size = new Size(374, 34),
            BackColor = _bgCard,
            Cursor = Cursors.Hand
        };
        _customCeilingSlider.Scroll += CustomCeilingSlider_Scroll;

        _customCeilingCard.Controls.Add(_customCeilingLabel);
        _customCeilingCard.Controls.Add(_saveCustom1Button);
        _customCeilingCard.Controls.Add(_saveCustom2Button);
        _customCeilingCard.Controls.Add(_customCeilingSlider);
        _mainViewPanel.Controls.Add(_customCeilingCard);

        // 6. Device Info & Quick Settings Card
        _settingsCard = new Panel
        {
            Location = new Point(14, 392),
            Size = new Size(392, 192),
            BackColor = _bgCard
        };
        _settingsCard.Paint += (s, e) => DrawCardBorder(e.Graphics, _settingsCard.ClientRectangle);

        _deviceInfoLabel = new Label
        {
            Text = "🎧 Audio Device: Detecting...",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = _accentSky,
            Location = new Point(12, 10),
            Size = new Size(368, 20),
            AutoEllipsis = true
        };

        _chkStartup = new CheckBox
        {
            Text = "Start with Windows on Login",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            ForeColor = _textWhite,
            Location = new Point(14, 36),
            Size = new Size(360, 24),
            Checked = StartupManager.IsStartupEnabled(),
            Cursor = Cursors.Hand
        };
        _chkStartup.CheckedChanged += (s, e) =>
        {
            StartupManager.SetStartup(_chkStartup.Checked);
            _config.Settings.StartWithWindows = _chkStartup.Checked;
            _config.Save();
        };

        _chkAllDevices = new CheckBox
        {
            Text = "Multi-Device Protection (All outputs)",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            ForeColor = _textWhite,
            Location = new Point(14, 64),
            Size = new Size(360, 24),
            Checked = _config.Settings.ApplyToAllDevices,
            Cursor = Cursors.Hand
        };
        _chkAllDevices.CheckedChanged += (s, e) =>
        {
            _config.Settings.ApplyToAllDevices = _chkAllDevices.Checked;
            _config.Save();
            _engine.EnforceVolumeCeiling();
        };

        _chkAppFiltering = new CheckBox
        {
            Text = "App-Specific Limiting (Experimental)",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            ForeColor = _textWhite,
            Location = new Point(14, 90),
            Size = new Size(240, 24),
            Checked = _config.Settings.AppFilteringEnabled,
            Cursor = Cursors.Hand
        };
        _chkAppFiltering.CheckedChanged += (s, e) =>
        {
            if (_config.Settings.AppFilteringEnabled != _chkAppFiltering.Checked)
            {
                _engine.ToggleAppFiltering(_chkAppFiltering.Checked);
            }
            if (_chkAppMode != null && _chkAppMode.Checked != _chkAppFiltering.Checked)
            {
                _chkAppMode.Checked = _chkAppFiltering.Checked;
            }
        };

        var btnSelectApps = new Button
        {
            Text = "🎯 Select Apps (ON / OFF)...",
            Font = new Font("Segoe UI", 8.0f, FontStyle.Bold),
            Size = new Size(175, 24),
            Location = new Point(205, 90),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(51, 65, 85),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        btnSelectApps.FlatAppearance.BorderSize = 0;
        btnSelectApps.Click += (s, e) => ShowAppManagerView();

        var hintLabel = new Label
        {
            Text = "💡 Safe volume ceiling protects hearing. With App-Specific enabled, only chosen apps are clamped; bypassed music blasts at 100% full scale.",
            Font = new Font("Segoe UI", 8.0f, FontStyle.Italic),
            ForeColor = _textMuted,
            Location = new Point(12, 120),
            Size = new Size(368, 54)
        };

        _settingsCard.Controls.Add(_deviceInfoLabel);
        _settingsCard.Controls.Add(_chkStartup);
        _settingsCard.Controls.Add(_chkAllDevices);
        _settingsCard.Controls.Add(_chkAppFiltering);
        _settingsCard.Controls.Add(btnSelectApps);
        _settingsCard.Controls.Add(hintLabel);
        _mainViewPanel.Controls.Add(_settingsCard);
        this.Controls.Add(_mainViewPanel);

        // ==========================================
        // App Manager View Panel (In-window view swap)
        // ==========================================
        _appManagerViewPanel = new Panel
        {
            Location = new Point(0, 46),
            Size = new Size(420, 594),
            BackColor = Color.Transparent,
            Visible = false
        };

        var appModeCard = new Panel
        {
            Location = new Point(14, 10),
            Size = new Size(392, 58),
            BackColor = _bgCard
        };
        appModeCard.Paint += (s, e) => DrawCardBorder(e.Graphics, appModeCard.ClientRectangle);

        _chkAppMode = new CheckBox
        {
            Text = "Enable App-Specific Limiting Mode",
            Font = new Font("Segoe UI", 9.0f, FontStyle.Bold),
            ForeColor = _textWhite,
            Location = new Point(12, 8),
            Size = new Size(368, 22),
            Checked = _config.Settings.AppFilteringEnabled,
            Cursor = Cursors.Hand
        };
        _chkAppMode.CheckedChanged += (s, e) =>
        {
            if (_config.Settings.AppFilteringEnabled != _chkAppMode.Checked)
            {
                _engine.ToggleAppFiltering(_chkAppMode.Checked);
            }
            if (_chkAppFiltering != null && _chkAppFiltering.Checked != _chkAppMode.Checked)
            {
                _chkAppFiltering.Checked = _chkAppMode.Checked;
            }
            PopulateAppManagerRows();
        };

        var appModeSub = new Label
        {
            Text = "When ON, only apps marked LIMITED are clamped. Bypassed apps blast freely at 100%.",
            Font = new Font("Segoe UI", 7.5f),
            ForeColor = _textMuted,
            Location = new Point(12, 30),
            Size = new Size(368, 24)
        };
        appModeCard.Controls.Add(_chkAppMode);
        appModeCard.Controls.Add(appModeSub);

        _txtAppSearch = new TextBox
        {
            Location = new Point(14, 76),
            Size = new Size(248, 26),
            BackColor = _bgCard,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.0f),
            PlaceholderText = "🔍 Search applications..."
        };
        _txtAppSearch.TextChanged += (s, e) => PopulateAppManagerRows();

        _btnAddApp = new Button
        {
            Text = "➕ Add Process...",
            Location = new Point(268, 75),
            Size = new Size(138, 27),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(51, 65, 85),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8.0f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnAddApp.FlatAppearance.BorderSize = 0;
        _btnAddApp.Click += (s, e) => PromptAddProcess();

        _appsScrollPanel = new FlowLayoutPanel
        {
            Location = new Point(14, 108),
            Size = new Size(392, 434),
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.FromArgb(20, 28, 44),
            Padding = new Padding(4)
        };

        var btnBackBottom = new Button
        {
            Text = "← Back to Dashboard",
            Location = new Point(14, 548),
            Size = new Size(392, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(51, 65, 85),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.0f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnBackBottom.FlatAppearance.BorderSize = 0;
        btnBackBottom.Click += (s, e) => ShowMainView();

        _appManagerViewPanel.Controls.Add(appModeCard);
        _appManagerViewPanel.Controls.Add(_txtAppSearch);
        _appManagerViewPanel.Controls.Add(_btnAddApp);
        _appManagerViewPanel.Controls.Add(_appsScrollPanel);
        _appManagerViewPanel.Controls.Add(btnBackBottom);
        this.Controls.Add(_appManagerViewPanel);

        this.ResumeLayout(false);
    }

    public void ShowAppManagerView()
    {
        if (this.InvokeRequired)
        {
            this.BeginInvoke(new Action(ShowAppManagerView));
            return;
        }

        SafeShowNearTray();
        _mainViewPanel.Visible = false;
        _appManagerViewPanel.Visible = true;
        _backButton.Visible = true;
        _titleLabel.Text = "🎯 App Protection";
        _titleLabel.Location = new Point(78, 11);
        _subtitleLabel.Visible = false;
        _chkAppMode.Checked = _config.Settings.AppFilteringEnabled;

        PopulateAppManagerRows();
    }

    public void ShowMainView()
    {
        _appManagerViewPanel.Visible = false;
        _mainViewPanel.Visible = true;
        _backButton.Visible = false;
        _titleLabel.Text = "🛡️ PermadB";
        _titleLabel.Location = new Point(12, 11);
        _subtitleLabel.Visible = true;
        _chkAppFiltering.Checked = _config.Settings.AppFilteringEnabled;
    }

    private void PopulateAppManagerRows()
    {
        _appsScrollPanel.SuspendLayout();
        while (_appsScrollPanel.Controls.Count > 0)
        {
            var oldCtrl = _appsScrollPanel.Controls[0];
            _appsScrollPanel.Controls.RemoveAt(0);
            oldCtrl.Dispose();
        }

        var apps = _engine.GetDiscoverableAudioApps();
        var filter = _txtAppSearch?.Text.Trim() ?? "";

        int count = 0;
        foreach (var app in apps)
        {
            string procName = app.ProcessName;
            if (!string.IsNullOrEmpty(filter) &&
                !procName.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !app.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            count++;
            bool isProtected = _engine.IsAppProtected(procName);

            var rowPanel = new Panel
            {
                Size = new Size(364, 46),
                BackColor = _bgCard,
                Margin = new Padding(0, 0, 0, 5),
                Tag = procName
            };
            rowPanel.Paint += (s, e) => DrawCardBorder(e.Graphics, rowPanel.ClientRectangle);

            var nameLbl = new Label
            {
                Text = procName,
                Font = _fontAppName,
                ForeColor = _textWhite,
                Location = new Point(10, 6),
                Size = new Size(220, 18),
                AutoEllipsis = true
            };

            string status = isProtected ? "🛡️ Limited (Safe)" : "⚡ Bypassed (100% blast)";
            var statusLbl = new Label
            {
                Text = status,
                Font = _fontAppStatus,
                ForeColor = isProtected ? _accentGreen : _textMuted,
                Location = new Point(10, 25),
                Size = new Size(220, 16)
            };

            var toggleBtn = new Button
            {
                Text = isProtected ? "🛡️ LIMITED" : "⚡ BYPASSED",
                Font = _fontAppButton,
                Size = new Size(110, 28),
                Location = new Point(244, 9),
                FlatStyle = FlatStyle.Flat,
                BackColor = isProtected ? Color.FromArgb(16, 185, 129) : Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            toggleBtn.FlatAppearance.BorderSize = 0;
            toggleBtn.Click += (s, e) =>
            {
                bool nextProt = !_engine.IsAppProtected(procName);
                _engine.SetAppProtection(procName, nextProt);

                toggleBtn.BackColor = nextProt ? Color.FromArgb(16, 185, 129) : Color.FromArgb(51, 65, 85);
                toggleBtn.Text = nextProt ? "🛡️ LIMITED" : "⚡ BYPASSED";
                statusLbl.Text = nextProt ? "🛡️ Limited (Safe)" : "⚡ Bypassed (100% blast)";
                statusLbl.ForeColor = nextProt ? _accentGreen : _textMuted;
            };

            rowPanel.Controls.Add(nameLbl);
            rowPanel.Controls.Add(statusLbl);
            rowPanel.Controls.Add(toggleBtn);

            _appsScrollPanel.Controls.Add(rowPanel);
        }

        if (count == 0)
        {
            var emptyLabel = new Label
            {
                Text = "No applications found. Click '➕ Add Process' to configure any software!",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                ForeColor = _textMuted,
                Size = new Size(360, 48),
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(0, 30, 0, 0)
            };
            _appsScrollPanel.Controls.Add(emptyLabel);
        }

        _appsScrollPanel.ResumeLayout();
    }

    private void PromptAddProcess()
    {
        using var promptForm = new Form
        {
            Width = 380,
            Height = 180,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = "Add Application to Limiter",
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = _bgDark,
            ForeColor = Color.White
        };

        var lbl = new Label { Left = 20, Top = 16, Width = 320, Text = "Process Name (e.g. Discord, Spotify, cs2, chrome):", Font = new Font("Segoe UI", 8.5f) };
        var txt = new TextBox { Left = 20, Top = 40, Width = 320, BackColor = _bgCard, ForeColor = Color.White, Font = new Font("Segoe UI", 9.5f) };
        var chk = new CheckBox { Left = 20, Top = 72, Width = 320, Text = "Limit this app (Uncheck to bypass so music blasts)", Checked = true, Font = new Font("Segoe UI", 8.5f) };

        var btnOk = new Button { Text = "Add", Left = 170, Width = 80, Top = 104, DialogResult = DialogResult.OK, BackColor = _accentGreen, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        btnOk.FlatAppearance.BorderSize = 0;
        var btnCancel = new Button { Text = "Cancel", Left = 260, Width = 80, Top = 104, DialogResult = DialogResult.Cancel, BackColor = Color.FromArgb(51, 65, 85), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        btnCancel.FlatAppearance.BorderSize = 0;

        promptForm.Controls.Add(lbl);
        promptForm.Controls.Add(txt);
        promptForm.Controls.Add(chk);
        promptForm.Controls.Add(btnOk);
        promptForm.Controls.Add(btnCancel);
        promptForm.AcceptButton = btnOk;
        promptForm.CancelButton = btnCancel;

        if (promptForm.ShowDialog(this) == DialogResult.OK)
        {
            var pName = txt.Text.Trim();
            if (pName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                pName = pName.Substring(0, pName.Length - 4);
            }
            if (!string.IsNullOrWhiteSpace(pName))
            {
                _engine.SetAppProtection(pName, chk.Checked);
                PopulateAppManagerRows();
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Draw outer 1px border around the entire frameless form
        using var pen = new Pen(_borderDark, 1.5f);
        e.Graphics.DrawRectangle(pen, 0, 0, this.Width - 1, this.Height - 1);
    }

    private void DrawCardBorder(Graphics g, Rectangle bounds)
    {
        using var pen = new Pen(_borderDark, 1.0f);
        g.DrawRectangle(pen, 0, 0, bounds.Width - 1, bounds.Height - 1);
    }

    private void HeaderPanel_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ReleaseCapture();
            SendMessage(this.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            this.Hide();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void PowerToggleButton_Click(object? sender, EventArgs e)
    {
        _config.Settings.Enabled = !_config.Settings.Enabled;
        _config.Save();

        if (_config.Settings.Enabled)
        {
            _engine.EnforceVolumeCeiling();
        }

        UpdatePowerStatusUI();
    }

    private void UpdatePowerStatusUI()
    {
        bool isEnabled = _config.Settings.Enabled;
        if (isEnabled)
        {
            _powerStatusLabel.Text = "Protection: ACTIVE";
            _powerStatusLabel.ForeColor = _accentGreen;
            _powerSubLabel.Text = "Windows Volume: 100% • Limiter: Armed";
            _powerToggleButton.Text = "ACTIVE";
            _powerToggleButton.BackColor = Color.FromArgb(22, 101, 52);
            _powerToggleButton.ForeColor = Color.White;
        }
        else
        {
            _powerStatusLabel.Text = "Protection: PAUSED";
            _powerStatusLabel.ForeColor = _accentAmber;
            _powerSubLabel.Text = "Decibel limiter bypassed • Master unmanaged";
            _powerToggleButton.Text = "PAUSED";
            _powerToggleButton.BackColor = Color.FromArgb(71, 85, 105);
            _powerToggleButton.ForeColor = _textWhite;
        }
    }

    private void ApplyPreset(string preset)
    {
        _isUpdatingUi = true;
        try
        {
            _engine.ApplyPreset(preset);
            _lastKnownPreset = preset;
            var profile = _engine.GetActiveProfile();
            int sliderVal = (int)Math.Clamp(profile.SafeCeilingPercent, 10, 100);
            _customCeilingSlider.Value = sliderVal;
            _customCeilingLabel.Text = $"Custom Limiter Ceiling: {profile.SafeCeilingPercent:F0}% ({profile.TargetSafeDbSpl:F0} dBA)";
            UpdatePresetCards();
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void CustomCeilingSlider_Scroll(object? sender, EventArgs e)
    {
        if (_isUpdatingUi) return;
        var percent = _customCeilingSlider.Value;
        var estDb = 50.0f + (percent / 100.0f) * 38.0f;
        _customCeilingLabel.Text = $"Custom Ceiling: {percent}% ({estDb:F0} dBA)";
        _engine.SetSafeCeiling(percent);
        _lastKnownPreset = "custom";
        UpdatePresetCards();
    }

    private void SaveCurrentAsCustom(int slot)
    {
        var percent = _customCeilingSlider.Value;
        _engine.SaveCustomPreset(slot, percent);
        UpdateCustomCardDisplay();
        UpdatePresetCards();

        var btn = slot == 2 ? _saveCustom2Button : _saveCustom1Button;
        if (btn != null)
        {
            btn.Text = "✓ Saved";
            btn.ForeColor = _accentGreen;
            var timer = new System.Windows.Forms.Timer { Interval = 1500 };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                timer.Dispose();
                if (!btn.IsDisposed)
                {
                    btn.Text = slot == 2 ? "💾 Cust 2" : "💾 Cust 1";
                    btn.ForeColor = _accentSky;
                }
            };
            timer.Start();
        }
    }

    private void UpdateCustomCardDisplay()
    {
        if (_cardCustom1 != null)
        {
            var custom1Val = _config.Settings.Custom1PresetPercent;
            var custom1Spl = 50.0f + (custom1Val / 100.0f) * 38.0f;
            _cardCustom1.CeilingText = $"{custom1Val:F0}%";
            _cardCustom1.TagText = $"{custom1Spl:F0} dB • Lowest";
        }
        if (_cardCustom2 != null)
        {
            var custom2Val = _config.Settings.Custom2PresetPercent;
            var custom2Spl = 50.0f + (custom2Val / 100.0f) * 38.0f;
            _cardCustom2.CeilingText = $"{custom2Val:F0}%";
            _cardCustom2.TagText = $"{custom2Spl:F0} dB • Highest";
        }
    }

    private void UpdatePresetCards()
    {
        var preset = _config.Settings.ActivePreset;
        _cardSafe.IsActive = string.Equals(preset, "safe", StringComparison.OrdinalIgnoreCase);
        _cardNight.IsActive = string.Equals(preset, "night", StringComparison.OrdinalIgnoreCase);
        _cardStudio.IsActive = string.Equals(preset, "studio", StringComparison.OrdinalIgnoreCase);
        _cardCustom1.IsActive = string.Equals(preset, "custom1", StringComparison.OrdinalIgnoreCase) || string.Equals(preset, "custom", StringComparison.OrdinalIgnoreCase);
        _cardCustom2.IsActive = string.Equals(preset, "custom2", StringComparison.OrdinalIgnoreCase);

        UpdateCustomCardDisplay();

        _cardSafe.Invalidate();
        _cardNight.Invalidate();
        _cardStudio.Invalidate();
        _cardCustom1.Invalidate();
        _cardCustom2.Invalidate();
    }

    private void RefreshLiveMetrics()
    {
        if (!this.Visible) return;

        var m = _engine.LatestMetrics;
        var profile = _engine.GetActiveProfile();

        // Update meter
        _meterControl.UpdateMetrics(m);

        // Update device label
        var icon = profile.DeviceType == "headphones" ? "🎧" : "🔊";
        var devText = $"{icon} {profile.DeviceName} (Ceiling: {profile.SafeCeilingPercent:F0}%)";
        if (_deviceInfoLabel.Text != devText)
        {
            _deviceInfoLabel.Text = devText;
        }

        // Keep power status synced
        UpdatePowerStatusUI();

        // Sync preset cards if changed externally (e.g. from system tray context menu)
        var currentPreset = _config.Settings.ActivePreset;
        if (!_isUpdatingUi && !string.Equals(_lastKnownPreset, currentPreset, StringComparison.OrdinalIgnoreCase))
        {
            _lastKnownPreset = currentPreset;
            UpdatePresetCards();
            _isUpdatingUi = true;
            try
            {
                int targetVal = (int)Math.Clamp(profile.SafeCeilingPercent, 10, 100);
                if (_customCeilingSlider.Value != targetVal)
                {
                    _customCeilingSlider.Value = targetVal;
                }
                _customCeilingLabel.Text = $"Custom Ceiling: {profile.SafeCeilingPercent:F0}% ({profile.TargetSafeDbSpl:F0} dBA)";
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }
    }

    public void ShowNearTray()
    {
        // Position at bottom-right corner above system tray
        var screen = Screen.FromPoint(Cursor.Position);
        var wa = screen.WorkingArea;
        this.Location = new Point(wa.Right - this.Width - 14, wa.Bottom - this.Height - 14);

        if (!this.Visible)
        {
            this.Show();
        }
        this.WindowState = FormWindowState.Normal;
        this.BringToFront();
        this.Activate();

        UpdatePresetCards();
        RefreshLiveMetrics();
    }

    public void ToggleNearTray()
    {
        if (this.Visible && this.WindowState == FormWindowState.Normal)
        {
            this.Hide();
        }
        else
        {
            ShowNearTray();
        }
    }

    public void SafeShowNearTray()
    {
        if (this.IsDisposed) return;
        if (!this.IsHandleCreated) _ = this.Handle;

        if (this.InvokeRequired)
        {
            try { this.BeginInvoke(new Action(SafeShowNearTray)); } catch { }
            return;
        }
        ShowNearTray();
    }

    public void SafeToggleNearTray()
    {
        if (this.IsDisposed) return;
        if (!this.IsHandleCreated) _ = this.Handle;

        if (this.InvokeRequired)
        {
            try { this.BeginInvoke(new Action(SafeToggleNearTray)); } catch { }
            return;
        }
        ToggleNearTray();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (this.Visible)
        {
            _refreshTimer.Start();
            RefreshLiveMetrics();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }
}

// -----------------------------------------------------------------------------------------
// Custom Live Decibel Meter Control (GDI+ 30 FPS Double Buffered)
// -----------------------------------------------------------------------------------------
public class DecibelMeterControl : Panel
{
    private AudioMetrics _metrics = new();
    private float _displaySpl = 40.0f;
    private float _peakHold = 40.0f;
    private DateTime _lastPeakHoldTime = DateTime.UtcNow;

    private readonly Color _bgDark = Color.FromArgb(15, 23, 42);
    private readonly Color _borderDark = Color.FromArgb(51, 65, 85);
    private readonly Color _green = Color.FromArgb(34, 197, 94);
    private readonly Color _amber = Color.FromArgb(245, 158, 11);
    private readonly Color _red = Color.FromArgb(239, 68, 68);
    private readonly Color _sky = Color.FromArgb(56, 189, 248);
    private readonly Color _textWhite = Color.FromArgb(248, 250, 252);
    private readonly Color _textMuted = Color.FromArgb(148, 163, 184);

    // Cached GDI resources to eliminate allocation churn at 30 FPS
    private readonly Font _fontLarge = new("Segoe UI", 18.0f, FontStyle.Bold);
    private readonly Font _fontBadge = new("Segoe UI", 8.0f, FontStyle.Bold);
    private readonly Font _fontSub = new("Segoe UI", 8.0f, FontStyle.Regular);
    private readonly Pen _borderPen = new(Color.FromArgb(51, 65, 85), 1.0f);
    private readonly Pen _peakPen = new(Color.White, 2.0f);
    private readonly Pen _ceilingPen = new(Color.FromArgb(56, 189, 248), 2.0f) { DashStyle = DashStyle.Dot };
    private readonly SolidBrush _brushWhite = new(Color.FromArgb(248, 250, 252));
    private readonly SolidBrush _brushMuted = new(Color.FromArgb(148, 163, 184));
    private readonly SolidBrush _brushSky = new(Color.FromArgb(56, 189, 248));
    private readonly SolidBrush _bgGrooveBrush = new(Color.FromArgb(15, 23, 42));

    public DecibelMeterControl()
    {
        this.DoubleBuffered = true;
        this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public void UpdateMetrics(AudioMetrics m)
    {
        _metrics = m;

        // Smooth decaying display SPL
        if (m.EstimatedDbSpl > _displaySpl)
        {
            _displaySpl = m.EstimatedDbSpl; // fast attack
        }
        else
        {
            _displaySpl = Math.Max(m.EstimatedDbSpl, _displaySpl - 1.8f); // smooth decay
        }

        // Hard ceiling enforcement: when limiter is clamping, audio cannot exceed ceiling
        float ceilingSpl = 50.0f + (m.SafeCeilingPercent / 100.0f) * 38.0f;
        if (m.IsClamping && _displaySpl > ceilingSpl)
        {
            _displaySpl = ceilingSpl;
        }

        if (_displaySpl > _peakHold)
        {
            _peakHold = _displaySpl;
            _lastPeakHoldTime = DateTime.UtcNow;
        }
        else if ((DateTime.UtcNow - _lastPeakHoldTime).TotalSeconds > 1.5)
        {
            _peakHold = Math.Max(_displaySpl, _peakHold - 1.2f);
        }

        this.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fontLarge.Dispose();
            _fontBadge.Dispose();
            _fontSub.Dispose();
            _borderPen.Dispose();
            _peakPen.Dispose();
            _ceilingPen.Dispose();
            _brushWhite.Dispose();
            _brushMuted.Dispose();
            _brushSky.Dispose();
            _bgGrooveBrush.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Draw card border
        g.DrawRectangle(_borderPen, 0, 0, this.Width - 1, this.Height - 1);

        // 1. Decibel Readout Text
        var dbText = $"{_displaySpl:F1} dBA";
        g.DrawString(dbText, _fontLarge, _brushWhite, 12, 10);

        // 2. Status Badge Pill (Right side)
        string statusText;
        Color badgeBg;
        Color badgeFg = Color.White;

        float ceilingSpl = 50.0f + (_metrics.SafeCeilingPercent / 100.0f) * 38.0f;

        if (_metrics.IsClamping)
        {
            statusText = "⚡ LIMITER ACTIVE";
            badgeBg = _red;
        }
        else if (_displaySpl >= ceilingSpl - 1.5f)
        {
            statusText = "⚠️ AT CEILING";
            badgeBg = _amber;
        }
        else if (_displaySpl > 45.0f)
        {
            statusText = "✓ SAFE LEVEL";
            badgeBg = _green;
        }
        else
        {
            statusText = "QUIET";
            badgeBg = Color.FromArgb(51, 65, 85);
            badgeFg = _textMuted;
        }

        var badgeSize = g.MeasureString(statusText, _fontBadge);
        var badgeRect = new Rectangle(this.Width - (int)badgeSize.Width - 24, 16, (int)badgeSize.Width + 12, 22);

        using (var badgeBrush = new SolidBrush(badgeBg))
        {
            g.FillRectangle(badgeBrush, badgeRect);
        }
        using (var badgeTextBrush = new SolidBrush(badgeFg))
        {
            g.DrawString(statusText, _fontBadge, badgeTextBrush, badgeRect.X + 6, badgeRect.Y + 4);
        }

        // 3. Audio Meter Bar Gauge
        int barX = 14;
        int barY = 52;
        int barWidth = this.Width - 28;
        int barHeight = 16;

        // Background groove
        g.FillRectangle(_bgGrooveBrush, barX, barY, barWidth, barHeight);

        // Fill ratio (range 30 dBA to 105 dBA)
        float normalizedSpl = Math.Clamp((_displaySpl - 30.0f) / 75.0f, 0.0f, 1.0f);
        int fillWidth = (int)(barWidth * normalizedSpl);

        if (fillWidth > 2)
        {
            var fillRect = new Rectangle(barX, barY, fillWidth, barHeight);
            using var fillBrush = new LinearGradientBrush(
                new Point(barX, barY),
                new Point(barX + barWidth, barY),
                _green,
                _red
            );
            g.FillRectangle(fillBrush, fillRect);
        }

        // Peak hold tick mark
        float normPeak = Math.Clamp((_peakHold - 30.0f) / 75.0f, 0.0f, 1.0f);
        int peakX = barX + (int)(barWidth * normPeak);
        g.DrawLine(_peakPen, peakX, barY - 2, peakX, barY + barHeight + 2);

        // Limiter Ceiling Marker Line (e.g. 75 dBA)
        float normCeiling = Math.Clamp((ceilingSpl - 30.0f) / 75.0f, 0.0f, 1.0f);
        int ceilingX = barX + (int)(barWidth * normCeiling);
        g.DrawLine(_ceilingPen, ceilingX, barY - 4, ceilingX, barY + barHeight + 4);

        // Outer border on meter
        g.DrawRectangle(_borderPen, barX, barY, barWidth, barHeight);

        // 4. Subtext row
        var volText = $"Windows Volume: {_metrics.CurrentVolumePercent:F0}%";
        g.DrawString(volText, _fontSub, _brushMuted, 14, 76);

        var ceilingTag = $"▲ Limiter Ceiling: {_metrics.SafeCeilingPercent:F0}%";
        var tagSize = g.MeasureString(ceilingTag, _fontSub);
        int tagX = Math.Clamp(ceilingX - (int)(tagSize.Width / 2), 14, this.Width - (int)tagSize.Width - 14);
        g.DrawString(ceilingTag, _fontSub, _brushSky, tagX, 76);
    }
}

// -----------------------------------------------------------------------------------------
// Custom Preset Card Button Control
// -----------------------------------------------------------------------------------------
public class PresetCardControl : Panel
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Title { get; set; } = string.Empty;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string CeilingText { get; set; } = string.Empty;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string TagText { get; set; } = string.Empty;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool IsActive { get; set; }

    private readonly Action _onClick;
    private readonly Action? _onRightClick;
    private bool _isHovered = false;

    private readonly Color _bgCard = Color.FromArgb(30, 41, 59);
    private readonly Color _bgHover = Color.FromArgb(41, 55, 78);
    private readonly Color _bgActive = Color.FromArgb(12, 74, 110);   // Sky 900
    private readonly Color _borderDark = Color.FromArgb(51, 65, 85);
    private readonly Color _borderActive = Color.FromArgb(56, 189, 248); // Sky 400
    private readonly Color _textWhite = Color.FromArgb(248, 250, 252);
    private readonly Color _textMuted = Color.FromArgb(148, 163, 184);
    private readonly Color _sky = Color.FromArgb(56, 189, 248);

    // Cached GDI fonts and brushes (Zero GDI churn on hover/paint)
    private readonly Font _fontTitle = new("Segoe UI", 8.5f, FontStyle.Bold);
    private readonly Font _fontCheck = new("Segoe UI", 8.0f, FontStyle.Bold);
    private readonly Font _fontCeiling = new("Segoe UI", 12.0f, FontStyle.Bold);
    private readonly Font _fontTag = new("Segoe UI", 7.0f, FontStyle.Regular);
    private readonly SolidBrush _brushTitle = new(Color.FromArgb(248, 250, 252));
    private readonly SolidBrush _brushCheck = new(Color.FromArgb(56, 189, 248));
    private readonly SolidBrush _brushCeilingActive = new(Color.FromArgb(56, 189, 248));
    private readonly SolidBrush _brushCeilingNormal = new(Color.FromArgb(248, 250, 252));
    private readonly SolidBrush _brushTag = new(Color.FromArgb(148, 163, 184));
    private readonly Pen _penActive = new(Color.FromArgb(56, 189, 248), 2.0f);
    private readonly Pen _penHover = new(Color.FromArgb(56, 189, 248), 1.0f);
    private readonly Pen _penDark = new(Color.FromArgb(51, 65, 85), 1.0f);

    public PresetCardControl(string title, string ceilingText, string tagText, Action onClick, Action? onRightClick = null)
    {
        Title = title;
        CeilingText = ceilingText;
        TagText = tagText;
        _onClick = onClick;
        _onRightClick = onRightClick;

        this.DoubleBuffered = true;
        this.Cursor = Cursors.Hand;

        this.MouseEnter += (s, e) => { _isHovered = true; this.Invalidate(); };
        this.MouseLeave += (s, e) => { _isHovered = false; this.Invalidate(); };
        this.MouseUp += (s, e) =>
        {
            if (this.ClientRectangle.Contains(e.Location))
            {
                if (e.Button == MouseButtons.Left)
                {
                    _onClick();
                }
                else if (e.Button == MouseButtons.Right && _onRightClick != null)
                {
                    _onRightClick();
                }
            }
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fontTitle.Dispose();
            _fontCheck.Dispose();
            _fontCeiling.Dispose();
            _fontTag.Dispose();
            _brushTitle.Dispose();
            _brushCheck.Dispose();
            _brushCeilingActive.Dispose();
            _brushCeilingNormal.Dispose();
            _brushTag.Dispose();
            _penActive.Dispose();
            _penHover.Dispose();
            _penDark.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var bounds = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
        var bgColor = IsActive ? _bgActive : (_isHovered ? _bgHover : _bgCard);

        using (var bgBrush = new SolidBrush(bgColor))
        {
            g.FillRectangle(bgBrush, bounds);
        }

        var pen = IsActive ? _penActive : (_isHovered ? _penHover : _penDark);
        g.DrawRectangle(pen, bounds);

        // Title
        g.DrawString(Title, _fontTitle, _brushTitle, 8, 8);

        // Active check badge
        if (IsActive)
        {
            g.DrawString("✓", _fontCheck, _brushCheck, this.Width - 18, 8);
        }

        // Ceiling Text (e.g. 65%)
        g.DrawString(CeilingText, _fontCeiling, IsActive ? _brushCeilingActive : _brushCeilingNormal, 8, 30);

        // Tag (Recommended / Relaxed / Dynamic / Saved)
        g.DrawString(TagText, _fontTag, _brushTag, 8, 62);
    }
}
