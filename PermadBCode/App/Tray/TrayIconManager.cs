using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using PermadB.Audio;
using PermadB.Config;

namespace PermadB.Tray;

public class TrayIconManager : IDisposable
{
    private readonly ConfigManager _config;
    private readonly AudioEngine _engine;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;

    // Menu items to update dynamically
    private ToolStripMenuItem _headerItem = null!;
    private ToolStripMenuItem _enabledItem = null!;
    private ToolStripMenuItem _deviceInfoItem = null!;
    private ToolStripMenuItem _safePresetItem = null!;
    private ToolStripMenuItem _nightPresetItem = null!;
    private ToolStripMenuItem _studioPresetItem = null!;
    private ToolStripMenuItem _custom1PresetItem = null!;
    private ToolStripMenuItem _custom2PresetItem = null!;
    private ToolStripMenuItem _saveCustom1Item = null!;
    private ToolStripMenuItem _saveCustom2Item = null!;
    private ToolStripMenuItem _strictLockItem = null!;
    private ToolStripMenuItem _allDevicesItem = null!;
    private ToolStripMenuItem _startupItem = null!;

    public event Action? OnOpenDashboardRequested;
    public event Action? OnExitRequested;

    public TrayIconManager(ConfigManager config, AudioEngine engine)
    {
        _config = config;
        _engine = engine;

        _contextMenu = new ContextMenuStrip();
        _contextMenu.RenderMode = ToolStripRenderMode.System;

        _notifyIcon = new NotifyIcon
        {
            Visible = false,
            ContextMenuStrip = _contextMenu
        };

        BuildContextMenu();
        UpdateTrayAppearance();

        // Left click opens dashboard
        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                OnOpenDashboardRequested?.Invoke();
            }
        };

        _notifyIcon.DoubleClick += (s, e) =>
        {
            OnOpenDashboardRequested?.Invoke();
        };

        // Listen for engine and config updates
        _engine.OnActiveDeviceChanged += _ => UpdateTrayAppearance();
        _engine.OnDevicesUpdated += UpdateTrayAppearance;
        _config.OnSettingsChanged += _ => UpdateTrayAppearance();

        // Make visible in system tray only after Icon, Text, and Menu are properly configured
        _notifyIcon.Visible = true;
    }

    private void BuildContextMenu()
    {
        _contextMenu.Items.Clear();

        // Title Header
        _headerItem = new ToolStripMenuItem("🛡️ PermadB - Safe Audio Guard")
        {
            Enabled = false,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
        };
        _contextMenu.Items.Add(_headerItem);

        // Enable / Disable Active Guard
        _enabledItem = new ToolStripMenuItem("Protection: ACTIVE", null, (s, e) =>
        {
            _config.Settings.Enabled = !_config.Settings.Enabled;
            _config.Save();
            if (_config.Settings.Enabled)
            {
                _engine.EnforceVolumeCeiling();
            }
            UpdateTrayAppearance();
        });
        _contextMenu.Items.Add(_enabledItem);

        // Active Device info item
        _deviceInfoItem = new ToolStripMenuItem("Device: Detecting...")
        {
            Enabled = false
        };
        _contextMenu.Items.Add(_deviceInfoItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        // Presets submenu
        _safePresetItem = new ToolStripMenuItem("Safe Ears (65% Ceiling / 75 dBA)", null, (s, e) =>
        {
            _engine.ApplyPreset("safe");
            UpdateTrayAppearance();
            ShowNotification("Safe Ears Activated", "Volume ceiling set to safe level (65% / 75 dBA).");
        });
        _contextMenu.Items.Add(_safePresetItem);

        _nightPresetItem = new ToolStripMenuItem("Night Mode (50% Ceiling / 68 dBA)", null, (s, e) =>
        {
            _engine.ApplyPreset("night");
            UpdateTrayAppearance();
            ShowNotification("Night Mode Activated", "Volume ceiling set to fatigue-free level (50% / 68 dBA).");
        });
        _contextMenu.Items.Add(_nightPresetItem);

        _studioPresetItem = new ToolStripMenuItem("Studio Mode (85% Ceiling / 82 dBA)", null, (s, e) =>
        {
            _engine.ApplyPreset("studio");
            UpdateTrayAppearance();
            ShowNotification("Studio Mode Activated", "Volume ceiling set to high dynamic range (85% / 82 dBA).");
        });
        _contextMenu.Items.Add(_studioPresetItem);

        // Custom 1 Preset (Lowest / Saved)
        float custom1Percent = _config.Settings.Custom1PresetPercent;
        float custom1Spl = 50.0f + (custom1Percent / 100.0f) * 38.0f;
        _custom1PresetItem = new ToolStripMenuItem($"Custom 1 - Lowest ({custom1Percent:F0}% Ceiling / {custom1Spl:F0} dBA)", null, (s, e) =>
        {
            _engine.ApplyPreset("custom1");
            UpdateTrayAppearance();
            ShowNotification("Custom 1 Activated", $"Volume ceiling set to Custom 1 Lowest ({_config.Settings.Custom1PresetPercent:F0}%).");
        });
        _contextMenu.Items.Add(_custom1PresetItem);

        // Custom 2 Preset (Highest / Saved)
        float custom2Percent = _config.Settings.Custom2PresetPercent;
        float custom2Spl = 50.0f + (custom2Percent / 100.0f) * 38.0f;
        _custom2PresetItem = new ToolStripMenuItem($"Custom 2 - Highest ({custom2Percent:F0}% Ceiling / {custom2Spl:F0} dBA)", null, (s, e) =>
        {
            _engine.ApplyPreset("custom2");
            UpdateTrayAppearance();
            ShowNotification("Custom 2 Activated", $"Volume ceiling set to Custom 2 Highest ({_config.Settings.Custom2PresetPercent:F0}%).");
        });
        _contextMenu.Items.Add(_custom2PresetItem);

        _saveCustom1Item = new ToolStripMenuItem("💾 Save Current Ceiling as Custom 1 (Lowest)", null, (s, e) =>
        {
            var profile = _engine.GetActiveProfile();
            _engine.SaveCustomPreset(1, profile.SafeCeilingPercent);
            UpdateTrayAppearance();
            ShowNotification("Custom 1 Saved", $"Saved {profile.SafeCeilingPercent:F0}% as Custom 1 (Lowest).");
        });
        _contextMenu.Items.Add(_saveCustom1Item);

        _saveCustom2Item = new ToolStripMenuItem("💾 Save Current Ceiling as Custom 2 (Highest)", null, (s, e) =>
        {
            var profile = _engine.GetActiveProfile();
            _engine.SaveCustomPreset(2, profile.SafeCeilingPercent);
            UpdateTrayAppearance();
            ShowNotification("Custom 2 Saved", $"Saved {profile.SafeCeilingPercent:F0}% as Custom 2 (Highest).");
        });
        _contextMenu.Items.Add(_saveCustom2Item);

        _contextMenu.Items.Add(new ToolStripSeparator());

        // Open Dashboard
        var dashboardItem = new ToolStripMenuItem("📊 Open PermadB Dashboard...", null, (s, e) =>
        {
            OnOpenDashboardRequested?.Invoke();
        })
        {
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
        };
        _contextMenu.Items.Add(dashboardItem);

        var donateItem = new ToolStripMenuItem("☕ Support on Ko-fi...", null, (s, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://ko-fi.com/unsever") { UseShellExecute = true });
            }
            catch { }
        });
        _contextMenu.Items.Add(donateItem);

        // Strict Ceiling Guard
        _strictLockItem = new ToolStripMenuItem("🔒 Strict Ceiling Guard", null, (s, e) =>
        {
            _config.Settings.StrictVolumeLock = !_config.Settings.StrictVolumeLock;
            _config.Save();
            if (_config.Settings.StrictVolumeLock)
            {
                _engine.EnforceVolumeCeiling();
                ShowNotification("Strict Ceiling Guard Active", "Volume cannot exceed your safe ceiling. Lower volume is always allowed!");
            }
            else
            {
                ShowNotification("Ceiling Guard Soft Mode", "Strict ceiling enforcement is paused.");
            }
            UpdateTrayAppearance();
        });
        _contextMenu.Items.Add(_strictLockItem);

        // Protect All Devices (PA / DJ / Headphones)
        _allDevicesItem = new ToolStripMenuItem("🌐 Protect All Devices (PA/DJ/Headphones)", null, (s, e) =>
        {
            _config.Settings.ApplyToAllDevices = !_config.Settings.ApplyToAllDevices;
            _config.Save();
            if (_config.Settings.ApplyToAllDevices)
            {
                _engine.EnforceVolumeCeiling();
                ShowNotification("All-Device Guard Active", "Safe ceiling enforced across all connected speakers, DJ interfaces, and headphones!");
            }
            else
            {
                ShowNotification("Active Device Only", "Ceiling enforced on active default audio device only.");
            }
            UpdateTrayAppearance();
        });
        _contextMenu.Items.Add(_allDevicesItem);


        // Start with Windows
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (s, e) =>
        {
            var currentlyEnabled = StartupManager.IsStartupEnabled();
            var success = StartupManager.SetStartup(!currentlyEnabled);
            if (success)
            {
                _config.Settings.StartWithWindows = !currentlyEnabled;
                _config.Save();
            }
            UpdateTrayAppearance();
        });
        _contextMenu.Items.Add(_startupItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        // Exit
        var exitItem = new ToolStripMenuItem("❌ Exit PermadB", null, (s, e) =>
        {
            OnExitRequested?.Invoke();
        });
        _contextMenu.Items.Add(exitItem);
    }

    private void AddCustomLevelSubmenu(ToolStripMenuItem parent)
    {
        int[] steps = { 30, 40, 50, 60, 70, 75, 80, 85, 90 };
        foreach (var percent in steps)
        {
            var subItem = new ToolStripMenuItem($"{percent}% Limiter Ceiling", null, (s, e) =>
            {
                _config.Settings.ActivePreset = "custom";
                _engine.SetSafeCeiling(percent);
                UpdateTrayAppearance();
                ShowNotification("Custom Ceiling Set", $"Volume ceiling set to {percent}%.");
            });
            parent.DropDownItems.Add(subItem);
        }
    }

    public void UpdateTrayAppearance()
    {
        if (_notifyIcon == null) return;

        try
        {
            var profile = _engine.GetActiveProfile();
            var isEnabled = _config.Settings.Enabled;
            var preset = _config.Settings.ActivePreset;

            // Update Icon
            var iconState = !isEnabled ? "disabled" : (preset == "custom" ? "custom" : "active");
            var oldIcon = _notifyIcon.Icon;
            _notifyIcon.Icon = IconGenerator.CreateShieldIcon(iconState);
            oldIcon?.Dispose();

            // Update Menu Checks
            _enabledItem.Checked = isEnabled;
            _enabledItem.Text = isEnabled ? "Protection: ACTIVE (Shield On)" : "Protection: DISABLED (Bypassed)";

            var devIcon = profile.DeviceType == "headphones" ? "🎧" : "🔊";
            var shortName = profile.DeviceName.Length > 28 ? profile.DeviceName[..25] + "..." : profile.DeviceName;
            _deviceInfoItem.Text = $"{devIcon} {shortName} [Ceiling: {profile.SafeCeilingPercent:F0}%]";

            _safePresetItem.Checked = isEnabled && preset == "safe";
            _nightPresetItem.Checked = isEnabled && preset == "night";
            _studioPresetItem.Checked = isEnabled && preset == "studio";
            _custom1PresetItem.Checked = isEnabled && (preset == "custom1" || preset == "custom");
            _custom2PresetItem.Checked = isEnabled && preset == "custom2";

            var c1Val = _config.Settings.Custom1PresetPercent;
            var c1Spl = 50.0f + (c1Val / 100.0f) * 38.0f;
            _custom1PresetItem.Text = $"Custom 1 - Lowest ({c1Val:F0}% / {c1Spl:F0} dBA)";

            var c2Val = _config.Settings.Custom2PresetPercent;
            var c2Spl = 50.0f + (c2Val / 100.0f) * 38.0f;
            _custom2PresetItem.Text = $"Custom 2 - Highest ({c2Val:F0}% / {c2Spl:F0} dBA)";

            _saveCustom1Item.Text = $"💾 Save Current ({profile.SafeCeilingPercent:F0}%) as Custom 1 (Lowest)";
            _saveCustom2Item.Text = $"💾 Save Current ({profile.SafeCeilingPercent:F0}%) as Custom 2 (Highest)";

            _strictLockItem.Checked = _config.Settings.StrictVolumeLock;
            _allDevicesItem.Checked = _config.Settings.ApplyToAllDevices;
            _startupItem.Checked = StartupManager.IsStartupEnabled();

            // Tooltip text (max 63 chars for Windows NotifyIcon compatibility)
            var statusStr = isEnabled ? $"Active ({profile.SafeCeilingPercent:F0}% Ceiling)" : "Disabled";
            var tooltip = $"PermadB: {statusStr}\n{shortName}";
            if (tooltip.Length > 63) tooltip = tooltip[..60] + "...";
            _notifyIcon.Text = tooltip;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Tray] Error updating appearance: {ex.Message}");
        }
    }



    public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (_config.Settings.NotificationsEnabled)
        {
            _notifyIcon.ShowBalloonTip(3000, title, message, icon);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
    }
}
