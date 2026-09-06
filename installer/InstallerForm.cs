using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PermadB.Installer;

public class InstallerForm : Form
{
    private TextBox _txtPath = null!;
    private Button _btnBrowse = null!;
    private CheckBox _chkDesktop = null!;
    private CheckBox _chkStartMenu = null!;
    private CheckBox _chkStartup = null!;
    private CheckBox _chkLaunch = null!;
    private ProgressBar _progressBar = null!;
    private Label _lblStatus = null!;
    private Button _btnInstall = null!;
    private Button _btnCancel = null!;
    private bool _isCompleted = false;

    public InstallerForm()
    {
        InitializeUi();
    }

    private void InitializeUi()
    {
        Text = "PermadB Setup - Permanent Audio Decibel Guard";
        Size = new Size(540, 460);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        BackColor = Color.FromArgb(15, 23, 42); // Slate 900
        ForeColor = Color.FromArgb(248, 250, 252);
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

        // Header Banner
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 85,
            BackColor = Color.FromArgb(30, 41, 59)
        };
        headerPanel.Paint += (s, e) =>
        {
            using var brush = new LinearGradientBrush(headerPanel.ClientRectangle, Color.FromArgb(16, 185, 129), Color.FromArgb(5, 150, 105), 135f);
            using var path = new GraphicsPath();
            // Draw small shield icon on banner
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillEllipse(new SolidBrush(Color.FromArgb(20, 16, 185, 129)), 18, 18, 48, 48);
        };

        var lblTitle = new Label
        {
            Text = "🛡️ PermadB Setup Wizard",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(24, 16),
            AutoSize = true
        };

        var lblSubtitle = new Label
        {
            Text = "Permanent decibel & ear protection guard for Windows 11 / 10",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(148, 163, 184),
            Location = new Point(26, 48),
            AutoSize = true
        };

        headerPanel.Controls.Add(lblTitle);
        headerPanel.Controls.Add(lblSubtitle);
        Controls.Add(headerPanel);

        // Destination Folder Group
        var lblDest = new Label
        {
            Text = "Destination Directory:",
            Location = new Point(25, 105),
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        Controls.Add(lblDest);

        _txtPath = new TextBox
        {
            Text = InstallerEngine.GetDefaultInstallDir(),
            Location = new Point(25, 130),
            Width = 370,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(_txtPath);

        _btnBrowse = new Button
        {
            Text = "Browse...",
            Location = new Point(405, 128),
            Width = 95,
            Height = 27,
            BackColor = Color.FromArgb(51, 65, 85),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnBrowse.FlatAppearance.BorderSize = 0;
        _btnBrowse.Click += (s, e) =>
        {
            using var fbd = new FolderBrowserDialog();
            fbd.SelectedPath = _txtPath.Text;
            if (fbd.ShowDialog(this) == DialogResult.OK)
            {
                _txtPath.Text = Path.Combine(fbd.SelectedPath, "PermadB");
            }
        };
        Controls.Add(_btnBrowse);

        // Options
        int optY = 175;
        _chkDesktop = CreateOptionCheckbox("Create Desktop Shortcut", optY, true);
        _chkStartMenu = CreateOptionCheckbox("Create Start Menu Shortcut", optY += 30, true);
        _chkStartup = CreateOptionCheckbox("Start PermadB automatically when Windows boots", optY += 30, true);
        _chkLaunch = CreateOptionCheckbox("Launch PermadB immediately in system tray", optY += 30, true);

        // Progress Bar
        _progressBar = new ProgressBar
        {
            Location = new Point(25, 320),
            Width = 475,
            Height = 16,
            Visible = false
        };
        Controls.Add(_progressBar);

        _lblStatus = new Label
        {
            Text = "Ready to install PermadB.",
            Location = new Point(25, 342),
            Width = 475,
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Segoe UI", 8.5f)
        };
        Controls.Add(_lblStatus);

        // Bottom Action Buttons
        _btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(310, 375),
            Width = 90,
            Height = 32,
            BackColor = Color.FromArgb(51, 65, 85),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnCancel.FlatAppearance.BorderSize = 0;
        _btnCancel.Click += (s, e) => Close();
        Controls.Add(_btnCancel);

        _btnInstall = new Button
        {
            Text = "Install",
            Location = new Point(410, 375),
            Width = 90,
            Height = 32,
            BackColor = Color.FromArgb(16, 185, 129),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat
        };
        _btnInstall.FlatAppearance.BorderSize = 0;
        _btnInstall.Click += async (s, e) =>
        {
            if (_isCompleted)
            {
                Close();
                return;
            }

            await StartInstallationAsync();
        };
        Controls.Add(_btnInstall);
    }

    private CheckBox CreateOptionCheckbox(string text, int top, bool defaultChecked)
    {
        var chk = new CheckBox
        {
            Text = text,
            Location = new Point(28, top),
            AutoSize = true,
            Checked = defaultChecked,
            ForeColor = Color.FromArgb(226, 232, 240)
        };
        Controls.Add(chk);
        return chk;
    }

    private async Task StartInstallationAsync()
    {
        var targetDir = _txtPath.Text.Trim();
        if (string.IsNullOrEmpty(targetDir))
        {
            MessageBox.Show("Please specify a valid installation directory.", "Invalid Path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _btnInstall.Enabled = false;
        _btnCancel.Enabled = false;
        _btnBrowse.Enabled = false;
        _txtPath.Enabled = false;
        _chkDesktop.Enabled = false;
        _chkStartMenu.Enabled = false;
        _chkStartup.Enabled = false;
        _chkLaunch.Enabled = false;

        _progressBar.Visible = true;
        _progressBar.Value = 10;

        try
        {
            await Task.Run(() =>
            {
                InstallerEngine.Install(
                    targetDir,
                    _chkDesktop.Checked,
                    _chkStartMenu.Checked,
                    _chkStartup.Checked,
                    _chkLaunch.Checked,
                    (statusMsg, pct) =>
                    {
                        Invoke(() =>
                        {
                            _lblStatus.Text = statusMsg;
                            _progressBar.Value = Math.Clamp(pct, 0, 100);
                        });
                    }
                );
            });

            _lblStatus.Text = "✓ Installation Complete! PermadB is running in your Windows tray.";
            _lblStatus.ForeColor = Color.FromArgb(52, 211, 153);
            _progressBar.Value = 100;

            _isCompleted = true;
            _btnInstall.Text = "Finish";
            _btnInstall.Enabled = true;
            _btnCancel.Visible = false;
        }
        catch (Exception ex)
        {
            _lblStatus.Text = $"Error: {ex.Message}";
            _lblStatus.ForeColor = Color.FromArgb(239, 68, 68);
            MessageBox.Show($"Installation failed: {ex.Message}", "Installation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _btnInstall.Enabled = true;
            _btnCancel.Enabled = true;
        }
    }
}
