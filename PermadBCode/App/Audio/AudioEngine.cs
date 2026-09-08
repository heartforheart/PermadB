using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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

    // Process name and baseline volume tracking for per-app Windows Sound Mixer sessions
    private readonly ConcurrentDictionary<int, string> _processNameCache = new();
    private readonly ConcurrentDictionary<string, float> _appBaselineVolume = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _appLastClampTime = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, float> _appSmoothedPeak = new(StringComparer.OrdinalIgnoreCase);

    // Voice communication applications where speech intelligibility must NEVER be crushed or muted
    private static readonly HashSet<string> CommunicationApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Discord", "DiscordCanary", "DiscordPTB", "DiscordDevelopment",
        "Slack", "Teams", "ms-teams", "Skype", "Zoom",
        "TeamSpeak", "ts3client_win64", "ts3client_win32",
        "Ventrilo", "Mumble", "Telegram", "WhatsApp", "Steam"
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
        _config.Settings.ActivePreset = "custom";
        _config.Save();
        UpdateCachedProfile(profile);

        PostToAudioThread(() =>
        {
            InternalEnforceCeiling();
            InternalPollMeter();
        });
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
        // Invariant: PermadB never alters Windows Volume Mixer state or per-application volume.
        Console.WriteLine($"[AudioEngine] SetAppVolume({processName}, {volumePercent:F0}%) ignored: Volume immutability invariant strictly enforced.");
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

                Thread.Sleep(20); // 50 Hz UI meter update loop
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

    public void SendCeilingToApo()
    {
        PostToAudioThread(InternalSendCeilingToApo);
    }

    private void InternalSendCeilingToApo()
    {
        try
        {
            var profile = GetActiveProfile();
            float ceilingPercent = profile.SafeCeilingPercent;
            bool isEnabled = _config.Settings.Enabled;

            // Convert user ceiling percentage to acoustic digital ceiling using human perceptual curve.
            // Human hearing is logarithmic: a 30% volume setting must attenuate significantly (~-27 dBFS)
            // so that audio peaks are genuinely quieted to comfortable listening levels.
            float norm = Math.Clamp(ceilingPercent / 100.0f, 0.05f, 1.0f);
            float linear = 0.8912509f * (float)Math.Pow(norm, 2.5);
            linear = Math.Clamp(linear, 0.001f, 0.8912509f);
            float dbfs = 20.0f * (float)Math.Log10(linear);
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

                    Console.WriteLine($"[AudioEngine] Dynamic ceiling sent to APO: {ceilingPercent:F0}% ({linear:F4} linear, {dbfs:F1} dBFS, enabled={isEnabled}, seq={_apoCommandSeq})");
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
                isClamping = telem.CurrentGainReductionDb > 0.05f;
                spikesClamped = (long)telem.LimiterActivations;

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
                spikesClamped = hasApoTelemetry ? (long)telem.LimiterActivations : 0;
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

            var channelCount = meter.PeakValues.Count;
            var channelPeaks = new float[channelCount];
            for (int i = 0; i < channelCount; i++)
            {
                channelPeaks[i] = meter.PeakValues[i];
            }

            // Active Hearing Safety Monitoring: Detect if acoustic output exceeds safe target for UI meter stats
            bool isExceedingCeiling = estimatedPeakSpl > profile.TargetSafeDbSpl;
            if (isExceedingCeiling)
            {
                OnPeakClamped?.Invoke(peakDbfs);
            }

            // Per-Application Sound Mixer: Only scan sessions periodically (every 2s) to keep CPU at near 0%
            if ((DateTime.UtcNow - _lastSessionScan).TotalSeconds > 2.0)
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
                        if (s.State == AudioSessionState.AudioSessionStateExpired) continue;

                        var pid = (int)s.GetProcessID;
                        string procName = "System Sounds";
                        if (pid > 0)
                        {
                            procName = _processNameCache.GetOrAdd(pid, p =>
                            {
                                try { return Process.GetProcessById(p).ProcessName; }
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

                        bool isAppClamped = false;

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
                            IsUnsafe = false
                        });
                    }
                    _cachedAppSessions = appSessionsList;
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
                AppMixerGuardEnabled = false,
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
