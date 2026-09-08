using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PermadB.Config;

public class ConfigManager
{
    private readonly string _configFilePath;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AppSettings Settings { get; private set; }

    public event Action<AppSettings>? OnSettingsChanged;

    public ConfigManager(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath))
        {
            _configFilePath = customPath;
        }
        else
        {
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PermadB"
            );
            Directory.CreateDirectory(appDataDir);
            _configFilePath = Path.Combine(appDataDir, "config.json");
        }

        Settings = Load();
    }

    public AppSettings Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    var json = File.ReadAllText(_configFilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (settings != null)
                    {
                        // Ensure intrusive flags from older builds are cleanly reset
                        settings.AppMixerGuardEnabled = false;
                        settings.ProtectMicrophoneInputs = false;
                        if (settings.AppVolumeOverrides.Count > 0)
                        {
                            settings.AppVolumeOverrides.Clear();
                        }
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Failed to load config from {_configFilePath}: {ex.Message}");
            }

            var defaultSettings = new AppSettings();
            Save(defaultSettings);
            return defaultSettings;
        }
    }

    public void Save(AppSettings? settingsToSave = null)
    {
        lock (_lock)
        {
            try
            {
                if (settingsToSave != null)
                {
                    Settings = settingsToSave;
                }

                var dir = Path.GetDirectoryName(_configFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var json = JsonSerializer.Serialize(Settings, JsonOptions);
                File.WriteAllText(_configFilePath, json);
                OnSettingsChanged?.Invoke(Settings);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Failed to save config to {_configFilePath}: {ex.Message}");
            }
        }
    }

    public DeviceProfile GetOrCreateProfile(string deviceId, string deviceName)
    {
        lock (_lock)
        {
            if (Settings.DeviceProfiles.TryGetValue(deviceId, out var existing))
            {
                existing.DeviceName = deviceName;
                existing.LastSeen = DateTime.UtcNow;
                return existing;
            }

            // Create new profile based on device name heuristics
            var lowerName = deviceName.ToLowerInvariant();
            var isHeadphones = lowerName.Contains("headphone") ||
                               lowerName.Contains("headset") ||
                               lowerName.Contains("earphone") ||
                               lowerName.Contains("earbuds") ||
                               lowerName.Contains("airpod") ||
                               lowerName.Contains("cloud") ||
                               lowerName.Contains("iem");

            var profile = new DeviceProfile
            {
                DeviceId = deviceId,
                DeviceName = deviceName,
                DeviceType = isHeadphones ? "headphones" : "speakers",
                SafeCeilingPercent = isHeadphones ? 65.0f : 75.0f,
                TargetSafeDbSpl = 75.0f,
                EstimatedMaxDbSpl = isHeadphones ? 102.0f : 95.0f,
                IsCalibrated = false,
                LastSeen = DateTime.UtcNow
            };

            Settings.DeviceProfiles[deviceId] = profile;
            Save();
            return profile;
        }
    }
}
