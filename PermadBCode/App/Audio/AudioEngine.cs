using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using PermadB.Config;

namespace PermadB.Audio;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct PermadBSfxSlot
{
    public uint Active;               // 0 = free, 1 = claimed by active SFX instance
    public uint InstanceId;           // Monotonic token incremented on each claim
    public uint AudioProcessingMode;  // 0=Default, 1=Communications, 2=Media, 3=Other
    public float InputPeak;           // Max pre-limiter linear peak observed [0..1+]
    public float OutputPeak;          // Max post-limiter linear peak observed [0..1+]
    public float TargetCeilingLinear; // Target linear ceiling (default 0.1585f / -16dBFS)
    public uint Bypass;               // 0 = limit to TargetCeilingLinear, 1 = bypass SFX
    public uint MatchedProcessId;     // ProcessId matched by C# supervisor (0 = unmatched)
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct PermadBApoTelemetry
{
    public uint Magic;                  // "PERM" (0x5045524D)
    public uint Version;                // 3 (Dual-Stage SFX + EFX)
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
    public float MasterSafetyCeilingLinear; // Upper safety cap for EFX brickwall
    public uint SfxActiveCount;         // Number of active SFX instances

    // Diagnostic lifecycle counters
    public uint EfxProcessCount;        // Incremented on every EFX APOProcess
    public uint SfxInitCount;           // Incremented on every SFX Initialize
    public uint SfxLockCount;           // Incremented on every SFX LockForProcess
    public uint SfxProcessCount;        // Incremented on every SFX APOProcess
    public uint LastSfxChannels;        // Channel count of last SFX stream locked
    public uint DiagnosticPadding;      // Alignment padding to 8 bytes

    public const int SfxSlotCount = 16;
    public PermadBSfxSlot Slot0;
    public PermadBSfxSlot Slot1;
    public PermadBSfxSlot Slot2;
    public PermadBSfxSlot Slot3;
    public PermadBSfxSlot Slot4;
    public PermadBSfxSlot Slot5;
    public PermadBSfxSlot Slot6;
    public PermadBSfxSlot Slot7;
    public PermadBSfxSlot Slot8;
    public PermadBSfxSlot Slot9;
    public PermadBSfxSlot Slot10;
    public PermadBSfxSlot Slot11;
    public PermadBSfxSlot Slot12;
    public PermadBSfxSlot Slot13;
    public PermadBSfxSlot Slot14;
    public PermadBSfxSlot Slot15;

    public PermadBSfxSlot GetSlot(int index) => index switch
    {
        0 => Slot0, 1 => Slot1, 2 => Slot2, 3 => Slot3,
        4 => Slot4, 5 => Slot5, 6 => Slot6, 7 => Slot7,
        8 => Slot8, 9 => Slot9, 10 => Slot10, 11 => Slot11,
        12 => Slot12, 13 => Slot13, 14 => Slot14, 15 => Slot15,
        _ => default
    };

    public void SetSlot(int index, PermadBSfxSlot slot)
    {
        switch (index)
        {
            case 0: Slot0 = slot; break;
            case 1: Slot1 = slot; break;
            case 2: Slot2 = slot; break;
            case 3: Slot3 = slot; break;
            case 4: Slot4 = slot; break;
            case 5: Slot5 = slot; break;
            case 6: Slot6 = slot; break;
            case 7: Slot7 = slot; break;
            case 8: Slot8 = slot; break;
            case 9: Slot9 = slot; break;
            case 10: Slot10 = slot; break;
            case 11: Slot11 = slot; break;
            case 12: Slot12 = slot; break;
            case 13: Slot13 = slot; break;
            case 14: Slot14 = slot; break;
            case 15: Slot15 = slot; break;
        }
    }
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
    public int SfxActiveCount { get; set; }
    public string Mode => AppFilteringEnabled ? "PerApp" : "Master";
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

    // Real-Time APO Telemetry reader (reads C:\Users\Public\permadb_apo_telemetry_v3.dat written by audiodg.exe)
    private const string ApoTelemetryPath = @"C:\Users\Public\permadb_apo_telemetry_v3.dat";
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

    // Virtual audio stream bridge for per-app stream separation
    private readonly VirtualStreamBridge _virtualBridge = new();
    private readonly AudioPolicyConfigClient _policyConfigClient = new();
    private string? _cachedVirtualSinkId;

    // Native WASAPI COM field reflection caches for zero-allocation, zero-leak session queries
    private static readonly FieldInfo? AsmField = typeof(AudioSessionManager).GetField("audioSessionInterface2", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? MeterNativeField = typeof(AudioMeterInformation).GetField("audioMeterInformation", BindingFlags.NonPublic | BindingFlags.Instance);

    private AudioMeterInformation? _cachedMeter;
    private AudioEndpointVolume? _cachedEndpointVolume;
    private IAudioMeterInformation? _cachedRawMeter;
    private IAudioSessionManager2? _cachedSessionManager;
    private IntPtr _channelPeaksNativeBuffer = IntPtr.Zero;
    private float[] _channelPeaksManaged = new float[2];
    private readonly AutoResetEvent _wakeSignal = new(false);
    private volatile bool _uiVisible = false;
    private int _sseClientCount = 0;
    private long _lastWebUiActivityTicks = 0;
    private volatile bool _trimRequested = false;
    private bool _initialTrimDone = false;
    private readonly DateTime _startupUtc = DateTime.UtcNow;
    private DateTime _lastMemoryTrimTime = DateTime.UtcNow;
    private DateTime _lastSfxSyncTime = DateTime.MinValue;
    private bool _fxWriteAccessDenied = false;

    private static readonly string[] TargetAudioModes =
    {
        "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}", // DEFAULT
        "{9E90EA20-B493-4FD1-A1A8-7E1361A956CF}", // RAW
        "{98951333-B9CD-48B1-A0A3-FF40682D73F7}", // COMMUNICATIONS
        "{FC1CFC9B-B9D6-4CFA-B5E0-4BB2166878B2}", // SPEECH
        "{4780004E-7133-41D8-8C74-660DADD2C0EE}", // MEDIA
        "{B26FEB0D-EC94-477C-9494-D1AB8E753F6E}", // MOVIE
        "{9CF2A70B-F377-403B-BD6B-360863E0355C}", // NOTIFICATION
        "{FC1CF0A4-3860-4592-8232-A567847BE2D1}", // Speech alias
        "{47800040-4533-461B-9B4C-9E9D77B11057}", // Media alias
        "{B26FEDB9-886C-430B-8018-CB5467E9FC0E}", // Movie alias
        "{00000000-0000-0000-0000-000000000000}"  // Wildcard
    };

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    // Process name and peak tracking for active WASAPI audio sessions
    private readonly ConcurrentDictionary<int, string> _processNameCache = new();
    private readonly ConcurrentDictionary<string, float> _appSmoothedPeak = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<int>> _appProcessIds = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastProtectedAudioTime = DateTime.MinValue;
    private DateTime _lastProtectedSpikeTime = DateTime.MinValue;
    private DateTime _lastBypassedAudioTime = DateTime.MinValue;
    private DateTime _lastLogTick = DateTime.MinValue;
    private DateTime _lastFxPropertiesCheck = DateTime.MinValue;

    // 50Hz Side-Channel Peak History for SFX Stream Correlation
    private readonly Dictionary<int, Queue<float>> _sessionPeakQueues = new();
    private readonly Queue<float>[] _slotPeakQueues = Enumerable.Range(0, 16).Select(_ => new Queue<float>()).ToArray();
    private readonly uint[] _lastSlotInstanceId = new uint[16];

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
        try
        {
            _channelPeaksNativeBuffer = Marshal.AllocHGlobal(8 * sizeof(float));
        }
        catch { }

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

    public void SetUiVisible(bool visible)
    {
        bool changed = _uiVisible != visible;
        _uiVisible = visible;
        if (visible)
        {
            PostToAudioThread(InternalPollMeter);
        }
        else if (changed)
        {
            _trimRequested = true;
            _wakeSignal.Set();
        }
    }

    public void SetSseClientCount(int count)
    {
        int prev = Interlocked.Exchange(ref _sseClientCount, Math.Max(0, count));
        if (prev == 0 && count > 0)
        {
            PostToAudioThread(InternalPollMeter);
        }
    }

    public void NotifyWebUiActivity()
    {
        long prevTicks = Interlocked.Read(ref _lastWebUiActivityTicks);
        long nowTicks = DateTime.UtcNow.Ticks;
        Interlocked.Exchange(ref _lastWebUiActivityTicks, nowTicks);
        if ((nowTicks - prevTicks) >= TimeSpan.FromSeconds(5).Ticks)
        {
            _wakeSignal.Set();
        }
    }

    private bool HasWebUiConsumer()
    {
        if (Volatile.Read(ref _sseClientCount) > 0) return true;
        long last = Interlocked.Read(ref _lastWebUiActivityTicks);
        return last > 0 && (DateTime.UtcNow.Ticks - last) < TimeSpan.FromSeconds(5).Ticks;
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
            _virtualBridge.UpdateCeiling(GetActivePresetLinearCeiling());
            InternalSyncVirtualBridge();
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

    public static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var s = name.Trim();
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Substring(0, s.Length - 4);
        }
        return s.ToLowerInvariant();
    }

    public void SetAppProtection(string processName, bool isProtected)
    {
        // App-Specific Limiting is temporarily disabled while Master Safety Limiter handles all audio
    }

    public void ToggleAppFiltering(bool? enabled = null)
    {
        bool wasEnabled = _config.Settings.AppFilteringEnabled;
        _config.Settings.AppFilteringEnabled = false;
        if (wasEnabled)
        {
            _config.Save();
            AppLogger.Log("[USER_ACTION] ToggleAppFiltering: OFF (Master Mode enforced)");
        }
        _lastProtectedAudioTime = DateTime.MinValue;
        _lastBypassedAudioTime = DateTime.MinValue;
        _lastSessionScan = DateTime.MinValue;
        PostToAudioThread(() =>
        {
            InternalSyncVirtualBridge();
            InternalEnforceCeiling();
            InternalPollMeter();
        });
    }

    private void InternalSyncVirtualBridge()
    {
        try
        {
            if (_config.Settings.AppFilteringEnabled && _config.Settings.Enabled && _enumerator != null && _activeDevice != null)
            {
                float ceiling = GetActivePresetLinearCeiling();
                _virtualBridge.Start(_enumerator, _activeDevice, ceiling);

                var virtualSink = VirtualStreamBridge.FindVirtualSinkDevice(_enumerator);
                if (virtualSink != null)
                {
                    _cachedVirtualSinkId = virtualSink.ID;
                    SyncAppRouting(_cachedVirtualSinkId);
                }
            }
            else
            {
                _virtualBridge.Stop();
                _policyConfigClient.ClearAllApplicationEndpoints();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AudioEngine] Error in InternalSyncVirtualBridge: {ex.Message}");
        }
    }

    private void SyncAppRouting(string? virtualSinkId)
    {
        if (string.IsNullOrWhiteSpace(virtualSinkId) || !_config.Settings.AppFilteringEnabled || !_config.Settings.Enabled)
        {
            _policyConfigClient.ResetAllRedirectedApps();
            return;
        }

        try
        {
            foreach (var kvp in _appProcessIds)
            {
                string normName = kvp.Key;
                var pids = kvp.Value;
                bool isProt = IsAppProtected(normName);

                foreach (var pid in pids)
                {
                    if (pid <= 0) continue;
                    uint uPid = (uint)pid;

                    if (isProt)
                    {
                        _policyConfigClient.SetApplicationEndpoint(uPid, virtualSinkId);
                    }
                    else
                    {
                        _policyConfigClient.SetApplicationEndpoint(uPid, null);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AudioEngine] SyncAppRouting exception: {ex.Message}");
        }
    }

    public bool IsAppProtected(string processName)
    {
        var norm = NormalizeProcessName(processName);
        if (string.IsNullOrWhiteSpace(norm)) return false;

        foreach (var kvp in _config.Settings.AppProtectionRules)
        {
            if (NormalizeProcessName(kvp.Key) == norm)
            {
                return kvp.Value;
            }
        }
        // In App-Specific Mode, ALL apps default to Bypassed (false).
        // No app starts limited unless explicitly toggled to Limited (true) by the user.
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
            _virtualBridge.UpdateCeiling(GetActivePresetLinearCeiling());
            InternalSyncVirtualBridge();
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
            _wakeSignal.Set();
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

            // Adaptive polling loop: 20 Hz (50ms) when UI/SSE is active, 2 Hz (500ms) idle in tray
            // Actions posted via PostToAudioThread signal _wakeSignal for <1ms command latency.
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                while (_actionQueue.TryTake(out var action))
                {
                    try { action(); } catch { }
                }

                // Poll audio meter (lightweight APO sync when hidden; full UI telemetry when visible)
                InternalPollMeter();

                // Smart threshold/hide-triggered memory trimming
                CheckAndTrimMemory();

                int pollIntervalMs = (_uiVisible || HasWebUiConsumer()) ? 50 : 500;
                _wakeSignal.WaitOne(pollIntervalMs);
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

    private void ReleaseCachedDeviceInterfaces()
    {
        if (_cachedRawMeter != null)
        {
            try { Marshal.FinalReleaseComObject(_cachedRawMeter); } catch { }
            _cachedRawMeter = null;
        }
        if (_cachedSessionManager != null)
        {
            try { Marshal.FinalReleaseComObject(_cachedSessionManager); } catch { }
            _cachedSessionManager = null;
        }
        _cachedMeter = null;
        _cachedEndpointVolume = null;
    }

    private void InternalRefreshActiveDevice()
    {
        if (_enumerator == null) return;

        try
        {
            ReleaseCachedDeviceInterfaces();
            if (_activeDevice != null && _volumeDelegate != null)
            {
                try { _activeDevice.AudioEndpointVolume.OnVolumeNotification -= _volumeDelegate; } catch { }
            }
            try { _activeDevice?.Dispose(); } catch { }
            _activeDevice = null;

            _activeDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (_activeDevice != null)
            {
                _cachedMeter = _activeDevice.AudioMeterInformation;
                _cachedEndpointVolume = _activeDevice.AudioEndpointVolume;
                try
                {
                    _cachedRawMeter = MeterNativeField?.GetValue(_cachedMeter) as IAudioMeterInformation;
                }
                catch { }
                try
                {
                    var asm = _activeDevice.AudioSessionManager;
                    _cachedSessionManager = AsmField?.GetValue(asm) as IAudioSessionManager2;
                }
                catch { }

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
                    case "custom1":
                    case "custom":
                        profile.SafeCeilingPercent = Math.Clamp(_config.Settings.Custom1PresetPercent, 10.0f, 100.0f);
                        profile.TargetSafeDbSpl = 50.0f + (profile.SafeCeilingPercent / 100.0f) * 38.0f;
                        break;
                    case "custom2":
                        profile.SafeCeilingPercent = Math.Clamp(_config.Settings.Custom2PresetPercent, 10.0f, 100.0f);
                        profile.TargetSafeDbSpl = 50.0f + (profile.SafeCeilingPercent / 100.0f) * 38.0f;
                        break;
                    default:
                        _config.Settings.ActivePreset = "safe";
                        profile.SafeCeilingPercent = 65.0f;
                        profile.TargetSafeDbSpl = 75.0f;
                        break;
                }

                _volumeDelegate = new AudioEndpointVolumeNotificationDelegate(OnVolumeNotificationReceived);
                _cachedEndpointVolume.OnVolumeNotification += _volumeDelegate;

                Console.WriteLine($"[AudioEngine] Active Device: {friendlyName} ({profile.DeviceType}, Preset={_config.Settings.ActivePreset}, Ceiling={profile.SafeCeilingPercent:F0}%)");
                EnsureEndpointFxProperties(id);
                UpdateCachedProfile(profile);
                InternalSyncVirtualBridge();
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
                finally
                {
                    if (_activeDevice == null || !string.Equals(ep.ID, _activeDevice.ID, StringComparison.OrdinalIgnoreCase))
                    {
                        try { ep.Dispose(); } catch { }
                    }
                }
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

    private static bool ContainsAllModes(string[]? currentModes, string[] targetModes)
    {
        if (currentModes == null || currentModes.Length < targetModes.Length) return false;
        for (int i = 0; i < targetModes.Length; i++)
        {
            bool found = false;
            for (int j = 0; j < currentModes.Length; j++)
            {
                if (string.Equals(currentModes[j], targetModes[i], StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
            if (!found) return false;
        }
        return true;
    }

    public void EnsureEndpointFxProperties(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return;

        const string clsidEfx = "{968ff234-1895-49f1-8b97-9d9075eb25b6}";
        const string clsidSfx = "{b41e8c71-2947-4e3a-965a-06385d8204d1}";

        const string pkeyLfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1";
        const string pkeyGfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2";
        const string pkeySfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5";
        const string pkeyMfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6";
        const string pkeyEfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7";
        const string pkeyCompositeSfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13";
        const string pkeyCompositeMfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},14";
        const string pkeyCompositeEfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15";
        const string pkeySfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},5";
        const string pkeyMfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},6";
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

            // Fast zero-exception read-only check before requesting write access
            using (var readKey = Registry.LocalMachine.OpenSubKey(subPath, false))
            {
                if (readKey == null) return;

                bool compSfxOk = (readKey.GetValue(pkeyCompositeSfx) as string[]) is { Length: 1 } cs &&
                                 string.Equals(cs[0], clsidSfx, StringComparison.OrdinalIgnoreCase);
                bool sfxModesOk = ContainsAllModes(readKey.GetValue(pkeySfxModes) as string[], TargetAudioModes);
                bool mfxClean = readKey.GetValue(pkeyMfx) == null &&
                                readKey.GetValue(pkeyCompositeMfx) == null &&
                                readKey.GetValue(pkeyMfxModes) == null &&
                                readKey.GetValue("PermadB_Created_CompositeMFX") == null;
                bool compEfxOk = (readKey.GetValue(pkeyCompositeEfx) as string[]) is { } ce &&
                                 Array.Exists(ce, s => string.Equals(s, clsidEfx, StringComparison.OrdinalIgnoreCase));
                bool efxModesOk = ContainsAllModes(readKey.GetValue(pkeyEfxModes) as string[], TargetAudioModes);

                if (string.Equals(readKey.GetValue(pkeyLfx) as string, clsidSfx, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(readKey.GetValue(pkeySfx) as string, clsidSfx, StringComparison.OrdinalIgnoreCase) &&
                    compSfxOk && sfxModesOk && mfxClean &&
                    string.Equals(readKey.GetValue(pkeyGfx) as string, clsidEfx, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(readKey.GetValue(pkeyEfx) as string, clsidEfx, StringComparison.OrdinalIgnoreCase) &&
                    compEfxOk && efxModesOk)
                {
                    return;
                }
            }

            if (_fxWriteAccessDenied) return;

            RegistryKey? key = null;
            try
            {
                key = Registry.LocalMachine.OpenSubKey(subPath, true);
            }
            catch (UnauthorizedAccessException)
            {
                _fxWriteAccessDenied = true;
                return;
            }
            catch (System.Security.SecurityException)
            {
                _fxWriteAccessDenied = true;
                return;
            }
            catch
            {
                try
                {
                    key = Registry.LocalMachine.OpenSubKey(
                        subPath,
                        RegistryKeyPermissionCheck.ReadWriteSubTree,
                        System.Security.AccessControl.RegistryRights.SetValue | System.Security.AccessControl.RegistryRights.QueryValues);
                }
                catch
                {
                    _fxWriteAccessDenied = true;
                    return;
                }
            }

            if (key != null)
            {
                using (key)
                {
                    // 0. Configure Legacy LFX (Stream / Local Effect APO)
                    var lfx = key.GetValue(pkeyLfx) as string;
                    if (!string.Equals(lfx, clsidSfx, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(lfx) && key.GetValue("PermadB_Backup_LFX") == null)
                        {
                            key.SetValue("PermadB_Backup_LFX", lfx, RegistryValueKind.String);
                        }
                        key.SetValue(pkeyLfx, clsidSfx, RegistryValueKind.String);
                    }

                    // 1. Configure SFX (Pre-Mix Stream Effect APO) - Sole entry to prevent OEM DTS chain drops
                    var sfx = key.GetValue(pkeySfx) as string;
                    if (!string.Equals(sfx, clsidSfx, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(sfx) && key.GetValue("PermadB_Backup_SFX") == null)
                        {
                            key.SetValue("PermadB_Backup_SFX", sfx, RegistryValueKind.String);
                        }
                        key.SetValue(pkeySfx, clsidSfx, RegistryValueKind.String);
                    }

                    var compSfx = key.GetValue(pkeyCompositeSfx) as string[];
                    if (compSfx != null && compSfx.Length > 0)
                    {
                        var oemSfx = compSfx.Where(s => !string.Equals(s, clsidSfx, StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (oemSfx.Length > 0 && key.GetValue("PermadB_Backup_CompositeSFX") == null)
                        {
                            key.SetValue("PermadB_Backup_CompositeSFX", compSfx, RegistryValueKind.MultiString);
                        }
                    }
                    if (compSfx == null || compSfx.Length != 1 || !string.Equals(compSfx[0], clsidSfx, StringComparison.OrdinalIgnoreCase))
                    {
                        key.SetValue(pkeyCompositeSfx, new[] { clsidSfx }, RegistryValueKind.MultiString);
                    }

                    var sfxModes = key.GetValue(pkeySfxModes) as string[];
                    if (sfxModes == null || sfxModes.Length == 0)
                    {
                        key.SetValue(pkeySfxModes, TargetAudioModes, RegistryValueKind.MultiString);
                    }
                    else if (!ContainsAllModes(sfxModes, TargetAudioModes))
                    {
                        key.SetValue(pkeySfxModes, sfxModes.Union(TargetAudioModes, StringComparer.OrdinalIgnoreCase).ToArray(), RegistryValueKind.MultiString);
                    }

                    // 1b. Clean up MFX: MFX is post-mode-mix (sums all apps together before processing), which causes cross-app bleeding!
                    try { key.DeleteValue(pkeyMfx, false); } catch { }
                    try { key.DeleteValue(pkeyCompositeMfx, false); } catch { }
                    try { key.DeleteValue(pkeyMfxModes, false); } catch { }
                    try { key.DeleteValue("PermadB_Created_CompositeMFX", false); } catch { }

                    // 2. Configure Legacy GFX (Endpoint / Global Effect APO)
                    var gfx = key.GetValue(pkeyGfx) as string;
                    if (!string.Equals(gfx, clsidEfx, StringComparison.OrdinalIgnoreCase))
                    {
                        key.SetValue(pkeyGfx, clsidEfx, RegistryValueKind.String);
                    }

                    // 3. Configure EFX (Post-Mix Master Limiter APO)
                    var efx = key.GetValue(pkeyEfx) as string;
                    if (!string.Equals(efx, clsidEfx, StringComparison.OrdinalIgnoreCase))
                    {
                        key.SetValue(pkeyEfx, clsidEfx, RegistryValueKind.String);
                    }

                    var compEfx = key.GetValue(pkeyCompositeEfx) as string[];
                    if (compEfx == null || !compEfx.Contains(clsidEfx, StringComparer.OrdinalIgnoreCase))
                    {
                        var newCompEfx = compEfx == null ? new[] { clsidEfx } : compEfx.Append(clsidEfx).ToArray();
                        key.SetValue(pkeyCompositeEfx, newCompEfx, RegistryValueKind.MultiString);
                    }

                    var efxModes = key.GetValue(pkeyEfxModes) as string[];
                    if (efxModes == null || efxModes.Length == 0)
                    {
                        key.SetValue(pkeyEfxModes, TargetAudioModes, RegistryValueKind.MultiString);
                    }
                    else if (!ContainsAllModes(efxModes, TargetAudioModes))
                    {
                        key.SetValue(pkeyEfxModes, efxModes.Union(TargetAudioModes, StringComparer.OrdinalIgnoreCase).ToArray(), RegistryValueKind.MultiString);
                    }
                }
            }
        }
        catch { }
    }

    public void SendCeilingToApo()
    {
        PostToAudioThread(() => InternalSendCeilingToApo());
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

    private bool InternalSendCeilingToApo()
    {
        bool commandChanged = false;
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
                _virtualBridge.UpdateCeiling(GetActivePresetLinearCeiling());

                // DUAL-LAYER DEFENSE IN APP-SPECIFIC MODE:
                // Layer 1: Apps that follow Windows Default Audio Routing (Brave, Discord, Spotify, etc.)
                //          route via AudioPolicyConfig to VoiceMeeter Input and are brickwall-clamped by VirtualStreamBridge.
                // Layer 2: Legacy/hardcoded apps (e.g. Audacity configured with explicit MME device index)
                //          that stream directly to Master (HyperX):
                //          If a Limited app is audible on Master AND NO Bypassed app is audible, clamp Master EFX
                //          to GetActivePresetLinearCeiling() to guarantee ear protection!
                //          If a Bypassed app IS playing on Master, keep Master EFX relaxed at -1.0 dBFS (100% blast).
                var audibleSessions = _cachedAppSessions
                    .Where(s => s.PeakValue > 0.005f &&
                                !s.ProcessName.Equals("System Sounds", StringComparison.OrdinalIgnoreCase) &&
                                !s.ProcessName.Equals("PermadB", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                bool anyLimitedOnMaster = audibleSessions.Any(s => IsAppProtected(s.ProcessName));
                bool anyBypassedOnMaster = audibleSessions.Any(s => !IsAppProtected(s.ProcessName));

                if (anyLimitedOnMaster && !anyBypassedOnMaster)
                {
                    linear = GetActivePresetLinearCeiling();
                    dbfs = 20.0f * (float)Math.Log10(linear);
                }
                else
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
                commandChanged = Math.Abs(linear - _lastSentCeilingLinear) > 0.001f || enabledInt != _lastSentEnabled;
                if (commandChanged)
                {
                    _apoCommandSeq++;
                    int offsetMagic = (int)Marshal.OffsetOf<PermadBApoTelemetry>(nameof(PermadBApoTelemetry.Magic));
                    int offsetVersion = (int)Marshal.OffsetOf<PermadBApoTelemetry>(nameof(PermadBApoTelemetry.Version));
                    int offsetLinear = (int)Marshal.OffsetOf<PermadBApoTelemetry>(nameof(PermadBApoTelemetry.TargetCeilingLinear));
                    int offsetDbfs = (int)Marshal.OffsetOf<PermadBApoTelemetry>(nameof(PermadBApoTelemetry.TargetCeilingDbfs));
                    int offsetEnabled = (int)Marshal.OffsetOf<PermadBApoTelemetry>(nameof(PermadBApoTelemetry.IsEnabled));
                    int offsetSeq = (int)Marshal.OffsetOf<PermadBApoTelemetry>(nameof(PermadBApoTelemetry.CommandSeq));
                    int offsetMasterSafety = (int)Marshal.OffsetOf<PermadBApoTelemetry>(nameof(PermadBApoTelemetry.MasterSafetyCeilingLinear));

                    _telemetryAcc.Write(offsetMagic, (uint)0x5045524D); // "PERM"
                    _telemetryAcc.Write(offsetVersion, (uint)4); // Version 4: Dual-Stage SFX + EFX + Diagnostics
                    _telemetryAcc.Write(offsetLinear, linear);
                    _telemetryAcc.Write(offsetDbfs, dbfs);
                    _telemetryAcc.Write(offsetEnabled, (uint)enabledInt);
                    _telemetryAcc.Write(offsetSeq, _apoCommandSeq);
                    _telemetryAcc.Write(offsetMasterSafety, 0.8912509f); // -1.0 dBFS Master Acoustic Brickwall

                    _lastSentCeilingLinear = linear;
                    _lastSentEnabled = enabledInt;

                    string apoMsg = $"[APO_IPC] Dynamic ceiling sent to APO: Mode={(_config.Settings.AppFilteringEnabled ? "AppSpecific" : "Master")}, Ceiling={ceilingPercent:F0}% ({linear:F4} linear, {dbfs:F1} dBFS, target={profile.TargetSafeDbSpl:F0}dBA, enabled={isEnabled}, seq={_apoCommandSeq})";
                    Console.WriteLine($"[AudioEngine] {apoMsg}");
                    AppLogger.Log(apoMsg);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AudioEngine] Error sending ceiling to APO: {ex.Message}");
        }
        return commandChanged;
    }

    private void CorrelateAndSyncSfxSlots(List<AppSessionInfo> activeSessions, float safeLinear)
    {
        if (_telemetryAcc == null) return;

        try
        {
            if (!_config.Settings.AppFilteringEnabled)
            {
                // Fast path for Master Mode: ensure all active slots are clamped to safeLinear with bypass=0 without queue/LINQ allocations
                int s0Offset = (int)Marshal.OffsetOf<PermadBApoTelemetry>(nameof(PermadBApoTelemetry.Slot0));
                int sSize = Marshal.SizeOf<PermadBSfxSlot>();
                int offTarget = (int)Marshal.OffsetOf<PermadBSfxSlot>(nameof(PermadBSfxSlot.TargetCeilingLinear));
                int offBypass = (int)Marshal.OffsetOf<PermadBSfxSlot>(nameof(PermadBSfxSlot.Bypass));

                for (int i = 0; i < 16; i++)
                {
                    int slotOffset = s0Offset + (i * sSize);
                    _telemetryAcc.Read(slotOffset, out PermadBSfxSlot slot);
                    if (slot.Active != 0)
                    {
                        if (Math.Abs(slot.TargetCeilingLinear - safeLinear) > 0.001f)
                        {
                            _telemetryAcc.Write(slotOffset + offTarget, safeLinear);
                        }
                        if (slot.Bypass != 0)
                        {
                            _telemetryAcc.Write(slotOffset + offBypass, 0u);
                        }
                    }
                }
                return;
            }

            // 1. Maintain sliding history of session peaks (5 ticks = 100ms)
            var currentPids = new HashSet<int>();
            foreach (var session in activeSessions)
            {
                if (session.ProcessId <= 0) continue;
                currentPids.Add(session.ProcessId);
                if (!_sessionPeakQueues.TryGetValue(session.ProcessId, out var queue))
                {
                    queue = new Queue<float>(5);
                    _sessionPeakQueues[session.ProcessId] = queue;
                }
                if (queue.Count >= 5) queue.Dequeue();
                queue.Enqueue(session.PeakValue);
            }

            // Prune dead sessions periodically
            if (_sessionPeakQueues.Count > 30)
            {
                var deadPids = _sessionPeakQueues.Keys.Where(p => !currentPids.Contains(p)).ToList();
                foreach (var dp in deadPids) _sessionPeakQueues.Remove(dp);
            }

            int slot0Offset = (int)Marshal.OffsetOf<PermadBApoTelemetry>(nameof(PermadBApoTelemetry.Slot0));
            int slotSize = Marshal.SizeOf<PermadBSfxSlot>();

            int offsetTarget = (int)Marshal.OffsetOf<PermadBSfxSlot>(nameof(PermadBSfxSlot.TargetCeilingLinear));
            int offsetBypass = (int)Marshal.OffsetOf<PermadBSfxSlot>(nameof(PermadBSfxSlot.Bypass));
            int offsetMatchedPid = (int)Marshal.OffsetOf<PermadBSfxSlot>(nameof(PermadBSfxSlot.MatchedProcessId));

            // Only consider genuine audible sessions (> 0.003f) and exclude "System Sounds"
            var sessionsWithAudio = activeSessions
                .Where(s => s.ProcessId > 0 && s.PeakValue > 0.003f && !s.ProcessName.Equals("System Sounds", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var distinctActiveAppNames = sessionsWithAudio.Select(s => NormalizeProcessName(s.ProcessName)).Distinct().ToList();

            // 2. Read active slots and validate/invalidate stale PID bindings
            var unmatchedActiveSlots = new List<(int Index, int Offset, PermadBSfxSlot Slot)>();
            for (int i = 0; i < 16; i++)
            {
                int sOff = slot0Offset + (i * slotSize);
                _telemetryAcc.Read(sOff, out PermadBSfxSlot s);
                if (s.Active != 0 && s.InputPeak > 0.002f)
                {
                    if (s.MatchedProcessId > 0)
                    {
                        var matchingSession = activeSessions.FirstOrDefault(sess => sess.ProcessId == (int)s.MatchedProcessId);
                        if (matchingSession == null)
                        {
                            var appKey = _appProcessIds.FirstOrDefault(kvp => kvp.Value.Contains((int)s.MatchedProcessId)).Key;
                            if (!string.IsNullOrEmpty(appKey))
                            {
                                matchingSession = activeSessions.FirstOrDefault(sess => NormalizeProcessName(sess.ProcessName) == appKey);
                            }
                        }
                        // Stale binding detection: if the session no longer exists or is silent (<= 0.002f) while this slot is active (> 0.005f)
                        if (matchingSession == null || (matchingSession.PeakValue <= 0.002f && s.InputPeak > 0.005f))
                        {
                            _telemetryAcc.Write(sOff + offsetMatchedPid, 0u);
                            s.MatchedProcessId = 0;
                        }
                    }

                    if (s.MatchedProcessId == 0)
                    {
                        unmatchedActiveSlots.Add((i, sOff, s));
                    }
                }
            }

            // 1-Tick Fast-Path Correlation: If all active audio sessions belong to one application, bind ALL active slots producing audio to that PID on Tick 1!
            if (distinctActiveAppNames.Count == 1 && unmatchedActiveSlots.Count > 0)
            {
                uint matchedPid = (uint)sessionsWithAudio[0].ProcessId;
                foreach (var slotItem in unmatchedActiveSlots)
                {
                    _telemetryAcc.Write(slotItem.Offset + offsetMatchedPid, matchedPid);
                }
            }

            // 3. Correlate and sync each SFX slot
            for (int i = 0; i < 16; i++)
            {
                int slotOffset = slot0Offset + (i * slotSize);
                _telemetryAcc.Read(slotOffset, out PermadBSfxSlot slot);

                if (slot.Active == 0)
                {
                    _slotPeakQueues[i].Clear();
                    _lastSlotInstanceId[i] = 0;
                    continue;
                }

                if (_lastSlotInstanceId[i] != slot.InstanceId)
                {
                    _lastSlotInstanceId[i] = slot.InstanceId;
                    _slotPeakQueues[i].Clear();
                }

                var slotQueue = _slotPeakQueues[i];
                if (slotQueue.Count >= 5) slotQueue.Dequeue();
                // Enqueue InputPeak: WASAPI session PeakValue measures pre-APO stream peak
                slotQueue.Enqueue(slot.InputPeak);

                uint matchedPid = slot.MatchedProcessId;

                // 4. Peak correlation match if not yet bound
                if (matchedPid == 0 && slot.InputPeak > 0.002f)
                {
                    // Fast-path: Communications mode (VoIP streams like Discord)
                    if (slot.AudioProcessingMode == 1) // Communications
                    {
                        var commSession = sessionsWithAudio.FirstOrDefault(s => CommunicationApps.Contains(s.ProcessName));
                        if (commSession != null)
                        {
                            matchedPid = (uint)commSession.ProcessId;
                        }
                    }

                    float currentSlotPeak = slot.InputPeak;
                    float bestDistance = float.MaxValue;
                    int bestPid = 0;

                    // Peak envelope correlation using minimum L1 distance over history
                    if (matchedPid == 0 && slotQueue.Count >= 3)
                    {
                        float[] slotHistory = slotQueue.ToArray();
                        foreach (var session in sessionsWithAudio)
                        {
                            if (session.ProcessId <= 0) continue;
                            if (_sessionPeakQueues.TryGetValue(session.ProcessId, out var sQueue) && sQueue.Count >= 3)
                            {
                                float[] sHistory = sQueue.ToArray();
                                int count = Math.Min(slotHistory.Length, sHistory.Length);
                                float diffSum = 0.0f;
                                for (int k = 0; k < count; k++)
                                {
                                    diffSum += Math.Abs(slotHistory[slotHistory.Length - 1 - k] - sHistory[sHistory.Length - 1 - k]);
                                }
                                float avgDiff = diffSum / count;
                                if (avgDiff < bestDistance)
                                {
                                    bestDistance = avgDiff;
                                    bestPid = session.ProcessId;
                                }
                            }
                        }
                    }

                    // Fallback: instant single-tick absolute peak distance
                    if (matchedPid == 0 && bestPid == 0 && sessionsWithAudio.Count > 0)
                    {
                        foreach (var session in sessionsWithAudio)
                        {
                            float diff = Math.Abs(currentSlotPeak - session.PeakValue);
                            if (diff < bestDistance)
                            {
                                bestDistance = diff;
                                bestPid = session.ProcessId;
                            }
                        }
                    }

                    if (bestPid > 0)
                    {
                        matchedPid = (uint)bestPid;
                    }

                    if (matchedPid > 0)
                    {
                        _telemetryAcc.Write(slotOffset + offsetMatchedPid, matchedPid);
                    }
                }

                // 5. Determine target ceiling and bypass for this slot
                float streamCeiling = safeLinear;
                uint streamBypass = 0;

                if (_config.Settings.AppFilteringEnabled)
                {
                    if (matchedPid > 0)
                    {
                        var matchedSession = activeSessions.FirstOrDefault(s => s.ProcessId == (int)matchedPid);
                        string procName = matchedSession?.ProcessName ?? string.Empty;
                        if (string.IsNullOrEmpty(procName))
                        {
                            foreach (var kvp in _appProcessIds)
                            {
                                if (kvp.Value.Contains((int)matchedPid))
                                {
                                    procName = kvp.Key;
                                    break;
                                }
                            }
                        }
                        if (string.IsNullOrEmpty(procName))
                        {
                            try
                            {
                                var p = Process.GetProcessById((int)matchedPid);
                                procName = p.ProcessName;
                            }
                            catch { }
                        }
                        bool isProtected = IsAppProtected(procName);

                        if (isProtected)
                        {
                            // Limited app (explicitly toggled by user in UI): clamped strictly to safe preset ceiling
                            streamCeiling = safeLinear;
                            streamBypass = 0;
                        }
                        else
                        {
                            // Bypassed app (default for all apps unless toggled): un-limited in SFX
                            streamCeiling = 0.8912509f;
                            streamBypass = 1;
                        }
                    }
                    else
                    {
                        // Unmatched slot with App-Specific Mode ON:
                        bool anyProtectedAudible = sessionsWithAudio.Any(s => IsAppProtected(s.ProcessName));
                        bool anyBypassedAudible = sessionsWithAudio.Any(s => !IsAppProtected(s.ProcessName));

                        // Only clamp an unmatched slot if the ONLY audible sound on the entire PC is from a Limited app.
                        // If a Bypassed app is playing (or nothing is playing), NEVER clamp unmatched slots!
                        if (anyProtectedAudible && !anyBypassedAudible)
                        {
                            streamCeiling = safeLinear;
                            streamBypass = 0;
                        }
                        else
                        {
                            streamCeiling = 0.8912509f;
                            streamBypass = 1;
                        }
                    }
                }
                else
                {
                    // Default Master Mode: every stream is actively limited in its own SFX stage
                    // completely eliminating inter-app ducking / pumping!
                    streamCeiling = safeLinear;
                    streamBypass = 0;
                }

                // Push updates to shared memory if changed
                if (Math.Abs(slot.TargetCeilingLinear - streamCeiling) > 0.001f)
                {
                    _telemetryAcc.Write(slotOffset + offsetTarget, streamCeiling);
                }
                if (slot.Bypass != streamBypass)
                {
                    _telemetryAcc.Write(slotOffset + offsetBypass, streamBypass);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CorrelateAndSyncSfxSlots] Warning: {ex.Message}");
        }
    }

    private void InternalEnforceCeiling()
    {
        // Limiting is executed directly on PCM inside audiodg.exe via PermadBApo.dll.
        // Windows volume sliders are NEVER altered.
        InternalSyncVirtualBridge();
        InternalSendCeilingToApo();

        int count = 0;
        if (_activeDevice != null) count++;

        if (_config.Settings.ApplyToAllDevices)
        {
            lock (_stateLock)
            {
                if (_cachedDevices.Count > 0)
                {
                    count = _cachedDevices.Count;
                }
            }
        }

        _cachedProtectedCount = Math.Max(1, count);
    }

    private void InternalPollMeter()
    {
        if (_activeDevice == null || _cachedMeter == null || _cachedEndpointVolume == null) return;

        try
        {
            var utcNow = DateTime.UtcNow;
            var profile = GetActiveProfile();
            bool commandChanged = InternalSendCeilingToApo();

            // Periodically ensure active device FxProperties are configured (30s, zero-exception read-only first)
            if ((utcNow - _lastFxPropertiesCheck).TotalSeconds >= 30.0)
            {
                _lastFxPropertiesCheck = utcNow;
                EnsureActiveDeviceFxProperties();
            }

            // Check real-time APO telemetry from audiodg.exe
            bool hasApoTelemetry = TryReadApoTelemetry(out var telem);

            bool uiVisible = _uiVisible;
            bool webUiActive = HasWebUiConsumer();
            if (!uiVisible && !webUiActive)
            {
                // Tray-idle fast path: keep Master SFX slots aligned when ceiling changes or every 2s,
                // while skipping cross-process COM peak meters and session enumeration!
                if (commandChanged || (utcNow - _lastSfxSyncTime).TotalSeconds >= 2.0)
                {
                    _lastSfxSyncTime = utcNow;
                    if (_config.Settings.AppFilteringEnabled || (hasApoTelemetry && telem.SfxActiveCount > 0))
                    {
                        CorrelateAndSyncSfxSlots(_cachedAppSessions, GetActivePresetLinearCeiling());
                    }
                }

                if (_cachedAppSessions.Count > 0 || !_appProcessIds.IsEmpty || !_appSmoothedPeak.IsEmpty || _sessionPeakQueues.Count > 0)
                {
                    _cachedAppSessions = new List<AppSessionInfo>();
                    _appProcessIds.Clear();
                    _appSmoothedPeak.Clear();
                    _sessionPeakQueues.Clear();
                }
                return;
            }

            var meter = _cachedMeter;
            var endpointVol = _cachedEndpointVolume;
            var meterPeak = meter.MasterPeakValue;
            var volScalar = endpointVol.MasterVolumeLevelScalar;
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

            float[] channelPeaks = _channelPeaksManaged;
            if (_cachedRawMeter != null && _channelPeaksNativeBuffer != IntPtr.Zero)
            {
                try
                {
                    _cachedRawMeter.GetMeteringChannelCount(out int chCount);
                    if (chCount > 0 && chCount <= 8)
                    {
                        if (_channelPeaksManaged.Length != chCount)
                        {
                            _channelPeaksManaged = new float[chCount];
                        }
                        _cachedRawMeter.GetChannelsPeakValues(chCount, _channelPeaksNativeBuffer);
                        Marshal.Copy(_channelPeaksNativeBuffer, _channelPeaksManaged, 0, chCount);
                        if (isApoRecentlyActive)
                        {
                            for (int c = 0; c < chCount; c++)
                            {
                                if (_channelPeaksManaged[c] > peak) _channelPeaksManaged[c] = peak;
                            }
                        }
                        channelPeaks = _channelPeaksManaged;
                    }
                }
                catch { }
            }

            // Active Hearing Safety Monitoring: Detect if acoustic output exceeds safe target for UI meter stats
            bool isExceedingCeiling = estimatedPeakSpl > profile.TargetSafeDbSpl;
            if (isExceedingCeiling)
            {
                OnPeakClamped?.Invoke(peakDbfs);
            }

            // Keep Master SFX slots synchronized when ceiling changes even if only WinForms UI is open
            if (!webUiActive && (commandChanged || (utcNow - _lastSfxSyncTime).TotalSeconds >= 1.5))
            {
                _lastSfxSyncTime = utcNow;
                if (hasApoTelemetry && telem.SfxActiveCount > 0)
                {
                    CorrelateAndSyncSfxSlots(_cachedAppSessions, GetActivePresetLinearCeiling());
                }
            }

            // Per-Application Sound Mixer & Active Session Enumeration (only when Web UI is active or AppFiltering enabled)
            // INVARIANT: Native COM pointers are 100% explicitly released with Marshal.ReleaseComObject / FinalReleaseComObject.
            // NAudio's leaky AudioSessionManager.RefreshSessions() is NEVER called, preventing Audiosrv svchost bloat.
            int sessionScanInterval = _config.Settings.AppFilteringEnabled ? 250 : 1000;
            if ((_config.Settings.AppFilteringEnabled || webUiActive) && (utcNow - _lastSessionScan).TotalMilliseconds >= sessionScanInterval)
            {
                _lastSessionScan = utcNow;
                var appSessionsList = new List<AppSessionInfo>();
                try
                {
                    var mgr2 = _cachedSessionManager;
                    if (mgr2 != null)
                    {
                        int hrEnum = mgr2.GetSessionEnumerator(out var sessionEnum);
                        if (hrEnum == 0 && sessionEnum != null)
                        {
                            try
                            {
                                int hrCount = sessionEnum.GetCount(out int sessionCount);
                                if (hrCount == 0 && sessionCount > 0)
                                {
                                    var rawSessions = new List<(uint Pid, string ProcName, string DisplayName, float Vol, bool Mute, float RawPeak)>();
                                    for (int i = 0; i < sessionCount; i++)
                                    {
                                        int hrS = sessionEnum.GetSession(i, out var s);
                                        if (hrS == 0 && s != null)
                                        {
                                            try
                                            {
                                                int sState = 0;
                                                s.GetState(out sState);
                                                if (sState == 2) continue; // 2 = AudioSessionStateExpired

                                                uint pid = 0;
                                                string displayName = string.Empty;
                                                if (s is IAudioSessionControl2 ctl2)
                                                {
                                                    ctl2.GetProcessId(out pid);
                                                    ctl2.GetDisplayName(out displayName);
                                                }

                                                string procName = "System Sounds";
                                                if (pid > 0)
                                                {
                                                    procName = _processNameCache.GetOrAdd((int)pid, p =>
                                                    {
                                                        try
                                                        {
                                                            using var proc = Process.GetProcessById(p);
                                                            return proc.ProcessName;
                                                        }
                                                        catch { return $"PID {p}"; }
                                                    });
                                                }

                                                float appVol = 1.0f;
                                                bool appMute = false;
                                                if (s is ISimpleAudioVolume simpleVol)
                                                {
                                                    simpleVol.GetMasterVolume(out appVol);
                                                    simpleVol.GetMute(out appMute);
                                                }

                                                float rawAppPeak = 0.0f;
                                                if (s is IAudioMeterInformation appMeter)
                                                {
                                                    appMeter.GetPeakValue(out rawAppPeak);
                                                }

                                                rawSessions.Add((pid, procName, displayName, appVol, appMute, rawAppPeak));
                                            }
                                            finally
                                            {
                                                try { Marshal.FinalReleaseComObject(s); } catch { }
                                            }
                                        }
                                    }

                                    // Group sessions by normalized process name so multi-session apps (e.g. Brave, Chrome, Discord)
                                    // consolidate all their tabs into ONE entry with the MAXIMUM peak across all sessions!
                                    var groupedSessions = rawSessions
                                        .GroupBy(r => NormalizeProcessName(r.ProcName), StringComparer.OrdinalIgnoreCase)
                                        .ToList();

                                    foreach (var group in groupedSessions)
                                    {
                                        string normName = group.Key;
                                        if (string.IsNullOrWhiteSpace(normName)) continue;

                                        // Take the MAXIMUM peak across all sessions so silent background tabs NEVER dilute active audio
                                        float maxRawPeak = group.Max(r => r.RawPeak);
                                        float maxVol = group.Max(r => r.Vol);
                                        bool isMute = group.All(r => r.Mute);

                                        var primary = group.OrderByDescending(r => r.RawPeak).First();
                                        string procName = primary.ProcName;
                                        var named = group.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.DisplayName));
                                        string displayName = !string.IsNullOrWhiteSpace(named.DisplayName) ? named.DisplayName : procName;
                                        uint primaryPid = primary.Pid;

                                        // Store all PIDs belonging to this application
                                        var allPids = group.Select(r => (int)r.Pid).Where(p => p > 0).Distinct().ToList();
                                        _appProcessIds[normName] = allPids;

                                        // Auto-route active sessions when App-Specific Mode is engaged
                                        if (_config.Settings.AppFilteringEnabled && _config.Settings.Enabled && !string.IsNullOrWhiteSpace(_cachedVirtualSinkId))
                                        {
                                            bool isTargetProt = IsAppProtected(normName);
                                            foreach (var p in allPids)
                                            {
                                                uint uP = (uint)p;
                                                if (isTargetProt)
                                                {
                                                    _policyConfigClient.SetApplicationEndpoint(uP, _cachedVirtualSinkId);
                                                }
                                                else
                                                {
                                                    _policyConfigClient.SetApplicationEndpoint(uP, null);
                                                }
                                            }
                                        }

                                        var smoothedPeak = _appSmoothedPeak.AddOrUpdate(
                                            normName,
                                            maxRawPeak,
                                            (_, prev) => Math.Max(maxRawPeak, prev * 0.75f + maxRawPeak * 0.25f)
                                        );

                                        var appPeakDbfs = smoothedPeak > 0.00001f ? 20.0f * (float)Math.Log10(smoothedPeak) : -96.0f;
                                        bool isVoip = CommunicationApps.Contains(procName);
                                        float crestFactor = isVoip ? 15.0f : 11.0f;
                                        var appEstPeakSpl = Math.Clamp(profile.EstimatedMaxDbSpl + appPeakDbfs + masterDbAtten, 25.0f, 120.0f);
                                        var appEstContinuousSpl = Math.Clamp(appEstPeakSpl - crestFactor, 25.0f, 105.0f);

                                        bool isProt = IsAppProtected(normName);
                                        bool isAppClamped = false;

                                        // Clean noise gate: 0.005f (-46 dBFS) ignores DAC dither and idle mic silence
                                        bool isAppActive = maxRawPeak > 0.005f || smoothedPeak > 0.005f;
                                        bool isAppSpike = appEstContinuousSpl > profile.TargetSafeDbSpl || maxRawPeak > 0.35f;

                                        if (isAppActive)
                                        {
                                            if (isProt)
                                            {
                                                _lastProtectedAudioTime = DateTime.UtcNow;
                                                if (isAppSpike)
                                                {
                                                    _lastProtectedSpikeTime = DateTime.UtcNow;
                                                }
                                            }
                                            else if (primaryPid > 0 && !normName.Equals("system sounds", StringComparison.OrdinalIgnoreCase))
                                            {
                                                _lastBypassedAudioTime = DateTime.UtcNow;
                                            }
                                        }

                                        if (_config.Settings.Enabled)
                                        {
                                            if (_config.Settings.AppFilteringEnabled)
                                            {
                                                isAppClamped = isProt && isAppActive && (_virtualBridge.CurrentGainReductionDb > 0.05f || maxRawPeak > _virtualBridge.LinearCeiling);
                                            }
                                            else
                                            {
                                                isAppClamped = isClamping;
                                            }
                                        }

                                        appSessionsList.Add(new AppSessionInfo
                                        {
                                            ProcessId = (int)primaryPid,
                                            ProcessName = procName,
                                            DisplayName = string.IsNullOrWhiteSpace(displayName) ? procName : displayName,
                                            VolumePercent = (float)Math.Round(maxVol * 100.0f, 1),
                                            PeakValue = (float)Math.Round(smoothedPeak, 3),
                                            PeakDbfs = (float)Math.Round(appPeakDbfs, 1),
                                            EstimatedDbSpl = (float)Math.Round(appEstContinuousSpl, 1),
                                            IsMuted = isMute,
                                            IsClamped = isAppClamped,
                                            IsProtected = isProt,
                                            IsUnsafe = appEstContinuousSpl > profile.TargetSafeDbSpl
                                        });
                                    }
                                }
                            }
                            finally
                            {
                                try { Marshal.FinalReleaseComObject(sessionEnum); } catch { }
                            }
                        }
                    }

                    // Periodic prune of process/peak caches to prevent unbounded growth over weeks
                    if (_processNameCache.Count > 250)
                    {
                        _processNameCache.Clear();
                    }
                    if (_appSmoothedPeak.Count > 250)
                    {
                        _appSmoothedPeak.Clear();
                    }
                    if (_appProcessIds.Count > 250)
                    {
                        _appProcessIds.Clear();
                    }

                    _cachedAppSessions = appSessionsList;
                    CorrelateAndSyncSfxSlots(appSessionsList, GetActivePresetLinearCeiling());

                    if (AppLogger.VerboseEnabled && (utcNow - _lastLogTick).TotalSeconds >= 3.0)
                    {
                        _lastLogTick = utcNow;
                        var summaries = appSessionsList.Select(x => $"{x.ProcessName}(pk={x.PeakValue:F3},{(x.IsProtected ? "Limited" : "Bypassed")}{(x.IsClamped ? ",clmp" : "")})");
                        float apoDbfs = _lastSentCeilingLinear > 0.0001f ? 20.0f * (float)Math.Log10(_lastSentCeilingLinear) : -96.0f;
                        string bridgeStatus = _virtualBridge.IsActive ? $"ACTIVE(GR={_virtualBridge.CurrentGainReductionDb:F1}dB,act={_virtualBridge.LimiterActivations})" : "OFF";
                        uint sfxCount = hasApoTelemetry ? telem.SfxActiveCount : 0;
                        AppLogger.Log($"[HEARTBEAT] Mode={(_config.Settings.AppFilteringEnabled ? "AppSpecific" : "Master")}, Preset={_config.Settings.ActivePreset}({profile.SafeCeilingPercent:F0}%), SFX_Instances={sfxCount}, EfxProc={telem.EfxProcessCount}, SfxInit={telem.SfxInitCount}, SfxLock={telem.SfxLockCount}, SfxProc={telem.SfxProcessCount}, SfxCh={telem.LastSfxChannels}, Bridge={bridgeStatus}, APO={_lastSentCeilingLinear:F4}({apoDbfs:F1}dBFS), MtrPeak={meterPeak:F2}, MtrVol={volScalar:P0}, Sessions=[{string.Join(", ", summaries)}]");
                    }
                }
                catch { }
            }

            // In-place zero-allocation metrics update
            lock (_stateLock)
            {
                _latestMetrics.MasterPeak = peak;
                _latestMetrics.PeakDbfs = (float)Math.Round(peakDbfs, 1);
                _latestMetrics.EstimatedDbSpl = (float)Math.Round(estimatedContinuousSpl, 1);
                _latestMetrics.CurrentVolumePercent = (float)Math.Round(volScalar * 100.0f, 1);
                _latestMetrics.SafeCeilingPercent = profile.SafeCeilingPercent;
                _latestMetrics.IsClamping = isClamping;
                _latestMetrics.SpikesClampedTotal = spikesClamped;
                _latestMetrics.ChannelPeaks = channelPeaks;
                _latestMetrics.ActiveDeviceId = profile.DeviceId;
                _latestMetrics.ActiveDeviceName = profile.DeviceName;
                _latestMetrics.ActiveDeviceType = profile.DeviceType;
                _latestMetrics.Enabled = _config.Settings.Enabled;
                _latestMetrics.ApplyToAllDevices = _config.Settings.ApplyToAllDevices;
                _latestMetrics.ProtectMicrophoneInputs = _config.Settings.ProtectMicrophoneInputs;
                _latestMetrics.ProtectedDevicesCount = _cachedProtectedCount;
                _latestMetrics.AppMixerGuardEnabled = _config.Settings.AppMixerGuardEnabled;
                _latestMetrics.AppFilteringEnabled = _config.Settings.AppFilteringEnabled;
                _latestMetrics.Custom1PresetPercent = _config.Settings.Custom1PresetPercent;
                _latestMetrics.Custom2PresetPercent = _config.Settings.Custom2PresetPercent;
                _latestMetrics.SfxActiveCount = hasApoTelemetry ? (int)telem.SfxActiveCount : 0;
                _latestMetrics.AppProtectionRules = _config.Settings.AppProtectionRules;
                _latestMetrics.AppSessions = _cachedAppSessions;
            }

            OnMetricsUpdated?.Invoke(_latestMetrics);
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
            ReleaseCachedDeviceInterfaces();
            if (_channelPeaksNativeBuffer != IntPtr.Zero)
            {
                try { Marshal.FreeHGlobal(_channelPeaksNativeBuffer); } catch { }
                _channelPeaksNativeBuffer = IntPtr.Zero;
            }

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
                try { _activeDevice.AudioEndpointVolume.OnVolumeNotification -= _volumeDelegate; } catch { }
            }
            try { _activeDevice?.Dispose(); } catch { }
            _activeDevice = null;
            _enumerator?.Dispose();
            _virtualBridge.Dispose();
            _policyConfigClient.Dispose();
        }
        catch { }
    }

    private void CheckAndTrimMemory()
    {
        var now = DateTime.UtcNow;
        bool startupTrim = !_initialTrimDone && (now - _startupUtc).TotalSeconds >= 8.0;
        bool requestedTrim = _trimRequested && (now - _lastMemoryTrimTime).TotalSeconds >= 2.0;
        bool periodicDue = (now - _lastMemoryTrimTime).TotalSeconds >= 60.0;

        if (!startupTrim && !requestedTrim && !periodicDue) return;

        long wsBytes = Environment.WorkingSet;
        if (!startupTrim && !requestedTrim && wsBytes < 20L * 1024 * 1024)
        {
            _lastMemoryTrimTime = now;
            return;
        }

        _initialTrimDone = true;
        _trimRequested = false;
        _lastMemoryTrimTime = now;

        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            IntPtr cur = GetCurrentProcess();
            bool ok1 = EmptyWorkingSet(cur);
            bool ok2 = SetProcessWorkingSetSize(cur, (IntPtr)(-1), (IntPtr)(-1));
            if (AppLogger.VerboseEnabled)
            {
                long wsMb = Environment.WorkingSet / 1024 / 1024;
                AppLogger.Log($"[MEMORY_TRIM] EmptyWorkingSet={ok1}, SetProcessWorkingSetSize={ok2}, WS={wsMb}MB");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[MEMORY_TRIM_ERR] {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _actionQueue.CompleteAdding();
        _wakeSignal.Set();
        _audioThread.Join(1000);
        _wakeSignal.Dispose();
        CleanupCom();
    }
}
