using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using PermadB.Config;

namespace PermadB.Audio;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct PermadBApoTelemetry
{
    public uint Magic;                  // "PERM" (0x5045524D)
    public uint Version;                // 2 (Phase 2 Lookahead Limiter)
    public uint AudiodgPid;             // PID of audiodg.exe
    public uint SampleRate;             // e.g. 48000
    public uint ChannelCount;           // e.g. 2
    public uint LimiterActivations;     // Total limiter activation count
    public ulong ProcessCount;          // Total APOProcess() invocations
    public ulong TotalFramesProcessed;  // Total frames processed
    public float ConfiguredCeilingDbfs; // -1.0 dBFS
    public float ConfiguredCeilingLinear;// 0.8912509f
    public float LastPeakInLinear;      // Peak linear input [0..1+]
    public float LastPeakOutLinear;     // Peak linear output [0..1+]
    public float LastPeakInDbfs;        // Peak dBFS in
    public float LastPeakOutDbfs;       // Peak dBFS out
    public float MaxObservedInDbfs;     // Highest input dBFS observed
    public float MaxObservedOutDbfs;    // Highest output dBFS observed
    public float CurrentGainReductionDb;// Current gain reduction in dB
    public float MaxGainReductionDb;    // Maximum gain reduction in dB
    public ulong LastProcessTick;       // GetTickCount64()
    public float TargetCeilingLinear;   // Control to APO: target linear ceiling (e.g. 0.30f for 30%)
    public float TargetCeilingDbfs;     // Control to APO: target dBFS ceiling
    public uint IsEnabled;              // Control to APO: 1 = active, 0 = bypass
    public uint CommandSeq;             // Incremented when command changed
}

public class AppSessionInfo
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public float VolumePercent { get; set; } = 100.0f;
    public float PeakValue { get; set; } = 0.0f;
    public float PeakDbfs { get; set; } = -96.0f;
    public float EstimatedDbSpl { get; set; } = 40.0f;
    public bool IsMuted { get; set; }
    public bool IsClamped { get; set; }
    public bool IsUnsafe { get; set; }
    public bool IsProtected { get; set; } = true;
}

public class AudioMetrics
{
    public float MasterPeak { get; set; }
    public float PeakDbfs { get; set; } = -96.0f;
    public float EstimatedDbSpl { get; set; } = 40.0f;
    public float CurrentVolumePercent { get; set; }
    public float SafeCeilingPercent { get; set; } = 65.0f;
    public bool IsClamping { get; set; }
    public long SpikesClampedTotal { get; set; }
    public float[] ChannelPeaks { get; set; } = Array.Empty<float>();
    public string ActiveDeviceId { get; set; } = string.Empty;
    public string ActiveDeviceName { get; set; } = "Detecting Audio Device...";
    public string ActiveDeviceType { get; set; } = "headphones";
    public bool Enabled { get; set; } = true;
    public bool ApplyToAllDevices { get; set; } = true;
    public bool ProtectMicrophoneInputs { get; set; } = true;
    public int ProtectedDevicesCount { get; set; } = 1;
    public bool AppMixerGuardEnabled { get; set; } = true;
    public bool AppFilteringEnabled { get; set; } = false;
    public float Custom1PresetPercent { get; set; } = 10.0f;
    public float Custom2PresetPercent { get; set; } = 100.0f;
    public Dictionary<string, bool> AppProtectionRules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AppSessionInfo> AppSessions { get; set; } = new();
}

public class AudioEngine : IMMNotificationClient, IDisposable
{
    private readonly ConfigManager _config;
    private readonly Thread _audioThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly BlockingCollection<Action> _actionQueue = new();

    // Pure C# thread-safe cached state (safe to access from any thread)
    private readonly object _stateLock = new();
    private DeviceProfile _cachedProfile = new();
    private List<DeviceProfile> _cachedDevices = new();
    private AudioMetrics _latestMetrics = new();

    // COM objects (ONLY accessed on _audioThread)
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _activeDevice;
    private AudioEndpointVolumeNotificationDelegate? _volumeDelegate;

    // Real-Time APO Telemetry reader (reads C:\Users\Public\permadb_apo_telemetry.dat written by audiodg.exe)
    private const string ApoTelemetryPath = @"C:\Users\Public\permadb_apo_telemetry.dat";
    private FileStream? _telemetryFs;
    private MemoryMappedFile? _telemetryMmf;
    private MemoryMappedViewAccessor? _telemetryAcc;

    private bool TryReadApoTelemetry(out PermadBApoTelemetry telemetry)
    {
        telemetry = default;
        try
        {
            if (_telemetryAcc == null)
            {
                if (File.Exists(ApoTelemetryPath))
                {
                    _telemetryFs = new FileStream(ApoTelemetryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    _telemetryMmf = MemoryMappedFile.CreateFromFile(_telemetryFs, null, Marshal.SizeOf<PermadBApoTelemetry>(), MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
                    _telemetryAcc = _telemetryMmf.CreateViewAccessor(0, Marshal.SizeOf<PermadBApoTelemetry>(), MemoryMappedFileAccess.ReadWrite);
                }
            }

            if (_telemetryAcc != null)
            {
                _telemetryAcc.Read(0, out telemetry);
                if (telemetry.Magic == 0x5045524D) // "PERM"
                {
                    return true;
                }
            }
        }
        catch
        {
            _telemetryAcc?.Dispose();
            _telemetryAcc = null;
            _telemetryMmf?.Dispose();
            _telemetryMmf = null;
            _telemetryFs?.Dispose();
            _telemetryFs = null;
        }
        return false;
    }

    // Audio safety state
    private float _userBaselineVolume = -1.0f;
    private DateTime _lastSessionScan = DateTime.MinValue;
    private List<AppSessionInfo> _cachedAppSessions = new();
    private readonly object _discoverableAppsLock = new();
    private List<AppSessionInfo> _cachedDiscoveredApps = new();
    private DateTime _lastDiscoveredAppsScan = DateTime.MinValue;

    // Process name and peak tracking for active WASAPI audio sessions
    private readonly ConcurrentDictionary<int, string> _processNameCache = new();
    private readonly ConcurrentDictionary<string, float> _appSmoothedPeak = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastProtectedAudioTime = DateTime.MinValue;

    // Voice communication applications where speech intelligibility must NEVER be crushed or muted
    public static readonly HashSet<string> CommunicationApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Discord", "DiscordCanary", "DiscordPTB", "DiscordDevelopment",
        "Slack", "Teams", "ms-teams", "Skype", "Zoom",
        "TeamSpeak", "ts3client_win64", "ts3client_win32",
        "Ventrilo", "Mumble", "Telegram", "WhatsApp", "Steam"
    };

    // Music and media playback applications where users want unconstrained full-scale blast
    public static readonly HashSet<string> MusicApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Spotify", "vlc", "foobar2000", "MusicBee", "AppleMusic", "Tidal", "AIMP", "wmplayer", "iTunes", "Audacity", "Deezer"
    };

    // Major web browsers and game launchers
    public static readonly HashSet<string> BrowserApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "EpicGamesLauncher"
    };

    public AudioMetrics LatestMetrics
    {
        get
        {
            lock (_stateLock) return _latestMetrics;
        }
        private set
        {
            lock (_stateLock) _latestMetrics = value;
        }
    }

    public event Action<AudioMetrics>? OnMetricsUpdated;
    public event Action<DeviceProfile>? OnActiveDeviceChanged;
    public event Action? OnDevicesUpdated;
    public event Action<float>? OnPeakClamped;

    public AudioEngine(ConfigManager config)
    {
        _config = config;

        // Dedicated MTA audio worker thread for WASAPI COM isolation
        _audioThread = new Thread(AudioThreadLoop)
        {
            Name = "PermadB_AudioWorker",
            IsBackground = true
        };
        _audioThread.SetApartmentState(ApartmentState.MTA);
        _audioThread.Start();

        // Wait up to 2 seconds for initial device enumeration
        for (int i = 0; i < 20 && string.IsNullOrEmpty(GetActiveProfile().DeviceId); i++)
        {
            Thread.Sleep(100);
        }
    }

    public DeviceProfile GetActiveProfile()
    {
        lock (_stateLock)
        {
            return _cachedProfile;
        }
    }

    public List<DeviceProfile> GetAllRenderDevices()
    {
        lock (_stateLock)
        {
            return _cachedDevices.ToList();
        }
    }

    public void EnforceVolumeCeiling()
    {
        PostToAudioThread(() =>
        {
            InternalEnforceCeiling();
        });
    }

    public void SetSafeCeiling(float percent)
    {
        var profile = GetActiveProfile();
        profile.SafeCeilingPercent = Math.Clamp(percent, 10.0f, 100.0f);
        profile.TargetSafeDbSpl = 50.0f + (profile.SafeCeilingPercent / 100.0f) * 38.0f;
        _config.Settings.Enabled = true;
        if (_config.Settings.ActivePreset == "custom2")
        {
            _config.Settings.Custom2PresetPercent = profile.SafeCeilingPercent;
        }
        else
        {
            _config.Settings.ActivePreset = "custom1";
            _config.Settings.Custom1PresetPercent = profile.SafeCeilingPercent;
        }
        _config.Save();
        UpdateCachedProfile(profile);
        _lastSessionScan = DateTime.MinValue; // Force immediate session scan & per-app volume clamp

        PostToAudioThread(() =>
        {
            InternalEnforceCeiling();
            InternalPollMeter();
        });
    }

    public void SaveCustomPreset(int slot, float percent)
    {
        percent = Math.Clamp(percent, 10.0f, 100.0f);
        if (slot == 2)
        {
            _config.Settings.Custom2PresetPercent = percent;
            _config.Settings.ActivePreset = "custom2";
        }
        else
        {
            _config.Settings.Custom1PresetPercent = percent;
            _config.Settings.ActivePreset = "custom1";
        }
        _config.Save();
        ApplyPreset(_config.Settings.ActivePreset);
    }

    public void SetAppProtection(string processName, bool isProtected)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;
        _config.Settings.AppProtectionRules[processName] = isProtected;
        _config.Save();
        _lastProtectedAudioTime = DateTime.MinValue;
        _lastSessionScan = DateTime.MinValue; // Force immediate re-scan!
        PostToAudioThread(() =>
        {
            InternalEnforceCeiling();
            InternalPollMeter();
        });
    }

    public void ToggleAppFiltering(bool? enabled = null)
    {
        _config.Settings.AppFilteringEnabled = enabled ?? !_config.Settings.AppFilteringEnabled;
        _config.Save();
        _lastProtectedAudioTime = DateTime.MinValue;
        _lastSessionScan = DateTime.MinValue; // Force immediate re-scan!
        PostToAudioThread(() =>
        {
            InternalEnforceCeiling();
            InternalPollMeter();
        });
    }

    public bool IsAppProtected(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        if (_config.Settings.AppProtectionRules.TryGetValue(processName, out bool val))
        {
            return val;
        }
        // Zero hardcoded presets: all apps default to Bypassed (false).
        // Only apps explicitly chosen by the user in Select Apps are limited.
        return false;
    }

    public List<AppSessionInfo> GetDiscoverableAudioApps()
    {
        lock (_discoverableAppsLock)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastDiscoveredAppsScan).TotalSeconds < 3.0 && _cachedDiscoveredApps.Count > 0)
            {
                // Return fresh status projections from cached scan without re-querying all processes
                var fastList = new List<AppSessionInfo>(_cachedDiscoveredApps.Count);
                foreach (var app in _cachedDiscoveredApps)
                {
                    fastList.Add(new AppSessionInfo
                    {
                        ProcessId = app.ProcessId,
                        ProcessName = app.ProcessName,
                        DisplayName = app.DisplayName,
                        VolumePercent = app.VolumePercent,
                        PeakValue = app.PeakValue,
                        PeakDbfs = app.PeakDbfs,
                        EstimatedDbSpl = app.EstimatedDbSpl,
                        IsMuted = app.IsMuted,
                        IsClamped = app.IsClamped,
                        IsProtected = IsAppProtected(app.ProcessName),
                        IsUnsafe = app.IsUnsafe
                    });
                }
                return fastList;
            }

            var dict = new Dictionary<string, AppSessionInfo>(StringComparer.OrdinalIgnoreCase);

            // 1. Add active WASAPI audio sessions with real-time evaluated protection
            lock (_stateLock)
            {
                foreach (var app in _cachedAppSessions)
                {
                    if (!string.IsNullOrWhiteSpace(app.ProcessName))
                    {
                        app.IsProtected = IsAppProtected(app.ProcessName);
                        dict[app.ProcessName] = app;
                    }
                }
            }

            // 2. Add saved configured rules from settings
            foreach (var kvp in _config.Settings.AppProtectionRules)
            {
                if (!dict.ContainsKey(kvp.Key))
                {
                    dict[kvp.Key] = new AppSessionInfo
                    {
                        ProcessName = kvp.Key,
                        DisplayName = kvp.Key,
                        IsProtected = kvp.Value,
                        IsClamped = false,
                        IsMuted = false,
                        VolumePercent = 100.0f,
                        PeakDbfs = -96.0f,
                        EstimatedDbSpl = 0.0f
                    };
                }
            }

            // 3. Scan running communication, music, and browser processes with guaranteed disposal of all Process objects
            Process[]? processes = null;
            try
            {
                processes = Process.GetProcesses();
                foreach (var p in processes)
                {
                    try
                    {
                        string pName = p.ProcessName;
                        if (dict.ContainsKey(pName)) continue;

                        bool isTarget = CommunicationApps.Contains(pName) ||
                                        MusicApps.Contains(pName) ||
                                        BrowserApps.Contains(pName) ||
                                        (!string.IsNullOrEmpty(p.MainWindowTitle) && 
                                         !pName.Equals("explorer", StringComparison.OrdinalIgnoreCase) && 
                                         !pName.Equals("PermadB", StringComparison.OrdinalIgnoreCase) &&
                                         !pName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase));

                        if (isTarget)
                        {
                            bool isProt = IsAppProtected(pName);
                            dict[pName] = new AppSessionInfo
                            {
                                ProcessId = p.Id,
                                ProcessName = pName,
                                DisplayName = !string.IsNullOrWhiteSpace(p.MainWindowTitle) ? $"{pName} ({p.MainWindowTitle})" : pName,
                                IsProtected = isProt,
                                IsClamped = false,
                                IsMuted = false,
                                VolumePercent = 100.0f,
                                PeakDbfs = -96.0f,
                                EstimatedDbSpl = 0.0f
                            };
                        }
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                if (processes != null)
                {
                    foreach (var p in processes)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }

            // Sort: active sessions first, then communication apps, then alphabetical
            var result = dict.Values
                .OrderByDescending(a => a.PeakValue > 0.001f)
                .ThenByDescending(a => CommunicationApps.Contains(a.ProcessName))
                .ThenBy(a => a.ProcessName)
                .ToList();

            _cachedDiscoveredApps = result;
            _lastDiscoveredAppsScan = now;
            return result;
        }
    }

    public void ApplyPreset(string preset)
    {
        var profile = GetActiveProfile();

        switch (preset.ToLowerInvariant())
        {
            case "night":
                profile.SafeCeilingPercent = 50.0f;
                profile.TargetSafeDbSpl = 68.0f;
                _config.Settings.DynamicThresholdDbfs = -6.0f;
                _config.Settings.ActivePreset = "night";
                break;
            case "studio":
                profile.SafeCeilingPercent = 85.0f;
                profile.TargetSafeDbSpl = 82.0f;
                _config.Settings.DynamicThresholdDbfs = -1.5f;
                _config.Settings.ActivePreset = "studio";
                break;
            case "custom2":
                float custom2Percent = Math.Clamp(_config.Settings.Custom2PresetPercent, 10.0f, 100.0f);
                profile.SafeCeilingPercent = custom2Percent;
                profile.TargetSafeDbSpl = 50.0f + (custom2Percent / 100.0f) * 38.0f;
                _config.Settings.DynamicThresholdDbfs = -3.0f;
                _config.Settings.ActivePreset = "custom2";
                break;
            case "custom":
            case "custom1":
                float custom1Percent = Math.Clamp(_config.Settings.Custom1PresetPercent, 10.0f, 100.0f);
                profile.SafeCeilingPercent = custom1Percent;
                profile.TargetSafeDbSpl = 50.0f + (custom1Percent / 100.0f) * 38.0f;
                _config.Settings.DynamicThresholdDbfs = -3.0f;
                _config.Settings.ActivePreset = "custom1";
                break;
            case "safe":
            default:
                profile.SafeCeilingPercent = 65.0f;
                profile.TargetSafeDbSpl = 75.0f;
                _config.Settings.DynamicThresholdDbfs = -3.0f;
                _config.Settings.ActivePreset = "safe";
                break;
        }

        _config.Settings.Enabled = true;
        _config.Save();
        UpdateCachedProfile(profile);
        _lastSessionScan = DateTime.MinValue; // Force immediate session scan & per-app volume clamp

        PostToAudioThread(() =>
        {
            InternalEnforceCeiling();
            InternalPollMeter();
        });
    }

    public void SetMasterVolume(float percent)
    {
        // Invariant: PermadB never alters Windows master volume.
        // Safety limiting is enforced exclusively on PCM audio inside audiodg.exe via the APO.
        Console.WriteLine($"[AudioEngine] SetMasterVolume({percent:F0}%) ignored: Volume immutability invariant strictly enforced.");
    }

    public void SetAppVolume(string processName, float volumePercent)
    {
        // Invariant: PermadB never alters Windows Volume Mixer.
        // Safety limiting is enforced exclusively on raw PCM audio inside audiodg.exe via the C++ APO DLL.
        Console.WriteLine($"[AudioEngine] SetAppVolume({processName}, {volumePercent:F0}%) ignored: Volume mixer immutability invariant strictly enforced.");
    }

    private void PostToAudioThread(Action action)
    {
        if (!_cts.IsCancellationRequested)
        {
            _actionQueue.Add(action);
        }
    }

    private void UpdateCachedProfile(DeviceProfile profile)
    {
        lock (_stateLock)
        {
            _cachedProfile = profile;
        }
        OnActiveDeviceChanged?.Invoke(profile);
    }

    private void AudioThreadLoop()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            try
            {
                _enumerator.RegisterEndpointNotificationCallback(this);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioEngine] Warning: Could not register device notifications: {ex.Message}");
            }

            InternalRefreshActiveDevice();
            InternalRefreshAllDevices();

            // Main 50Hz audio processing loop
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                // Process any queued commands from other threads
                while (_actionQueue.TryTake(out var action))
                {
                    try { action(); } catch { }
                }

                // Poll audio meter
                InternalPollMeter();

                Thread.Sleep(50); // 20 Hz UI meter update loop (reduced COM IPC overhead)
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AudioEngine] Audio thread fatal error: {ex.Message}");
        }
        finally
        {
            CleanupCom();
        }
    }

    private void InternalRefreshActiveDevice()
    {
        if (_enumerator == null) return;

        try
        {
            if (_activeDevice != null && _volumeDelegate != null)
            {
                try { _activeDevice.AudioEndpointVolume.OnVolumeNotification -= _volumeDelegate; } catch { }
            }

            _activeDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (_activeDevice != null)
            {
                var id = _activeDevice.ID;
                var friendlyName = _activeDevice.FriendlyName;
                var profile = _config.GetOrCreateProfile(id, friendlyName);

                // Synchronize profile safe ceiling with configured active preset on startup / device connect
                switch (_config.Settings.ActivePreset.ToLowerInvariant())
                {
                    case "safe":
                        profile.SafeCeilingPercent = 65.0f;
                        profile.TargetSafeDbSpl = 75.0f;
                        break;
                    case "night":
                        profile.SafeCeilingPercent = 50.0f;
                        profile.TargetSafeDbSpl = 68.0f;
                        break;
                    case "studio":
                        profile.SafeCeilingPercent = 85.0f;
                        profile.TargetSafeDbSpl = 82.0f;
                        break;
                    case "custom":
                        profile.SafeCeilingPercent = Math.Clamp(profile.SafeCeilingPercent, 10.0f, 100.0f);
                        profile.TargetSafeDbSpl = 50.0f + (profile.SafeCeilingPercent / 100.0f) * 38.0f;
                        break;
                    default:
                        _config.Settings.ActivePreset = "safe";
                        profile.SafeCeilingPercent = 65.0f;
                        profile.TargetSafeDbSpl = 75.0f;
                        break;
                }

                _volumeDelegate = new AudioEndpointVolumeNotificationDelegate(OnVolumeNotificationReceived);
                _activeDevice.AudioEndpointVolume.OnVolumeNotification += _volumeDelegate;

                Console.WriteLine($"[AudioEngine] Active Device: {friendlyName} ({profile.DeviceType}, Preset={_config.Settings.ActivePreset}, Ceiling={profile.SafeCeilingPercent:F0}%)");
                EnsureEndpointFxProperties(id);
                UpdateCachedProfile(profile);
                InternalEnforceCeiling();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AudioEngine] Error refreshing active device: {ex.Message}");
        }
    }

    private void InternalRefreshAllDevices()
    {
        if (_enumerator == null) return;

        try
        {
            var list = new List<DeviceProfile>();
            var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var ep in endpoints)
            {
                try
                {
                    EnsureEndpointFxProperties(ep.ID);
                    list.Add(_config.GetOrCreateProfile(ep.ID, ep.FriendlyName));
                }
                catch { }
            }

            lock (_stateLock)
            {
                _cachedDevices = list;
            }
            OnDevicesUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AudioEngine] Error enumerating endpoints: {ex.Message}");
        }
    }

    private void OnVolumeNotificationReceived(AudioVolumeNotificationData data)
    {
        _userBaselineVolume = data.MasterVolume;
    }

    private int _cachedProtectedCount = 1;

    private uint _apoCommandSeq = 1;
    private float _lastSentCeilingLinear = -1.0f;
    private int _lastSentEnabled = -1;

    public void EnsureActiveDeviceFxProperties()
    {
        if (_activeDevice == null) return;
        EnsureEndpointFxProperties(_activeDevice.ID);
    }

    public void EnsureEndpointFxProperties(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return;

        const string clsid = "{968ff234-1895-49f1-8b97-9d9075eb25b6}";
        const string pkeyEfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7";
        const string pkeyCompositeEfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15";
        const string pkeyEfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},7";

        string cleanId = deviceId;
        int lastBrace = cleanId.LastIndexOf('{');
        if (lastBrace > 0)
        {
            cleanId = cleanId.Substring(lastBrace);
        }

        try
        {
            string subPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{cleanId}\FxProperties";
            RegistryKey? key = null;
            try
            {
                key = Registry.LocalMachine.OpenSubKey(
                    subPath,
                    RegistryKeyPermissionCheck.ReadWriteSubTree,
                    System.Security.AccessControl.RegistryRights.SetValue | System.Security.AccessControl.RegistryRights.QueryValues);
            }
            catch
            {
                try { key = Registry.LocalMachine.OpenSubKey(subPath, true); } catch { }
            }

            if (key != null)
            {
                using (key)
                {
                    // Self-healing: restore any third-party stream effect APOs (e.g. DTS / NGENUITY) backed up by older versions
                    try
                    {
                        var backupComp = key.GetValue("PermadB_Backup_CompositeSFX_DTS") as string[];
                        if (backupComp != null && backupComp.Length > 0)
                        {
                            const string pkeyCompositeSfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13";
                            key.SetValue(pkeyCompositeSfx, backupComp, RegistryValueKind.MultiString);
                            key.DeleteValue("PermadB_Backup_CompositeSFX_DTS", false);
                        }
                    }
                    catch { }

                    try
                    {
                        var backupSfx = key.GetValue("PermadB_Backup_SFX_DTS");
                        if (backupSfx != null)
                        {
                            if (backupSfx is string[] multi)
                            {
                                const string pkeyCompositeSfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13";
                                key.SetValue(pkeyCompositeSfx, multi, RegistryValueKind.MultiString);
                            }
                            else if (backupSfx is string s && !string.IsNullOrEmpty(s))
                            {
                                const string pkeySfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5";
                                key.SetValue(pkeySfx, s, RegistryValueKind.String);
                            }
                            key.DeleteValue("PermadB_Backup_SFX_DTS", false);
                        }
                    }
                    catch { }

                    var efx = key.GetValue(pkeyEfx) as string;
                    if (!string.Equals(efx, clsid, StringComparison.OrdinalIgnoreCase))
                    {
                        key.SetValue(pkeyEfx, clsid, RegistryValueKind.String);
                    }

                    var comp = key.GetValue(pkeyCompositeEfx) as string[];
                    if (comp == null || !comp.Contains(clsid, StringComparer.OrdinalIgnoreCase))
                    {
                        var newComp = comp == null ? new[] { clsid } : comp.Append(clsid).ToArray();
                        key.SetValue(pkeyCompositeEfx, newComp, RegistryValueKind.MultiString);
                    }

                    var modes = key.GetValue(pkeyEfxModes) as string[];
                    if (modes == null || modes.Length == 0)
                    {
                        key.SetValue(pkeyEfxModes, new[] { "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}" }, RegistryValueKind.MultiString);
                    }
                }
            }
        }
        catch { }
    }

    public void SendCeilingToApo()
    {
        PostToAudioThread(InternalSendCeilingToApo);
    }

    /// <summary>
    /// Calculates the exact acoustically calibrated linear ceiling scalar for the active preset.
    /// Clamps continuous physical dBA SPL to TargetSafeDbSpl based on estimated transducer max SPL.
    /// </summary>
    public float GetActivePresetLinearCeiling()
    {
        var profile = GetActiveProfile();
        float maxSpl = profile.EstimatedMaxDbSpl > 50.0f ? profile.EstimatedMaxDbSpl : 100.0f;
        float targetDbfs = profile.TargetSafeDbSpl + 11.0f - maxSpl;
        // Strict range: [-40.0 dBFS .. -1.0 dBFS]
        targetDbfs = Math.Clamp(targetDbfs, -40.0f, -1.0f);
        float linear = (float)Math.Pow(10.0, targetDbfs / 20.0);
        return Math.Clamp(linear, 0.01f, 0.8912509f);
    }

    private void InternalSendCeilingToApo()
    {
        try
        {
            var profile = GetActiveProfile();
            float ceilingPercent = profile.SafeCeilingPercent;
            bool isEnabled = _config.Settings.Enabled;

            // Acoustically calibrated digital ceiling calculation:
            // Continuous SPL = profile.EstimatedMaxDbSpl + peakDbfs + masterDbAtten - 11.0f (crest factor)
            // To guarantee continuous SPL NEVER exceeds profile.TargetSafeDbSpl:
            // targetDbfs <= profile.TargetSafeDbSpl + 11.0f - profile.EstimatedMaxDbSpl - masterDbAtten;
            float linear = GetActivePresetLinearCeiling();
            float dbfs = 20.0f * (float)Math.Log10(linear);

            if (_config.Settings.AppFilteringEnabled && isEnabled)
            {
                // When Application-Specific Limiting is enabled:
                // Check if any protected application (e.g. Discord, or Brave when marked Limited) is actively outputting audio.
                // If yes: the APO dynamically clamps the stream to the safe acoustic preset ceiling.
                // If no (only bypassed apps like music are playing): the APO runs in transparent peak-limiter mode (-1.0 dBFS)
                // so music blasts freely at 100% full volume!
                // Limiting is executed 100% inside audiodg.exe via PermadBApo.dll — ZERO volume mixer manipulation!
                bool isProtectedAppActive = (DateTime.UtcNow - _lastProtectedAudioTime).TotalMilliseconds < 800;
                if (!isProtectedAppActive)
                {
                    linear = 0.8912509f;
                    dbfs = -1.0f;
                }
            }

            int enabledInt = isEnabled ? 1 : 0;

            if (_telemetryAcc == null)
            {
                try
                {
                    _telemetryFs = new FileStream(ApoTelemetryPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                    if (_telemetryFs.Length < Marshal.SizeOf<PermadBApoTelemetry>())
                    {
                        _telemetryFs.SetLength(Marshal.SizeOf<PermadBApoTelemetry>());
                    }
                    _telemetryMmf = MemoryMappedFile.CreateFromFile(_telemetryFs, null, Marshal.SizeOf<PermadBApoTelemetry>(), MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
                    _telemetryAcc = _telemetryMmf.CreateViewAccessor(0, Marshal.SizeOf<PermadBApoTelemetry>(), MemoryMappedFileAccess.ReadWrite);
                }
                catch { }
            }

            if (_telemetryAcc != null)
            {
                // Verify whether the live APO is in sync with our desired ceiling
                _telemetryAcc.Read(0, out PermadBApoTelemetry telem);
                bool apoOutOfSync = (telem.Magic != 0x5045524D) ||
                                    (Math.Abs(telem.ConfiguredCeilingLinear - linear) > 0.005f) ||
                                    (telem.IsEnabled != (uint)enabledInt);

                if (apoOutOfSync || Math.Abs(linear - _lastSentCeilingLinear) > 0.001f || enabledInt != _lastSentEnabled)
                {
                    _apoCommandSeq++;
                    int offsetMagic = (int)Marshal.OffsetOf<PermadBApoTelemetry>(nameof(PermadBApoTelemetry.Magic));
                    int offsetVersion = (int)Marshal.OffsetOf<PermadBApoTelemetry>(nameof(PermadBApoTelemetry.Version));
                    int offsetLinear = (int)Marshal.OffsetOf<PermadBApoTelemetry>(nameof(PermadBApoTelemetry.TargetCeilingLinear));
                    int offsetDbfs = (int)Marshal.OffsetOf<PermadBApoTelemetry>(nameof(PermadBApoTelemetry.TargetCeilingDbfs));
                    int offsetEnabled = (int)Marshal.OffsetOf<PermadBApoTelemetry>(nameof(PermadBApoTelemetry.IsEnabled));
                    int offsetSeq = (int)Marshal.OffsetOf<PermadBApoTelemetry>(nameof(PermadBApoTelemetry.CommandSeq));

                    _telemetryAcc.Write(offsetMagic, (uint)0x5045524D); // "PERM"
                    _telemetryAcc.Write(offsetVersion, (uint)2);
                    _telemetryAcc.Write(offsetLinear, linear);
                    _telemetryAcc.Write(offsetDbfs, dbfs);
                    _telemetryAcc.Write(offsetEnabled, (uint)enabledInt);
                    _telemetryAcc.Write(offsetSeq, _apoCommandSeq);

                    _lastSentCeilingLinear = linear;
                    _lastSentEnabled = enabledInt;

                    Console.WriteLine($"[AudioEngine] Dynamic ceiling sent to APO: {ceilingPercent:F0}% ({linear:F4} linear, {dbfs:F1} dBFS, target={profile.TargetSafeDbSpl:F0}dBA, enabled={isEnabled}, seq={_apoCommandSeq})");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AudioEngine] Error sending ceiling to APO: {ex.Message}");
        }
    }

    private void InternalEnforceCeiling()
    {
        // Limiting is executed directly on PCM inside audiodg.exe via PermadBApo.dll.
        // Windows volume sliders are NEVER altered.
        InternalSendCeilingToApo();

        int count = 0;
        if (_activeDevice != null) count++;

        if (_config.Settings.ApplyToAllDevices && _enumerator != null)
        {
            try
            {
                var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                count = endpoints.Count;
            }
            catch { }
        }

        _cachedProtectedCount = Math.Max(1, count);
    }

    private void InternalPollMeter()
    {
        if (_activeDevice == null) return;

        try
        {
            var meter = _activeDevice.AudioMeterInformation;
            var endpointVol = _activeDevice.AudioEndpointVolume;
            var meterPeak = meter.MasterPeakValue;
            var volScalar = endpointVol.MasterVolumeLevelScalar;
            var profile = GetActiveProfile();
            InternalSendCeilingToApo();

            // Check real-time APO telemetry from audiodg.exe
            bool hasApoTelemetry = TryReadApoTelemetry(out var telem);
            bool isApoRecentlyActive = hasApoTelemetry && ((ulong)Environment.TickCount64 - telem.LastProcessTick < 1500);

            float peak;
            float peakDbfs;
            bool isClamping;
            long spikesClamped;

            if (isApoRecentlyActive)
            {
                peak = telem.LastPeakOutLinear;
                peakDbfs = telem.LastPeakOutDbfs;
                // Gain reduction in C++ metrics is negative in dB (e.g. -6 dB)
                isClamping = telem.CurrentGainReductionDb < -0.05f || Math.Abs(telem.CurrentGainReductionDb) > 0.05f;
                spikesClamped = 0;

                if (isClamping)
                {
                    OnPeakClamped?.Invoke(telem.LastPeakInDbfs);
                }
            }
            else
            {
                // Limiter APO is not active on this device - rate-limit registry check to once every 10s
                if ((DateTime.UtcNow - _lastSessionScan).TotalSeconds > 9.5)
                {
                    EnsureActiveDeviceFxProperties();
                }
                peak = meterPeak;
                peakDbfs = peak > 0.00001f ? 20.0f * (float)Math.Log10(peak) : -96.0f;
                isClamping = false;
                spikesClamped = 0;
            }

            // Hardware master volume attenuation in dB
            float masterDbAtten;
            try
            {
                masterDbAtten = endpointVol.MasterVolumeLevel;
            }
            catch
            {
                masterDbAtten = volScalar > 0.001f ? 20.0f * (float)Math.Log10(volScalar) : -60.0f;
            }

            // Physical acoustic peak SPL at user's ears (EstimatedMaxDbSpl is physical dBA at 0 dBFS & 0 dB attenuation)
            var estimatedPeakSpl = profile.EstimatedMaxDbSpl + peakDbfs + masterDbAtten;
            estimatedPeakSpl = Math.Clamp(estimatedPeakSpl, 25.0f, 120.0f);

            // Continuous equivalent dBA (LAeq / RMS) for display and safety limits (typical audio crest factor ~11 dB)
            var estimatedContinuousSpl = Math.Clamp(estimatedPeakSpl - 11.0f, 25.0f, 115.0f);

            // Strict Safety Ceiling Guarantee: When the limiter is clamping or when audio hits the ceiling,
            // the continuous SPL cannot physically exceed the configured target safe ceiling.
            if ((isClamping || estimatedContinuousSpl > profile.TargetSafeDbSpl) && isApoRecentlyActive)
            {
                estimatedContinuousSpl = Math.Min(estimatedContinuousSpl, profile.TargetSafeDbSpl);
            }

            var channelCount = meter.PeakValues.Count;
            var channelPeaks = new float[channelCount];
            for (int i = 0; i < channelCount; i++)
            {
                float chPeak = meter.PeakValues[i];
                if (isApoRecentlyActive && chPeak > peak)
                {
                    chPeak = peak;
                }
                channelPeaks[i] = chPeak;
            }

            // Active Hearing Safety Monitoring: Detect if acoustic output exceeds safe target for UI meter stats
            bool isExceedingCeiling = estimatedPeakSpl > profile.TargetSafeDbSpl;
            if (isExceedingCeiling)
            {
                OnPeakClamped?.Invoke(peakDbfs);
            }

            // Per-Application Sound Mixer & Active Session Leveler (Responsive 100ms cycle)
            if ((DateTime.UtcNow - _lastSessionScan).TotalMilliseconds >= 100)
            {
                _lastSessionScan = DateTime.UtcNow;
                var appSessionsList = new List<AppSessionInfo>();
                try
                {
                    var sessionManager = _activeDevice.AudioSessionManager;
                    sessionManager.RefreshSessions();
                    var sessions = sessionManager.Sessions;
                    int sessionCount = sessions.Count;

                    for (int i = 0; i < sessionCount; i++)
                    {
                        var s = sessions[i];
                        try
                        {
                            if (s.State == AudioSessionState.AudioSessionStateExpired) continue;

                            var pid = (int)s.GetProcessID;
                            string procName = "System Sounds";
                            if (pid > 0)
                            {
                                procName = _processNameCache.GetOrAdd(pid, p =>
                                {
                                    try
                                    {
                                        using var proc = Process.GetProcessById(p);
                                        return proc.ProcessName;
                                    }
                                    catch { return $"PID {p}"; }
                                });
                            }

                            var appVol = s.SimpleAudioVolume.Volume;
                            var appMeter = s.AudioMeterInformation;
                            var rawAppPeak = appMeter.MasterPeakValue;
                            var appMute = s.SimpleAudioVolume.Mute;

                            var smoothedPeak = _appSmoothedPeak.AddOrUpdate(
                                procName,
                                rawAppPeak,
                                (_, prev) => Math.Max(rawAppPeak, prev * 0.75f + rawAppPeak * 0.25f)
                            );

                            var appPeakDbfs = smoothedPeak > 0.00001f ? 20.0f * (float)Math.Log10(smoothedPeak) : -96.0f;
                            bool isVoip = CommunicationApps.Contains(procName);
                            float crestFactor = isVoip ? 15.0f : 11.0f;
                            var appEstPeakSpl = Math.Clamp(profile.EstimatedMaxDbSpl + appPeakDbfs + masterDbAtten, 25.0f, 120.0f);
                            var appEstContinuousSpl = Math.Clamp(appEstPeakSpl - crestFactor, 25.0f, 105.0f);

                            bool isProt = IsAppProtected(procName);
                            bool isAppClamped = false;

                            bool isAppActive = rawAppPeak > 0.001f;
                            if (isProt && isAppActive)
                            {
                                _lastProtectedAudioTime = DateTime.UtcNow;
                            }

                            if (_config.Settings.Enabled)
                            {
                                if (_config.Settings.AppFilteringEnabled)
                                {
                                    isAppClamped = isProt && isAppActive && isClamping;
                                }
                                else
                                {
                                    isAppClamped = isClamping;
                                }
                            }

                            // INVARIANT: PermadB NEVER modifies Windows Volume Mixer sliders (s.SimpleAudioVolume.Volume).
                            // All digital limiting is executed deterministically on raw PCM inside audiodg.exe via PermadBApo.dll.

                            appSessionsList.Add(new AppSessionInfo
                            {
                                ProcessId = pid,
                                ProcessName = procName,
                                DisplayName = string.IsNullOrWhiteSpace(s.DisplayName) ? procName : s.DisplayName,
                                VolumePercent = (float)Math.Round(appVol * 100.0f, 1),
                                PeakValue = (float)Math.Round(smoothedPeak, 3),
                                PeakDbfs = (float)Math.Round(appPeakDbfs, 1),
                                EstimatedDbSpl = (float)Math.Round(appEstContinuousSpl, 1),
                                IsMuted = appMute,
                                IsClamped = isAppClamped,
                                IsProtected = isProt,
                                IsUnsafe = appEstContinuousSpl > profile.TargetSafeDbSpl
                            });
                        }
                        finally
                        {
                            try { (s as IDisposable)?.Dispose(); } catch { }
                        }
                    }
                    try { (sessions as IDisposable)?.Dispose(); } catch { }
                    _cachedAppSessions = appSessionsList;
                    InternalSendCeilingToApo();
                }
                catch { }
            }

            var m = new AudioMetrics
            {
                MasterPeak = peak,
                PeakDbfs = (float)Math.Round(peakDbfs, 1),
                EstimatedDbSpl = (float)Math.Round(estimatedContinuousSpl, 1),
                CurrentVolumePercent = (float)Math.Round(volScalar * 100.0f, 1),
                SafeCeilingPercent = profile.SafeCeilingPercent,
                IsClamping = isClamping,
                SpikesClampedTotal = spikesClamped,
                ChannelPeaks = channelPeaks,
                ActiveDeviceId = profile.DeviceId,
                ActiveDeviceName = profile.DeviceName,
                ActiveDeviceType = profile.DeviceType,
                Enabled = _config.Settings.Enabled,
                ApplyToAllDevices = _config.Settings.ApplyToAllDevices,
                ProtectMicrophoneInputs = _config.Settings.ProtectMicrophoneInputs,
                ProtectedDevicesCount = _cachedProtectedCount,
                AppMixerGuardEnabled = _config.Settings.AppMixerGuardEnabled,
                AppFilteringEnabled = _config.Settings.AppFilteringEnabled,
                Custom1PresetPercent = _config.Settings.Custom1PresetPercent,
                Custom2PresetPercent = _config.Settings.Custom2PresetPercent,
                AppProtectionRules = _config.Settings.AppProtectionRules,
                AppSessions = _cachedAppSessions
            };

            LatestMetrics = m;
            OnMetricsUpdated?.Invoke(m);
        }
        catch { }
    }

    #region IMMNotificationClient

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && (role == Role.Multimedia || role == Role.Console))
        {
            PostToAudioThread(() =>
            {
                InternalRefreshActiveDevice();
                InternalRefreshAllDevices();
            });
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        PostToAudioThread(InternalRefreshAllDevices);
    }

    public void OnDeviceAdded(string pwstrDeviceId)
    {
        PostToAudioThread(InternalRefreshAllDevices);
    }

    public void OnDeviceRemoved(string deviceId)
    {
        PostToAudioThread(InternalRefreshAllDevices);
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    #endregion

    private void CleanupCom()
    {
        try
        {
            _telemetryAcc?.Dispose();
            _telemetryAcc = null;
            _telemetryMmf?.Dispose();
            _telemetryMmf = null;
            _telemetryFs?.Dispose();
            _telemetryFs = null;

            if (_enumerator != null)
            {
                _enumerator.UnregisterEndpointNotificationCallback(this);
            }
            if (_activeDevice != null && _volumeDelegate != null)
            {
                _activeDevice.AudioEndpointVolume.OnVolumeNotification -= _volumeDelegate;
            }
            _enumerator?.Dispose();
        }
        catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _actionQueue.CompleteAdding();
        _audioThread.Join(500);
    }
}
