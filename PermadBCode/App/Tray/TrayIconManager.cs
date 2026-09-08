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
    private ToolStripMenuItem _customPresetItem = null!;
    private ToolStripMenuItem _strictLockItem = null!;
    private ToolStripMenuItem _allDevicesItem = null!;
    private ToolStripMenuItem _appMixerGuardItem = null!;
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
        _safePresetItem = new ToolStripMenuItem("Safe Ears (65% Limiter / 75 dBA)", null, (s, e) =>
        {
            _engine.ApplyPreset("safe");
            UpdateTrayAppearance();
            ShowNotification("Safe Ears Activated", "Limiter ceiling set to safe level (65% / 75 dBA). Windows volume: 100%.");
        });
        _contextMenu.Items.Add(_safePresetItem);

        _nightPresetItem = new ToolStripMenuItem("Night Mode (50% Limiter / 68 dBA)", null, (s, e) =>
        {
            _engine.ApplyPreset("night");
            UpdateTrayAppearance();
            ShowNotification("Night Mode Activated", "Limiter ceiling set to fatigue-free level (50% / 68 dBA). Windows volume: 100%.");
        });
        _contextMenu.Items.Add(_nightPresetItem);

        _studioPresetItem = new ToolStripMenuItem("Studio Mode (85% Limiter / 82 dBA)", null, (s, e) =>
        {
            _engine.ApplyPreset("studio");
            UpdateTrayAppearance();
            ShowNotification("Studio Mode Activated", "Limiter ceiling set to high dynamic range (85% / 82 dBA). Windows volume: 100%.");
        });
        _contextMenu.Items.Add(_studioPresetItem);

        // Custom Quick Level
        _customPresetItem = new ToolStripMenuItem("Set Custom Limiter Ceiling...");
        AddCustomLevelSubmenu(_customPresetItem);
        _contextMenu.Items.Add(_customPresetItem);

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

        // App Sound Mixer Guard (Level Games & Apps)
        _appMixerGuardItem = new ToolStripMenuItem("🎮 App Sound Mixer Guard (Level Games/Apps)", null, (s, e) =>
        {
            _config.Settings.AppMixerGuardEnabled = !_config.Settings.AppMixerGuardEnabled;
            _config.Save();
            if (_config.Settings.AppMixerGuardEnabled)
            {
                ShowNotification("App Mixer Guard Active", "Games and apps in Windows Sound Mixer are actively leveled under safe dB!");
            }
            else
            {
                ShowNotification("App Mixer Guard Paused", "Individual app volume leveling is paused.");
            }
            UpdateTrayAppearance();
        });
        _contextMenu.Items.Add(_appMixerGuardItem);

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
                ShowNotification("Custom Limiter Set", $"Limiter ceiling set to {percent}%. Windows volume: 100%.");
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
            _deviceInfoItem.Text = $"{devIcon} {shortName} [Limiter: {profile.SafeCeilingPercent:F0}%]";

            _safePresetItem.Checked = isEnabled && preset == "safe";
            _nightPresetItem.Checked = isEnabled && preset == "night";
            _studioPresetItem.Checked = isEnabled && preset == "studio";
            _customPresetItem.Checked = isEnabled && preset == "custom";

            _strictLockItem.Checked = _config.Settings.StrictVolumeLock;
            _allDevicesItem.Checked = _config.Settings.ApplyToAllDevices;
            _appMixerGuardItem.Checked = _config.Settings.AppMixerGuardEnabled;
            _startupItem.Checked = StartupManager.IsStartupEnabled();

            // Tooltip text (max 63 chars for Windows NotifyIcon compatibility)
            var statusStr = isEnabled ? $"Active ({profile.SafeCeilingPercent:F0}% Limiter)" : "Disabled";
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
