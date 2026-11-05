using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PermadB.Audio;
using PermadB.Config;

namespace PermadB.Forms;

public class AppManagerForm : Form
{
    private readonly AudioEngine _engine;
    private readonly ConfigManager _config;

    // Window drag Win32 interop
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

    // Palette (matching PermadB dark theme)
    private readonly Color _bgDark = Color.FromArgb(15, 23, 42);       // Slate 900
    private readonly Color _bgCard = Color.FromArgb(30, 41, 59);       // Slate 800
    private readonly Color _borderDark = Color.FromArgb(51, 65, 85);   // Slate 700
    private readonly Color _accentGreen = Color.FromArgb(16, 185, 129); // Emerald 500
    private readonly Color _accentAmber = Color.FromArgb(245, 158, 11); // Amber 500
    private readonly Color _accentSky = Color.FromArgb(56, 189, 248);   // Sky 400
    private readonly Color _textWhite = Color.FromArgb(248, 250, 252);
    private readonly Color _textMuted = Color.FromArgb(148, 163, 184);

    private CheckBox _chkAppFiltering = null!;
    private TextBox _txtFilter = null!;
    private FlowLayoutPanel _appsListPanel = null!;
    private System.Windows.Forms.Timer _refreshTimer = null!;

    public AppManagerForm(AudioEngine engine, ConfigManager config)
    {
        _engine = engine;
        _config = config;

        InitializeComponent();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += (s, e) => RefreshAppRows();
        _refreshTimer.Start();

        this.FormClosed += (s, e) => _refreshTimer.Stop();
        RefreshAppRows();
    }

    private void InitializeComponent()
    {
        this.SuspendLayout();

        this.FormBorderStyle = FormBorderStyle.None;
        this.StartPosition = FormStartPosition.CenterParent;
        this.ShowInTaskbar = false;
        this.Size = new Size(460, 620);
        this.BackColor = _bgDark;
        this.ForeColor = _textWhite;
        this.Font = new Font("Segoe UI", 9.0f, FontStyle.Regular);
        this.DoubleBuffered = true;

        // 1. Header Panel
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = Color.FromArgb(20, 30, 48),
            Padding = new Padding(14, 0, 8, 0)
        };
        headerPanel.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        };

        var titleLabel = new Label
        {
            Text = "🎯 Application Protection Manager",
            Font = new Font("Segoe UI", 11.0f, FontStyle.Bold),
            ForeColor = _textWhite,
            AutoSize = true,
            Location = new Point(12, 12),
            Cursor = Cursors.SizeAll
        };
        titleLabel.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        };

        var closeButton = new Button
        {
            Text = "✕",
            Font = new Font("Segoe UI", 10.0f, FontStyle.Bold),
            Size = new Size(32, 28),
            Location = new Point(418, 10),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = _textMuted,
            Cursor = Cursors.Hand
        };
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(64, 75, 96);
        closeButton.Click += (s, e) => this.Close();

        headerPanel.Controls.Add(titleLabel);
        headerPanel.Controls.Add(closeButton);
        this.Controls.Add(headerPanel);

        // 2. Mode Card
        var modeCard = new Panel
        {
            Location = new Point(14, 56),
            Size = new Size(432, 68),
            BackColor = _bgCard
        };
        modeCard.Paint += (s, e) => DrawCardBorder(e.Graphics, modeCard.ClientRectangle);

        _chkAppFiltering = new CheckBox
        {
            Text = "Enable App-Specific Limiting Mode",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = _textWhite,
            Location = new Point(12, 10),
            Size = new Size(408, 24),
            Checked = _config.Settings.AppFilteringEnabled,
            Cursor = Cursors.Hand
        };
        _chkAppFiltering.CheckedChanged += (s, e) =>
        {
            _engine.ToggleAppFiltering(_chkAppFiltering.Checked);
            RefreshAppRows();
        };

        var modeSubtitle = new Label
        {
            Text = "When ON, only apps marked LIMITED are clamped. Bypassed apps blast freely at 100% full scale.",
            Font = new Font("Segoe UI", 8.0f),
            ForeColor = _textMuted,
            Location = new Point(12, 36),
            Size = new Size(408, 28)
        };

        modeCard.Controls.Add(_chkAppFiltering);
        modeCard.Controls.Add(modeSubtitle);
        this.Controls.Add(modeCard);

        // 3. Toolbar / Quick Action Presets
        var btnVoipOnly = new Button
        {
            Text = "🛡️ Limit All VoIP",
            Font = new Font("Segoe UI", 8.0f, FontStyle.Bold),
            Size = new Size(118, 28),
            Location = new Point(14, 132),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = _accentSky,
            Cursor = Cursors.Hand
        };
        btnVoipOnly.FlatAppearance.BorderColor = _borderDark;
        btnVoipOnly.Click += (s, e) =>
        {
            _engine.SetVoipAppsProtection(true);
            RefreshAppRows();
        };

        var btnBypassMusic = new Button
        {
            Text = "⚡ Bypass All Music",
            Font = new Font("Segoe UI", 8.0f, FontStyle.Bold),
            Size = new Size(130, 28),
            Location = new Point(138, 132),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = _accentAmber,
            Cursor = Cursors.Hand
        };
        btnBypassMusic.FlatAppearance.BorderColor = _borderDark;
        btnBypassMusic.Click += (s, e) =>
        {
            _engine.SetMusicAppsProtection(false);
            RefreshAppRows();
        };

        var btnAddCustom = new Button
        {
            Text = "➕ Add Process...",
            Font = new Font("Segoe UI", 8.0f, FontStyle.Bold),
            Size = new Size(122, 28),
            Location = new Point(274, 132),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = _textWhite,
            Cursor = Cursors.Hand
        };
        btnAddCustom.FlatAppearance.BorderColor = _borderDark;
        btnAddCustom.Click += (s, e) => PromptAddProcess();

        this.Controls.Add(btnVoipOnly);
        this.Controls.Add(btnBypassMusic);
        this.Controls.Add(btnAddCustom);

        // 4. Search Filter
        _txtFilter = new TextBox
        {
            Location = new Point(14, 168),
            Size = new Size(432, 26),
            BackColor = _bgCard,
            ForeColor = _textWhite,
            Font = new Font("Segoe UI", 9.0f),
            PlaceholderText = "🔍 Filter application name..."
        };
        _txtFilter.TextChanged += (s, e) => RefreshAppRows();
        this.Controls.Add(_txtFilter);

        // 5. Scrollable Apps List Panel
        _appsListPanel = new FlowLayoutPanel
        {
            Location = new Point(14, 202),
            Size = new Size(432, 360),
            BackColor = Color.FromArgb(20, 28, 44),
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(4)
        };
        this.Controls.Add(_appsListPanel);

        // 6. Bottom Done Button
        var btnDone = new Button
        {
            Text = "Done / Save",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Size = new Size(432, 34),
            Location = new Point(14, 572),
            FlatStyle = FlatStyle.Flat,
            BackColor = _accentGreen,
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        btnDone.FlatAppearance.BorderSize = 0;
        btnDone.Click += (s, e) => this.Close();
        this.Controls.Add(btnDone);

        this.ResumeLayout(false);
    }

    private void RefreshAppRows()
    {
        if (this.IsDisposed) return;

        var apps = _engine.GetDiscoverableAudioApps();
        var filter = _txtFilter.Text.Trim();

        _appsListPanel.SuspendLayout();
        _appsListPanel.Controls.Clear();

        int matchCount = 0;
        foreach (var app in apps)
        {
            string procName = app.ProcessName;
            if (!string.IsNullOrEmpty(filter) &&
                !procName.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !app.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matchCount++;
            bool isProtected = _engine.IsAppProtected(procName);

            var rowPanel = new Panel
            {
                Size = new Size(406, 50),
                BackColor = _bgCard,
                Margin = new Padding(0, 0, 0, 6)
            };
            rowPanel.Paint += (s, e) => DrawCardBorder(e.Graphics, rowPanel.ClientRectangle);

            // Icon Pill
            var pillLabel = new Label
            {
                Text = procName.Length >= 2 ? procName.Substring(0, 2).ToUpper() : procName.ToUpper(),
                Font = new Font("Segoe UI", 8.0f, FontStyle.Bold),
                ForeColor = _accentSky,
                BackColor = Color.FromArgb(15, 23, 42),
                Size = new Size(30, 30),
                Location = new Point(8, 10),
                TextAlign = ContentAlignment.MiddleCenter
            };

            // Name & Status
            var nameLabel = new Label
            {
                Text = procName,
                Font = new Font("Segoe UI", 9.0f, FontStyle.Bold),
                ForeColor = _textWhite,
                Location = new Point(44, 8),
                Size = new Size(220, 18),
                AutoEllipsis = true
            };

            string statusText = isProtected ? "🛡️ Limited (Safe)" : "⚡ Bypassed (Full Scale)";
            if (app.PeakValue > 0.001f)
            {
                statusText += $" • ~{app.EstimatedDbSpl:F0} dBA";
            }
            var subLabel = new Label
            {
                Text = statusText,
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = isProtected ? _accentGreen : _textMuted,
                Location = new Point(44, 28),
                Size = new Size(220, 16)
            };

            // ON / OFF Toggle Button
            var toggleBtn = new Button
            {
                Text = isProtected ? "🛡️ LIMITED" : "⚡ BYPASSED",
                Font = new Font("Segoe UI", 8.0f, FontStyle.Bold),
                Size = new Size(116, 32),
                Location = new Point(278, 9),
                FlatStyle = FlatStyle.Flat,
                BackColor = isProtected ? Color.FromArgb(16, 185, 129) : Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            toggleBtn.FlatAppearance.BorderSize = 0;
            toggleBtn.Click += (s, e) =>
            {
                bool newProt = !_engine.IsAppProtected(procName);
                _engine.SetAppProtection(procName, newProt);
                RefreshAppRows();
            };

            rowPanel.Controls.Add(pillLabel);
            rowPanel.Controls.Add(nameLabel);
            rowPanel.Controls.Add(subLabel);
            rowPanel.Controls.Add(toggleBtn);

            _appsListPanel.Controls.Add(rowPanel);
        }

        if (matchCount == 0)
        {
            var emptyLabel = new Label
            {
                Text = "No applications match your search. Click '➕ Add Process' above to add any software!",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                ForeColor = _textMuted,
                Size = new Size(400, 48),
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(0, 40, 0, 0)
            };
            _appsListPanel.Controls.Add(emptyLabel);
        }

        _appsListPanel.ResumeLayout();
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
                RefreshAppRows();
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(_borderDark, 1.5f);
        e.Graphics.DrawRectangle(pen, 0, 0, this.Width - 1, this.Height - 1);
    }

    private void DrawCardBorder(Graphics g, Rectangle bounds)
    {
        using var pen = new Pen(_borderDark, 1.0f);
        g.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
    }
}
