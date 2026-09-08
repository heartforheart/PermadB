using System;
using System.Collections.Generic;

namespace PermadB.Config;

public class DeviceProfile
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceType { get; set; } = "headphones"; // "headphones", "speakers", "digital", "other"
    
    // Maximum master volume percentage allowed (0 to 100)
    public float SafeCeilingPercent { get; set; } = 65.0f;
    
    // Target acoustic dB SPL (A-weighted sound pressure level)
    public float TargetSafeDbSpl { get; set; } = 75.0f;
    
    // Estimated real-world dB SPL at 100% volume on this hardware
    public float EstimatedMaxDbSpl { get; set; } = 100.0f;
    
    public bool IsCalibrated { get; set; } = false;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}

public class AppSettings
{
    public bool Enabled { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public string ActivePreset { get; set; } = "safe"; // "safe", "night", "studio", "custom"
    
    // Dynamic transient limiter (ducks sudden peaks like gunshots, ads, explosions)
    public bool DynamicLimiterEnabled { get; set; } = true;
    public float DynamicThresholdDbfs { get; set; } = -3.0f;
    
    // Strict Ceiling Guard: Actively prevents volume from exceeding safe ceiling; allows lowering to quieter levels freely
    public bool StrictVolumeLock { get; set; } = true;
    
    public bool NotificationsEnabled { get; set; } = true;
    public int ApiPort { get; set; } = 49220;
    
    // Multi-Device Protection: Default false (only active default audio device is managed)
    public bool ApplyToAllDevices { get; set; } = false;
    
    // Microphone Input Protection: Default false (PermadB protects hearing; never touches microphones)
    public bool ProtectMicrophoneInputs { get; set; } = false;
    public float MicCeilingPercent { get; set; } = 100.0f;
    
    // App Sound Mixer Guard: Default false (Master Limiter handles all audio; never alters per-app volume sliders)
    public bool AppMixerGuardEnabled { get; set; } = false;
    public Dictionary<string, float> AppVolumeOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    
    public Dictionary<string, DeviceProfile> DeviceProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
