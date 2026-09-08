using System;
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

    private Panel _customCeilingCard = null!;
    private Label _customCeilingLabel = null!;
    private TrackBar _customCeilingSlider = null!;

    private Panel _settingsCard = null!;
    private Label _deviceInfoLabel = null!;
    private CheckBox _chkAllDevices = null!;
    private CheckBox _chkStartup = null!;

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
        _refreshTimer.Start();

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
        this.Size = new Size(420, 620);
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

        _headerPanel.Controls.Add(_titleLabel);
        _headerPanel.Controls.Add(_subtitleLabel);
        _headerPanel.Controls.Add(_closeButton);
        this.Controls.Add(_headerPanel);

        // 2. Power / Status Banner
        _powerCard = new Panel
        {
            Location = new Point(14, 56),
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
        this.Controls.Add(_powerCard);

        // 3. Live Decibel & Limiter Meter
        _meterControl = new DecibelMeterControl
        {
            Location = new Point(14, 118),
            Size = new Size(392, 116),
            BackColor = _bgCard
        };
        this.Controls.Add(_meterControl);

        // 4. Limiter Ceiling Presets
        var presetHeaderLabel = new Label
        {
            Text = "LIMITER CEILING PRESETS",
            Font = new Font("Segoe UI", 8.0f, FontStyle.Bold),
            ForeColor = _textMuted,
            Location = new Point(16, 242),
            AutoSize = true
        };
        this.Controls.Add(presetHeaderLabel);

        _presetContainer = new Panel
        {
            Location = new Point(14, 260),
            Size = new Size(392, 94),
            BackColor = Color.Transparent
        };

        _cardSafe = new PresetCardControl(
            title: "Safe Ears",
            ceilingText: "65% (75 dBA)",
            tagText: "Recommended",
            onClick: () => ApplyPreset("safe")
        )
        {
            Location = new Point(0, 0),
            Size = new Size(126, 94)
        };

        _cardNight = new PresetCardControl(
            title: "Night Mode",
            ceilingText: "50% (68 dBA)",
            tagText: "Relaxed",
            onClick: () => ApplyPreset("night")
        )
        {
            Location = new Point(133, 0),
            Size = new Size(126, 94)
        };

        _cardStudio = new PresetCardControl(
            title: "Studio",
            ceilingText: "85% (82 dBA)",
            tagText: "Dynamic",
            onClick: () => ApplyPreset("studio")
        )
        {
            Location = new Point(266, 0),
            Size = new Size(126, 94)
        };

        _presetContainer.Controls.Add(_cardSafe);
        _presetContainer.Controls.Add(_cardNight);
        _presetContainer.Controls.Add(_cardStudio);
        this.Controls.Add(_presetContainer);

        // 5. Custom Limiter Ceiling Slider Card
        _customCeilingCard = new Panel
        {
            Location = new Point(14, 362),
            Size = new Size(392, 70),
            BackColor = _bgCard
        };
        _customCeilingCard.Paint += (s, e) => DrawCardBorder(e.Graphics, _customCeilingCard.ClientRectangle);

        _customCeilingLabel = new Label
        {
            Text = "Custom Limiter Ceiling: 65% (75 dBA)",
            Font = new Font("Segoe UI", 9.0f, FontStyle.Bold),
            ForeColor = _textWhite,
            Location = new Point(12, 8),
            AutoSize = true
        };

        _customCeilingSlider = new TrackBar
        {
            Minimum = 30,
            Maximum = 95,
            TickFrequency = 5,
            Value = (int)Math.Clamp(_engine.GetActiveProfile().SafeCeilingPercent, 30, 95),
            Location = new Point(8, 28),
            Size = new Size(374, 34),
            BackColor = _bgCard,
            Cursor = Cursors.Hand
        };
        _customCeilingSlider.Scroll += CustomCeilingSlider_Scroll;

        _customCeilingCard.Controls.Add(_customCeilingLabel);
        _customCeilingCard.Controls.Add(_customCeilingSlider);
        this.Controls.Add(_customCeilingCard);

        // 6. Device Info & Quick Settings Card
        _settingsCard = new Panel
        {
            Location = new Point(14, 440),
            Size = new Size(392, 166),
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

        var hintLabel = new Label
        {
            Text = "💡 Windows audio stays at 100%. The 10ms Master Limiter automatically prevents hearing damage without altering individual app volume levels or microphones.",
            Font = new Font("Segoe UI", 8.0f, FontStyle.Italic),
            ForeColor = _textMuted,
            Location = new Point(12, 100),
            Size = new Size(368, 56)
        };

        _settingsCard.Controls.Add(_deviceInfoLabel);
        _settingsCard.Controls.Add(_chkStartup);
        _settingsCard.Controls.Add(_chkAllDevices);
        _settingsCard.Controls.Add(hintLabel);
        this.Controls.Add(_settingsCard);

        this.ResumeLayout(false);
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
            int sliderVal = (int)Math.Clamp(profile.SafeCeilingPercent, 30, 95);
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
        _customCeilingLabel.Text = $"Custom Limiter Ceiling: {percent}% ({estDb:F0} dBA)";
        _engine.SetSafeCeiling(percent);
        _lastKnownPreset = "custom";
        UpdatePresetCards();
    }

    private void UpdatePresetCards()
    {
        var preset = _config.Settings.ActivePreset;
        _cardSafe.IsActive = string.Equals(preset, "safe", StringComparison.OrdinalIgnoreCase);
        _cardNight.IsActive = string.Equals(preset, "night", StringComparison.OrdinalIgnoreCase);
        _cardStudio.IsActive = string.Equals(preset, "studio", StringComparison.OrdinalIgnoreCase);

        _cardSafe.Invalidate();
        _cardNight.Invalidate();
        _cardStudio.Invalidate();
    }

    private void RefreshLiveMetrics()
    {
        var m = _engine.LatestMetrics;
        var profile = _engine.GetActiveProfile();

        // Update meter
        _meterControl.UpdateMetrics(m);

        // Update device label
        var icon = profile.DeviceType == "headphones" ? "🎧" : "🔊";
        var devText = $"{icon} {profile.DeviceName} (Baseline: 100%)";
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
                int targetVal = (int)Math.Clamp(profile.SafeCeilingPercent, 30, 95);
                if (_customCeilingSlider.Value != targetVal)
                {
                    _customCeilingSlider.Value = targetVal;
                }
                _customCeilingLabel.Text = $"Custom Limiter Ceiling: {profile.SafeCeilingPercent:F0}% ({profile.TargetSafeDbSpl:F0} dBA)";
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Draw card border
        using (var borderPen = new Pen(_borderDark, 1.0f))
        {
            g.DrawRectangle(borderPen, 0, 0, this.Width - 1, this.Height - 1);
        }

        // 1. Decibel Readout Text
        var dbText = $"{_displaySpl:F1} dBA";
        using var fontLarge = new Font("Segoe UI", 18.0f, FontStyle.Bold);
        using var brushWhite = new SolidBrush(_textWhite);
        g.DrawString(dbText, fontLarge, brushWhite, 12, 10);

        // 2. Status Badge Pill (Right side)
        string statusText;
        Color badgeBg;
        Color badgeFg = Color.White;

        if (_metrics.IsClamping)
        {
            statusText = "⚡ LIMITER ACTIVE";
            badgeBg = _red;
        }
        else if (_displaySpl >= 78.0f)
        {
            statusText = "⚠️ NEAR CEILING";
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

        using var fontBadge = new Font("Segoe UI", 8.0f, FontStyle.Bold);
        var badgeSize = g.MeasureString(statusText, fontBadge);
        var badgeRect = new Rectangle(this.Width - (int)badgeSize.Width - 24, 16, (int)badgeSize.Width + 12, 22);

        using (var badgeBrush = new SolidBrush(badgeBg))
        {
            g.FillRectangle(badgeBrush, badgeRect);
        }
        using (var badgeTextBrush = new SolidBrush(badgeFg))
        {
            g.DrawString(statusText, fontBadge, badgeTextBrush, badgeRect.X + 6, badgeRect.Y + 4);
        }

        // 3. Audio Meter Bar Gauge
        int barX = 14;
        int barY = 52;
        int barWidth = this.Width - 28;
        int barHeight = 16;

        // Background groove
        using (var bgGroove = new SolidBrush(_bgDark))
        {
            g.FillRectangle(bgGroove, barX, barY, barWidth, barHeight);
        }

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
        using (var peakPen = new Pen(Color.White, 2.0f))
        {
            g.DrawLine(peakPen, peakX, barY - 2, peakX, barY + barHeight + 2);
        }

        // Limiter Ceiling Marker Line (e.g. 75 dBA)
        float ceilingSpl = 50.0f + (_metrics.SafeCeilingPercent / 100.0f) * 38.0f;
        float normCeiling = Math.Clamp((ceilingSpl - 30.0f) / 75.0f, 0.0f, 1.0f);
        int ceilingX = barX + (int)(barWidth * normCeiling);

        using (var ceilingPen = new Pen(_sky, 2.0f) { DashStyle = DashStyle.Dot })
        {
            g.DrawLine(ceilingPen, ceilingX, barY - 4, ceilingX, barY + barHeight + 4);
        }

        // Outer border on meter
        using (var meterPen = new Pen(_borderDark, 1.0f))
        {
            g.DrawRectangle(meterPen, barX, barY, barWidth, barHeight);
        }

        // 4. Subtext row
        using var fontSub = new Font("Segoe UI", 8.0f, FontStyle.Regular);
        using var brushMuted = new SolidBrush(_textMuted);

        var volText = $"Windows Volume: {_metrics.CurrentVolumePercent:F0}%";
        g.DrawString(volText, fontSub, brushMuted, 14, 76);

        var ceilingTag = $"▲ Limiter Ceiling: {_metrics.SafeCeilingPercent:F0}%";
        var tagSize = g.MeasureString(ceilingTag, fontSub);
        int tagX = Math.Clamp(ceilingX - (int)(tagSize.Width / 2), 14, this.Width - (int)tagSize.Width - 14);
        using var brushSky = new SolidBrush(_sky);
        g.DrawString(ceilingTag, fontSub, brushSky, tagX, 76);

        var clampStats = $"⚡ {_metrics.SpikesClampedTotal} spikes clamped";
        var clampSize = g.MeasureString(clampStats, fontSub);
        g.DrawString(clampStats, fontSub, brushMuted, this.Width - clampSize.Width - 14, 94);
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
    private bool _isHovered = false;

    private readonly Color _bgCard = Color.FromArgb(30, 41, 59);
    private readonly Color _bgHover = Color.FromArgb(41, 55, 78);
    private readonly Color _bgActive = Color.FromArgb(12, 74, 110);   // Sky 900
    private readonly Color _borderDark = Color.FromArgb(51, 65, 85);
    private readonly Color _borderActive = Color.FromArgb(56, 189, 248); // Sky 400
    private readonly Color _textWhite = Color.FromArgb(248, 250, 252);
    private readonly Color _textMuted = Color.FromArgb(148, 163, 184);
    private readonly Color _sky = Color.FromArgb(56, 189, 248);

    public PresetCardControl(string title, string ceilingText, string tagText, Action onClick)
    {
        Title = title;
        CeilingText = ceilingText;
        TagText = tagText;
        _onClick = onClick;

        this.DoubleBuffered = true;
        this.Cursor = Cursors.Hand;

        this.MouseEnter += (s, e) => { _isHovered = true; this.Invalidate(); };
        this.MouseLeave += (s, e) => { _isHovered = false; this.Invalidate(); };
        this.MouseUp += (s, e) =>
        {
            if (e.Button == MouseButtons.Left && this.ClientRectangle.Contains(e.Location))
            {
                _onClick();
            }
        };
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

        var borderColor = IsActive ? _borderActive : (_isHovered ? _sky : _borderDark);
        using (var pen = new Pen(borderColor, IsActive ? 2.0f : 1.0f))
        {
            g.DrawRectangle(pen, bounds);
        }

        // Title
        using var fontTitle = new Font("Segoe UI", 9.0f, FontStyle.Bold);
        using var brushTitle = new SolidBrush(_textWhite);
        g.DrawString(Title, fontTitle, brushTitle, 8, 10);

        // Active check badge
        if (IsActive)
        {
            using var fontCheck = new Font("Segoe UI", 8.0f, FontStyle.Bold);
            using var brushCheck = new SolidBrush(_borderActive);
            g.DrawString("✓", fontCheck, brushCheck, this.Width - 18, 10);
        }

        // Ceiling Text (e.g. 65% / 75 dBA)
        using var fontCeiling = new Font("Segoe UI", 10.0f, FontStyle.Bold);
        using var brushCeiling = new SolidBrush(IsActive ? _borderActive : _textWhite);
        g.DrawString(CeilingText, fontCeiling, brushCeiling, 8, 32);

        // Tag (Recommended / Relaxed / Dynamic)
        using var fontTag = new Font("Segoe UI", 7.5f, FontStyle.Regular);
        using var brushTag = new SolidBrush(_textMuted);
        g.DrawString(TagText, fontTag, brushTag, 8, 62);
    }
}
