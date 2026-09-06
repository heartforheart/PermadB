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

    // Transient Limiter state
    private DateTime _lastDuckTime = DateTime.MinValue;
    private long _spikesClampedCount = 0;
    private bool _isCurrentlyClamping = false;
    private float _preDuckMasterVolume = -1.0f;

    // Process name cache for per-app Windows Sound Mixer sessions
    private readonly ConcurrentDictionary<int, string> _processNameCache = new();

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
        PostToAudioThread(() =>
        {
            var profile = GetActiveProfile();
            profile.SafeCeilingPercent = Math.Clamp(percent, 5.0f, 100.0f);
            _config.Save();
            InternalEnforceCeiling();
            UpdateCachedProfile(profile);
        });
    }

    public void ApplyPreset(string preset)
    {
        PostToAudioThread(() =>
        {
            var profile = GetActiveProfile();
            float targetVolume;

            switch (preset.ToLowerInvariant())
            {
                case "night":
                    profile.SafeCeilingPercent = profile.DeviceType == "headphones" ? 40.0f : 45.0f;
                    profile.TargetSafeDbSpl = 60.0f;
                    _config.Settings.DynamicThresholdDbfs = -6.0f;
                    _config.Settings.ActivePreset = "night";
                    targetVolume = profile.SafeCeilingPercent;
                    break;
                case "studio":
                    profile.SafeCeilingPercent = profile.DeviceType == "headphones" ? 80.0f : 85.0f;
                    profile.TargetSafeDbSpl = 85.0f;
                    _config.Settings.DynamicThresholdDbfs = -1.5f;
                    _config.Settings.ActivePreset = "studio";
                    targetVolume = profile.SafeCeilingPercent;
                    break;
                case "safe":
                default:
                    profile.SafeCeilingPercent = profile.DeviceType == "headphones" ? 65.0f : 75.0f;
                    profile.TargetSafeDbSpl = 75.0f;
                    _config.Settings.DynamicThresholdDbfs = -3.0f;
                    _config.Settings.ActivePreset = "safe";
                    targetVolume = profile.SafeCeilingPercent;
                    break;
            }

            _config.Settings.DynamicLimiterEnabled = true;
            _config.Save();

            // Only clamp the master output volume if it currently exceeds the preset's safe ceiling.
            // If the user was already listening at a quieter level, preserve it (lower = safer)!
            if (_activeDevice != null)
            {
                var currentVol = _activeDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100.0f;
                if (currentVol > targetVolume)
                {
                    _isAdjustingVolume = true;
                    try
                    {
                        _activeDevice.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(targetVolume / 100.0f, 0.0f, 1.0f);
                        Console.WriteLine($"[AudioEngine] Preset '{preset}' applied: Clamped output volume from {currentVol:F1}% down to ceiling {targetVolume:F1}%");
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
                else
                {
                    Console.WriteLine($"[AudioEngine] Preset '{preset}' applied: Ceiling set to {targetVolume:F1}%. Current volume ({currentVol:F1}%) preserved.");
                }
            }

            UpdateCachedProfile(profile);
            InternalPollMeter();
        });
    }

    public void SetMasterVolume(float percent)
    {
        PostToAudioThread(() =>
        {
            if (_activeDevice == null) return;
            var profile = GetActiveProfile();

            var target = percent;
            if (_config.Settings.Enabled && target > profile.SafeCeilingPercent)
            {
                target = profile.SafeCeilingPercent;
            }

            _isAdjustingVolume = true;
            try
            {
                _activeDevice.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(target / 100.0f, 0.0f, 1.0f);
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

                Thread.Sleep(20); // 50 Hz
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
        var currentPercent = data.MasterVolume * 100.0f;

        // Allow any volume at or below the safe ceiling (lower is quieter and safer).
        // Intercept and clamp only if volume tries to exceed the safe ceiling!
        bool needsRestore = _config.Settings.StrictVolumeLock
            ? currentPercent > profile.SafeCeilingPercent + 0.1f
            : currentPercent > profile.SafeCeilingPercent + 0.5f;

        if (needsRestore)
        {
            InternalEnforceCeiling();
        }
    }

    private int _cachedProtectedCount = 1;

    private void InternalEnforceCeiling()
    {
        if (!_config.Settings.Enabled) return;

        int count = 0;

        // 1. Enforce on Default Active Device
        if (_activeDevice != null)
        {
            EnforceDeviceCeiling(_activeDevice, isDefault: true);
            count++;
        }

        // 2. Multi-Device Enforcer: If ApplyToAllDevices is enabled, enforce on ALL active playback endpoints!
        if (_config.Settings.ApplyToAllDevices && _enumerator != null)
        {
            try
            {
                var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var ep in endpoints)
                {
                    if (_activeDevice != null && ep.ID == _activeDevice.ID) continue;
                    EnforceDeviceCeiling(ep, isDefault: false);
                    count++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioEngine] Error enforcing all-devices ceiling: {ex.Message}");
            }
        }

        // 3. Microphone Input Protection (Capture endpoints)
        if (_config.Settings.ProtectMicrophoneInputs && _enumerator != null)
        {
            try
            {
                var captureEndpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                var micCap = Math.Clamp(_config.Settings.MicCeilingPercent, 10.0f, 100.0f) / 100.0f;
                foreach (var cap in captureEndpoints)
                {
                    try
                    {
                        var vol = cap.AudioEndpointVolume;
                        if (vol.MasterVolumeLevelScalar > micCap)
                        {
                            vol.MasterVolumeLevelScalar = micCap;
                            Console.WriteLine($"[AudioEngine] Microphone input capped to {micCap * 100:F0}% on {cap.FriendlyName}");
                        }
                        count++;
                    }
                    catch { }
                }
            }
            catch { }
        }

        _cachedProtectedCount = Math.Max(1, count);
    }

    private void EnforceDeviceCeiling(MMDevice device, bool isDefault)
    {
        try
        {
            var profile = isDefault ? GetActiveProfile() : _config.GetOrCreateProfile(device.ID, device.FriendlyName);
            var safeCapPercent = _config.Settings.ApplyToAllDevices 
                ? GetActiveProfile().SafeCeilingPercent 
                : profile.SafeCeilingPercent;

            var safeCap = Math.Clamp(safeCapPercent, 5.0f, 100.0f);
            var currentVol = device.AudioEndpointVolume.MasterVolumeLevelScalar * 100.0f;

            // Only correct if volume exceeds the safe ceiling.
            // Lower volumes are quieter and safer, so they are always allowed freely!
            bool needsCorrection = _config.Settings.StrictVolumeLock
                ? currentVol > safeCap + 0.1f
                : currentVol > safeCap + 0.5f;

            if (needsCorrection)
            {
                if (isDefault) _isAdjustingVolume = true;
                try
                {
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = safeCap / 100.0f;
                    Console.WriteLine($"[AudioEngine] Volume ceiling enforced on '{device.FriendlyName}': clamped from {currentVol:F1}% down to safe ceiling {safeCap:F1}%");
                }
                finally
                {
                    if (isDefault) _isAdjustingVolume = false;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AudioEngine] Ceiling enforcement error on {device.FriendlyName}: {ex.Message}");
        }
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

            // Proactive continuous ceiling enforcement (Double safety net at 50Hz)
            if (_config.Settings.Enabled)
            {
                var currentVolPercent = volScalar * 100.0f;
                bool needsCheck = _config.Settings.StrictVolumeLock
                    ? currentVolPercent > profile.SafeCeilingPercent + 0.05f
                    : currentVolPercent > profile.SafeCeilingPercent + 0.2f;

                if (needsCheck)
                {
                    InternalEnforceCeiling();
                    volScalar = endpointVol.MasterVolumeLevelScalar;
                }
            }

            var peakDbfs = peak > 0.00001f ? 20.0f * (float)Math.Log10(peak) : -96.0f;

            var volAttenDb = volScalar > 0.001f ? 20.0f * (float)Math.Log10(volScalar) : -60.0f;
            var estimatedSpl = profile.EstimatedMaxDbSpl + peakDbfs + volAttenDb;
            estimatedSpl = Math.Clamp(estimatedSpl, 25.0f, 120.0f);

            var channelCount = meter.PeakValues.Count;
            var channelPeaks = new float[channelCount];
            for (int i = 0; i < channelCount; i++)
            {
                channelPeaks[i] = meter.PeakValues[i];
            }

            // Dynamic Transient Limiter (Ear Shield): Real-time micro-ducking on uncompressed spikes
            if (_config.Settings.Enabled && _config.Settings.DynamicLimiterEnabled)
            {
                var threshold = _config.Settings.DynamicThresholdDbfs;
                var safeSpl = profile.TargetSafeDbSpl;

                // An audio burst is dangerous if peak dBFS hits threshold OR estimated SPL exceeds target limit
                bool isUnsafeSpike = peakDbfs > threshold || estimatedSpl > safeSpl + 1.0f;

                if (isUnsafeSpike)
                {
                    _isCurrentlyClamping = true;
                    _lastDuckTime = DateTime.UtcNow;
                    _spikesClampedCount++;

                    // Calculate overshoot in dB
                    var overshoot = Math.Max(peakDbfs - threshold, estimatedSpl - safeSpl);
                    if (overshoot > 0.5f && !_isAdjustingVolume)
                    {
                        if (_preDuckMasterVolume < 0)
                        {
                            _preDuckMasterVolume = volScalar;
                        }

                        // Duck volume scalar smoothly based on overshoot (max 15 dB cut)
                        var duckDb = Math.Clamp(overshoot, 2.0f, 15.0f);
                        var duckFactor = (float)Math.Pow(10, -duckDb / 20.0);
                        var targetDuckScalar = Math.Clamp(volScalar * duckFactor, 0.05f, profile.SafeCeilingPercent / 100.0f);

                        if (targetDuckScalar < endpointVol.MasterVolumeLevelScalar)
                        {
                            _isAdjustingVolume = true;
                            try
                            {
                                endpointVol.MasterVolumeLevelScalar = targetDuckScalar;
                                volScalar = targetDuckScalar;
                                Console.WriteLine($"[AudioEngine] 🛡️ Dynamic Ear Shield micro-ducked volume by {duckDb:F1} dB (Target: {targetDuckScalar * 100:F0}%)");
                            }
                            finally
                            {
                                _isAdjustingVolume = false;
                            }
                        }
                    }
                    OnPeakClamped?.Invoke(peakDbfs);
                }
                else if (_isCurrentlyClamping && (DateTime.UtcNow - _lastDuckTime).TotalMilliseconds > 350)
                {
                    _isCurrentlyClamping = false;
                    // Smooth recovery to pre-duck volume level
                    if (_preDuckMasterVolume > 0 && !_isAdjustingVolume)
                    {
                        var targetRecover = Math.Min(_preDuckMasterVolume, profile.SafeCeilingPercent / 100.0f);
                        if (endpointVol.MasterVolumeLevelScalar < targetRecover)
                        {
                            _isAdjustingVolume = true;
                            try
                            {
                                endpointVol.MasterVolumeLevelScalar = targetRecover;
                                volScalar = targetRecover;
                            }
                            finally
                            {
                                _isAdjustingVolume = false;
                            }
                        }
                        _preDuckMasterVolume = -1.0f;
                    }
                }
            }

            // Per-Application Sound Mixer Guard: Inspect & level games and applications in Windows Volume Mixer
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
                    var appPeak = appMeter.MasterPeakValue;
                    var appMute = s.SimpleAudioVolume.Mute;
                    var appPeakDbfs = appPeak > 0.00001f ? 20.0f * (float)Math.Log10(appPeak) : -96.0f;

                    // Estimated real-world acoustic SPL produced by this specific app
                    var appEstSpl = profile.EstimatedMaxDbSpl + appPeakDbfs + volAttenDb;
                    appEstSpl = Math.Clamp(appEstSpl, 25.0f, 120.0f);

                    bool isAppClamped = false;
                    bool isAppUnsafe = false;

                    // 1. Check user-defined manual cap for this application
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

                    // 2. Active App Sound Mixer Guard: Automatically clamp applications outputting in unsafe dB range
                    if (_config.Settings.Enabled && _config.Settings.AppMixerGuardEnabled)
                    {
                        var safeTarget = profile.TargetSafeDbSpl;
                        if (appEstSpl > safeTarget + 1.0f)
                        {
                            isAppUnsafe = true;
                            var overshootDb = appEstSpl - safeTarget;
                            var attenFactor = (float)Math.Pow(10, -Math.Min(overshootDb, 18.0f) / 20.0);
                            var safeAppVol = Math.Clamp(appVol * attenFactor, 0.05f, 1.0f);

                            if (safeAppVol < appVol - 0.02f)
                            {
                                s.SimpleAudioVolume.Volume = safeAppVol;
                                appVol = safeAppVol;
                                isAppClamped = true;
                                _spikesClampedCount++;
                                Console.WriteLine($"[AudioEngine] 🛡️ Sound Mixer Guard clamped '{procName}' ({appEstSpl:F1} dBA) down to {safeAppVol * 100:F0}%");
                            }
                        }
                    }

                    appSessionsList.Add(new AppSessionInfo
                    {
                        ProcessId = pid,
                        ProcessName = procName,
                        DisplayName = string.IsNullOrWhiteSpace(s.DisplayName) ? procName : s.DisplayName,
                        VolumePercent = (float)Math.Round(appVol * 100.0f, 1),
                        PeakValue = (float)Math.Round(appPeak, 3),
                        PeakDbfs = (float)Math.Round(appPeakDbfs, 1),
                        EstimatedDbSpl = (float)Math.Round(appEstSpl, 1),
                        IsMuted = appMute,
                        IsClamped = isAppClamped,
                        IsUnsafe = isAppUnsafe
                    });
                }
            }
            catch { }

            var m = new AudioMetrics
            {
                MasterPeak = peak,
                PeakDbfs = (float)Math.Round(peakDbfs, 1),
                EstimatedDbSpl = (float)Math.Round(estimatedSpl, 1),
                CurrentVolumePercent = (float)Math.Round(volScalar * 100.0f, 1),
                SafeCeilingPercent = profile.SafeCeilingPercent,
                IsClamping = _isCurrentlyClamping,
                SpikesClampedTotal = _spikesClampedCount,
                ChannelPeaks = channelPeaks,
                ActiveDeviceId = profile.DeviceId,
                ActiveDeviceName = profile.DeviceName,
                ActiveDeviceType = profile.DeviceType,
                Enabled = _config.Settings.Enabled,
                ApplyToAllDevices = _config.Settings.ApplyToAllDevices,
                ProtectMicrophoneInputs = _config.Settings.ProtectMicrophoneInputs,
                ProtectedDevicesCount = _cachedProtectedCount,
                AppMixerGuardEnabled = _config.Settings.AppMixerGuardEnabled,
                AppSessions = appSessionsList
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
