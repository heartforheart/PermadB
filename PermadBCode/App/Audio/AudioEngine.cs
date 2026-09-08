using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using PermadB.Config;

namespace PermadB.Audio;

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
    private bool _isAdjustingVolume = false;

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
            if (_activeDevice != null)
            {
                var targetScalar = profile.SafeCeilingPercent / 100.0f;
                _isAdjustingVolume = true;
                try
                {
                    _activeDevice.AudioEndpointVolume.MasterVolumeLevelScalar = targetScalar;
                    _userBaselineVolume = targetScalar;
                    Console.WriteLine($"[AudioEngine] Custom ceiling applied: {percent:F0}%. Windows volume set to {percent:F0}%.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AudioEngine] Error setting custom ceiling volume: {ex.Message}");
                }
                finally
                {
                    _isAdjustingVolume = false;
                }
            }

            InternalEnforceCeiling();
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
            if (_activeDevice != null)
            {
                var targetScalar = profile.SafeCeilingPercent / 100.0f;
                _isAdjustingVolume = true;
                try
                {
                    _activeDevice.AudioEndpointVolume.MasterVolumeLevelScalar = targetScalar;
                    _userBaselineVolume = targetScalar;
                    Console.WriteLine($"[AudioEngine] Preset '{preset}' applied: Ceiling set to {profile.SafeCeilingPercent:F0}% ({profile.TargetSafeDbSpl:F0} dBA). Windows volume set to {profile.SafeCeilingPercent:F0}%.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AudioEngine] Error applying preset volume: {ex.Message}");
                }
                finally
                {
                    _isAdjustingVolume = false;
                }
            }

            InternalEnforceCeiling();
            InternalPollMeter();
        });
    }

    public void SetMasterVolume(float percent)
    {
        PostToAudioThread(() =>
        {
            if (_activeDevice == null) return;
            _isAdjustingVolume = true;
            try
            {
                var target = Math.Clamp(percent / 100.0f, 0.0f, 1.0f);
                _activeDevice.AudioEndpointVolume.MasterVolumeLevelScalar = target;
                _userBaselineVolume = target;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioEngine] SetMasterVolume error: {ex.Message}");
            }
            finally
            {
                _isAdjustingVolume = false;
            }
        });
    }

    public void SetAppVolume(string processName, float volumePercent)
    {
        PostToAudioThread(() =>
        {
            if (_activeDevice == null) return;
            try
            {
                var targetScalar = Math.Clamp(volumePercent / 100.0f, 0.0f, 1.0f);
                _config.Settings.AppVolumeOverrides[processName] = volumePercent;
                _config.Save();

                var sessionManager = _activeDevice.AudioSessionManager;
                sessionManager.RefreshSessions();
                var sessions = sessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    var pid = (int)s.GetProcessID;
                    string name = string.Empty;
                    if (pid > 0 && _processNameCache.TryGetValue(pid, out var cachedName))
                    {
                        name = cachedName;
                    }
                    if (string.Equals(name, processName, StringComparison.OrdinalIgnoreCase))
                    {
                        s.SimpleAudioVolume.Volume = targetScalar;
                        Console.WriteLine($"[AudioEngine] Set Sound Mixer volume for '{processName}' to {volumePercent:F0}%");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioEngine] Error setting app volume for {processName}: {ex.Message}");
            }
        });
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

                _volumeDelegate = new AudioEndpointVolumeNotificationDelegate(OnVolumeNotificationReceived);
                _activeDevice.AudioEndpointVolume.OnVolumeNotification += _volumeDelegate;

                Console.WriteLine($"[AudioEngine] Active Device: {friendlyName} ({profile.DeviceType})");
                UpdateCachedProfile(profile);
                InternalEnforceCeiling();

                // Restore any apps (e.g. System Sounds, NVIDIA Container) or mics ducked by older builds back to 100%
                RestoreClampedAppVolumes();
                RestoreMicrophoneVolumes();
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
        if (_isAdjustingVolume || !_config.Settings.Enabled) return;

        var profile = GetActiveProfile();
        var safeCap = Math.Clamp(profile.SafeCeilingPercent, 5.0f, 100.0f) / 100.0f;

        // If user or an app attempts to turn volume above the safe ceiling, clamp it
        if (data.MasterVolume > safeCap + 0.005f)
        {
            PostToAudioThread(InternalEnforceCeiling);
        }
        else
        {
            _userBaselineVolume = data.MasterVolume;
        }
    }

    private int _cachedProtectedCount = 1;

    private void InternalEnforceCeiling()
    {
        if (!_config.Settings.Enabled) return;

        int count = 0;
        if (_activeDevice != null)
        {
            EnforceDeviceCeiling(_activeDevice);
            count++;
        }

        if (_config.Settings.ApplyToAllDevices && _enumerator != null)
        {
            try
            {
                var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var ep in endpoints)
                {
                    if (_activeDevice != null && ep.ID == _activeDevice.ID) continue;
                    EnforceDeviceCeiling(ep);
                    count++;
                }
            }
            catch { }
        }

        _cachedProtectedCount = Math.Max(1, count);
    }

    private void EnforceDeviceCeiling(MMDevice device)
    {
        try
        {
            var profile = GetActiveProfile();
            var safeCapPercent = profile.SafeCeilingPercent;
            var safeCap = Math.Clamp(safeCapPercent, 5.0f, 100.0f) / 100.0f;
            var currentVol = device.AudioEndpointVolume.MasterVolumeLevelScalar;

            // Only clamp if volume strictly exceeds the safe ceiling.
            // Lower volumes are quieter and safer, so they are always allowed freely!
            if (currentVol > safeCap + 0.005f)
            {
                _isAdjustingVolume = true;
                try
                {
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = safeCap;
                    _userBaselineVolume = safeCap;
                    Console.WriteLine($"[AudioEngine] Safe ceiling enforced on '{device.FriendlyName}': clamped from {currentVol * 100:F0}% down to {safeCap * 100:F0}%.");
                }
                finally
                {
                    _isAdjustingVolume = false;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AudioEngine] Ceiling enforcement error on {device.FriendlyName}: {ex.Message}");
        }
    }

    private void RestoreClampedAppVolumes()
    {
        if (_enumerator == null) return;
        try
        {
            var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var ep in endpoints)
            {
                try
                {
                    var sessionManager = ep.AudioSessionManager;
                    sessionManager.RefreshSessions();
                    var sessions = sessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var s = sessions[i];
                        var pid = (int)s.GetProcessID;
                        string procName = "System Sounds";
                        if (pid > 0 && _processNameCache.TryGetValue(pid, out var cachedName))
                        {
                            procName = cachedName;
                        }
                        else if (pid > 0)
                        {
                            try { procName = Process.GetProcessById(pid).ProcessName; } catch { }
                        }

                        // If System Sounds, NVIDIA Container, or any app was previously ducked below 100%, restore it to 100%
                        if (s.SimpleAudioVolume.Volume < 0.99f)
                        {
                            s.SimpleAudioVolume.Volume = 1.0f;
                            Console.WriteLine($"[AudioEngine] Restored '{procName}' on '{ep.FriendlyName}' in Windows Sound Mixer back to 100%.");
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AudioEngine] RestoreClampedAppVolumes error: {ex.Message}");
        }
    }

    private void RestoreMicrophoneVolumes()
    {
        if (_enumerator == null) return;
        try
        {
            var captureEndpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var cap in captureEndpoints)
            {
                try
                {
                    // If mic was capped to ~80% (0.78f - 0.82f) by previous version, restore it to 100% (1.0f)
                    if (cap.AudioEndpointVolume.MasterVolumeLevelScalar >= 0.78f && cap.AudioEndpointVolume.MasterVolumeLevelScalar <= 0.82f)
                    {
                        cap.AudioEndpointVolume.MasterVolumeLevelScalar = 1.0f;
                        Console.WriteLine($"[AudioEngine] Restored microphone '{cap.FriendlyName}' volume back to 100%.");
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private void InternalPollMeter()
    {
        if (_activeDevice == null) return;

        try
        {
            var meter = _activeDevice.AudioMeterInformation;
            var endpointVol = _activeDevice.AudioEndpointVolume;
            var peak = meter.MasterPeakValue;
            var volScalar = endpointVol.MasterVolumeLevelScalar;
            var profile = GetActiveProfile();

            // Watchdog protection: If volume was pushed above safe ceiling (e.g. by external app/shortcut), clamp it back
            if (_config.Settings.Enabled && !_isAdjustingVolume)
            {
                var safeCap = Math.Clamp(profile.SafeCeilingPercent, 5.0f, 100.0f) / 100.0f;
                if (volScalar > safeCap + 0.005f)
                {
                    InternalEnforceCeiling();
                    volScalar = endpointVol.MasterVolumeLevelScalar;
                }
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

            var peakDbfs = peak > 0.00001f ? 20.0f * (float)Math.Log10(peak) : -96.0f;

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
                        if (_config.Settings.AppVolumeOverrides.TryGetValue(procName, out var userCapPercent))
                        {
                            var userCapScalar = Math.Clamp(userCapPercent / 100.0f, 0.0f, 1.0f);
                            if (appVol > userCapScalar + 0.02f)
                            {
                                s.SimpleAudioVolume.Volume = userCapScalar;
                                appVol = userCapScalar;
                                isAppClamped = true;
                            }
                        }
                        else if (string.Equals(procName, "System Sounds", StringComparison.OrdinalIgnoreCase) ||
                                 procName.Contains("nvcontainer", StringComparison.OrdinalIgnoreCase) ||
                                 procName.Contains("nvidia", StringComparison.OrdinalIgnoreCase))
                        {
                            if (appVol < 0.99f)
                            {
                                s.SimpleAudioVolume.Volume = 1.0f;
                                appVol = 1.0f;
                            }
                        }

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
                IsClamping = false,
                SpikesClampedTotal = 0,
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
