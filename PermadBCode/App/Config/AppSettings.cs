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
    
    // User-saved custom presets (10 to 100)
    // Custom 1 (Lowest / 10%) & Custom 2 (Highest / 100%)
    public float Custom1PresetPercent { get; set; } = 10.0f;
    public float Custom2PresetPercent { get; set; } = 100.0f;

    // Backward-compatibility alias for single CustomPresetPercent
    public float CustomPresetPercent
    {
        get => Custom1PresetPercent;
        set => Custom1PresetPercent = value;
    }
    
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

    // Application-Specific Limiting Mode:
    // If true: only explicitly protected apps (e.g. Discord) are clamped by the WASAPI session limiter,
    // allowing bypassed apps (like Spotify, VLC, games) to blast at full volume without Master APO attenuation!
    // If false (default): Master Hardware Limiter protects all mixed audio globally.
    public bool AppFilteringEnabled { get; set; } = false;
    public Dictionary<string, bool> AppProtectionRules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    
    public Dictionary<string, DeviceProfile> DeviceProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
