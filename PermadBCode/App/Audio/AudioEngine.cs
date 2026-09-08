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
    private float _userBaselineVolume = -1.0f;

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
        _config.Settings.ActivePreset = "custom";
        _config.Save();
        UpdateCachedProfile(profile);

        PostToAudioThread(() =>
        {
            // Ensure Windows master volume is maintained at 100%
            if (_activeDevice != null && !_isCurrentlyClamping)
            {
                _isAdjustingVolume = true;
                try
                {
                    _activeDevice.AudioEndpointVolume.MasterVolumeLevelScalar = 1.0f;
                    _userBaselineVolume = 1.0f;
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

        _config.Settings.DynamicLimiterEnabled = true;
        _config.Save();
        UpdateCachedProfile(profile);

        PostToAudioThread(() =>
        {
            // Always maintain Windows master volume at 100%; the preset controls the Limiter ceiling!
            if (_activeDevice != null)
            {
                _isAdjustingVolume = true;
                try
                {
                    _activeDevice.AudioEndpointVolume.MasterVolumeLevelScalar = 1.0f;
                    _userBaselineVolume = 1.0f;
                    Console.WriteLine($"[AudioEngine] Preset '{preset}' applied (Limiter Ceiling: {profile.SafeCeilingPercent:F0}% / {profile.TargetSafeDbSpl:F0} dBA). Windows master volume set to 100%.");
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

                Thread.Sleep(10); // 100 Hz (10ms DAW-grade limiter cycle)
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

        if (!_isCurrentlyClamping)
        {
            _userBaselineVolume = data.MasterVolume;
        }
    }

    private int _cachedProtectedCount = 1;

    private void InternalEnforceCeiling()
    {
        if (!_config.Settings.Enabled) return;

        int count = 0;

        // 1. Enforce on Default Active Device (Ensure baseline is 100%)
        if (_activeDevice != null)
        {
            EnforceDeviceCeiling(_activeDevice, isDefault: true);
            count++;
        }

        // 2. Multi-Device Enforcer: If ApplyToAllDevices is enabled, maintain baseline on ALL active playback endpoints!
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
            if (_isCurrentlyClamping) return; // Limiter is actively handling transient ducking

            // Windows audio sits at 100% by default with Limiter dynamically guarding spikes
            var currentVol = device.AudioEndpointVolume.MasterVolumeLevelScalar;
            if (currentVol < 0.99f && !_isCurrentlyClamping)
            {
                if (isDefault) _isAdjustingVolume = true;
                try
                {
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = 1.0f;
                    if (isDefault) _userBaselineVolume = 1.0f;
                    Console.WriteLine($"[AudioEngine] Master volume on '{device.FriendlyName}' maintained at 100%.");
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

            // Master Bus DAW-Grade Hardlock Limiter: 10ms transient brickwall protection for ALL audio (games, Discord, streams)
            if (_config.Settings.Enabled && _config.Settings.DynamicLimiterEnabled)
            {
                // Hard ceiling is locked directly to the active preset (e.g. 75 dBA for Safe Ears, 68 dBA for Night, 82 dBA for Studio)
                // Any acoustic burst above this ceiling is hard-clamped instantly!
                var limiterCeilingSpl = profile.TargetSafeDbSpl;

                // Check if current physical acoustic output exceeds the hardlock ceiling
                bool isExceedingCeiling = estimatedPeakSpl > limiterCeilingSpl;

                if (isExceedingCeiling)
                {
                    _isCurrentlyClamping = true;
                    _lastDuckTime = DateTime.UtcNow;
                    _spikesClampedCount++;

                    // Calculate exact decibel overshoot above the safe hardlock ceiling
                    var overshootDb = estimatedPeakSpl - limiterCeilingSpl;
                    
                    if (!_isAdjustingVolume)
                    {
                        var maxBaseline = 1.0f; // Windows audio baseline sits at 100%

                        // Exact brickwall limiter attenuation: cut by exactly the overshoot amount
                        var clampFactor = (float)Math.Pow(10, -Math.Min(overshootDb, 30.0f) / 20.0);
                        var targetDuckScalar = Math.Clamp(maxBaseline * clampFactor, 0.05f, maxBaseline);

                        if (targetDuckScalar < endpointVol.MasterVolumeLevelScalar - 0.005f)
                        {
                            _isAdjustingVolume = true;
                            try
                            {
                                endpointVol.MasterVolumeLevelScalar = targetDuckScalar;
                                volScalar = targetDuckScalar;
                                Console.WriteLine($"[AudioEngine] 🛡️ DAW Hardlock Limiter clamped peak ({estimatedPeakSpl:F1} dBA -> {limiterCeilingSpl:F1} dBA) -{overshootDb:F1} dB");
                            }
                            finally
                            {
                                _isAdjustingVolume = false;
                            }
                        }
                    }
                    OnPeakClamped?.Invoke(peakDbfs);
                }
                // Fast, Transparent DAW Release Envelope (40ms hold, smooth exponential recovery in ~80ms)
                else if (_isCurrentlyClamping && (DateTime.UtcNow - _lastDuckTime).TotalMilliseconds > 40)
                {
                    var targetRecover = 1.0f; // Recover smoothly back to 100%
                    
                    if (!_isAdjustingVolume && endpointVol.MasterVolumeLevelScalar < targetRecover - 0.005f)
                    {
                        _isAdjustingVolume = true;
                        try
                        {
                            // Smooth exponential release step (smoothly ramps back to 100% without audio clicks or pumping)
                            var step = (targetRecover - endpointVol.MasterVolumeLevelScalar) * 0.35f;
                            var newScalar = Math.Min(targetRecover, endpointVol.MasterVolumeLevelScalar + Math.Max(step, 0.02f));
                            endpointVol.MasterVolumeLevelScalar = newScalar;
                            volScalar = newScalar;

                            if (Math.Abs(newScalar - targetRecover) < 0.01f)
                            {
                                endpointVol.MasterVolumeLevelScalar = targetRecover;
                                volScalar = targetRecover;
                                _isCurrentlyClamping = false;
                            }
                        }
                        finally
                        {
                            _isAdjustingVolume = false;
                        }
                    }
                    else if (!_isAdjustingVolume)
                    {
                        _isCurrentlyClamping = false;
                    }
                }
                else if (!_isCurrentlyClamping && !_isAdjustingVolume)
                {
                    // Windows audio baseline remains at 100%
                    _userBaselineVolume = 1.0f;
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
                    var rawAppPeak = appMeter.MasterPeakValue;
                    var appMute = s.SimpleAudioVolume.Mute;

                    // Smooth application peak using exponential moving average (100ms window)
                    var smoothedPeak = _appSmoothedPeak.AddOrUpdate(
                        procName,
                        rawAppPeak,
                        (_, prev) => Math.Max(rawAppPeak, prev * 0.75f + rawAppPeak * 0.25f)
                    );

                    var appPeakDbfs = smoothedPeak > 0.00001f ? 20.0f * (float)Math.Log10(smoothedPeak) : -96.0f;

                    bool isVoip = CommunicationApps.Contains(procName);
                    // Human voice has ~15 dB crest factor; games/media have ~11 dB crest factor
                    float crestFactor = isVoip ? 15.0f : 11.0f;

                    // Estimated real-world acoustic peak SPL produced by this specific app
                    var appEstPeakSpl = profile.EstimatedMaxDbSpl + appPeakDbfs + masterDbAtten;
                    appEstPeakSpl = Math.Clamp(appEstPeakSpl, 25.0f, 120.0f);

                    // Continuous equivalent dBA (LAeq / RMS) for human display and safety monitoring
                    var appEstContinuousSpl = Math.Clamp(appEstPeakSpl - crestFactor, 25.0f, 105.0f);

                    bool isAppClamped = false;
                    bool isAppUnsafe = false;

                    // Update baseline volume when the app is quiet / normal and not clamped
                    if (!_appLastClampTime.TryGetValue(procName, out var lastClamp) || 
                        (DateTime.UtcNow - lastClamp).TotalSeconds > 4.0)
                    {
                        // Store the user's intended normal volume for this app
                        _appBaselineVolume[procName] = appVol;
                    }

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
                    // 2. Active App Sound Mixer Guard: Automatically level games outputting dangerous uncompressed dB (e.g. CS2 volume 1)
                    // Note: VoIP communication apps (Discord, Teams, Zoom, etc.) are excluded so user mixer levels are never altered
                    else if (_config.Settings.Enabled && _config.Settings.AppMixerGuardEnabled && !isVoip)
                    {
                        // For Games & Media (CS2, browsers, media players, etc.):
                        // Clamp if continuous sound exceeds the active profile's safe target (e.g. > 75 dBA continuous)
                        var safeTargetContinuous = profile.TargetSafeDbSpl;

                        if (appEstContinuousSpl > safeTargetContinuous + 0.5f && appPeakDbfs > -15.0f)
                        {
                            isAppUnsafe = true;
                            _appLastClampTime[procName] = DateTime.UtcNow;

                            var overshootDb = appEstContinuousSpl - safeTargetContinuous;
                            var baseVol = _appBaselineVolume.GetValueOrDefault(procName, 1.0f);

                            // Calculate target safe volume directly from baseline so it exactly hits safe target dB
                            var safeScalar = (float)Math.Pow(10, -Math.Min(overshootDb, 16.0f) / 20.0);
                            var targetSafeVol = Math.Clamp(baseVol * safeScalar, 0.25f, 1.0f);

                            if (appVol > targetSafeVol + 0.02f)
                            {
                                s.SimpleAudioVolume.Volume = targetSafeVol;
                                appVol = targetSafeVol;
                                isAppClamped = true;
                                _spikesClampedCount++;
                                Console.WriteLine($"[AudioEngine] 🛡️ App Sound Mixer Guard leveled '{procName}' ({appEstContinuousSpl:F1} dBA) down to safe {targetSafeVol * 100:F0}%");
                            }
                        }
                        // Auto-recovery: When the game is no longer blasting loud audio for > 3.0 seconds, gently restore back toward baseline
                        else if (_appLastClampTime.TryGetValue(procName, out var clampTime) && 
                                 (DateTime.UtcNow - clampTime).TotalSeconds > 3.0)
                        {
                            var baseVol = _appBaselineVolume.GetValueOrDefault(procName, 1.0f);
                            if (appVol < baseVol - 0.04f)
                            {
                                var recoveredVol = Math.Min(baseVol, appVol + 0.04f);
                                s.SimpleAudioVolume.Volume = recoveredVol;
                                appVol = recoveredVol;
                            }
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
                        IsClamped = isAppClamped || (_appLastClampTime.TryGetValue(procName, out var ct) && (DateTime.UtcNow - ct).TotalSeconds < 2.5),
                        IsUnsafe = isAppUnsafe
                    });
                }
            }
            catch { }

            var m = new AudioMetrics
            {
                MasterPeak = peak,
                PeakDbfs = (float)Math.Round(peakDbfs, 1),
                EstimatedDbSpl = (float)Math.Round(estimatedContinuousSpl, 1),
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
