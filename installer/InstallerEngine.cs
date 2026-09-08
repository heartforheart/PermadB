using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PermadB.Installer;

public static class InstallerEngine
{
    public static string GetDefaultInstallDir()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Path.Combine(programFiles, "PermadB");
    }

    public static void Install(string destinationDir, bool desktopShortcut, bool startMenuShortcut, bool startWithWindows, bool launchNow, Action<string, int>? progressCallback = null)
    {
        progressCallback?.Invoke("Stopping existing PermadB processes...", 10);
        KillExistingProcesses();

        progressCallback?.Invoke("Preparing installation directory...", 20);
        if (!Directory.Exists(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        // Reset user config on install / reinstall so it defaults cleanly to Safe Ears
        try
        {
            var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PermadB");
            var configPath = Path.Combine(appDataDir, "config.json");
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
        catch { }

        progressCallback?.Invoke("Extracting PermadB files...", 30);
        ExtractPayload(destinationDir);

        // Install PermadB Lookahead Brickwall Limiter APO into Windows Audio Engine
        InstallApo(destinationDir, progressCallback);

        progressCallback?.Invoke("Configuring shortcuts...", 75);
        var exePath = Path.Combine(destinationDir, "PermadB.exe");

        if (desktopShortcut)
        {
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            CreateShortcut(Path.Combine(desktopDir, "PermadB.lnk"), exePath, destinationDir);
        }

        if (startMenuShortcut)
        {
            var startMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            CreateShortcut(Path.Combine(startMenuDir, "PermadB.lnk"), exePath, destinationDir);
        }

        progressCallback?.Invoke("Configuring system registry...", 85);
        if (startWithWindows)
        {
            SetStartupRegistry(exePath);
        }

        RegisterAddRemovePrograms(destinationDir, exePath);

        progressCallback?.Invoke("Finalizing installation...", 100);

        if (launchNow && File.Exists(exePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = destinationDir,
                UseShellExecute = true
            });
        }
    }

    private static void InstallApo(string destinationDir, Action<string, int>? progressCallback)
    {
        try
        {
            progressCallback?.Invoke("Configuring Windows Audio Engine security policy...", 40);
            try
            {
                using var audioKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio", true);
                audioKey?.SetValue("DisableProtectedAudioDG", 1, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[InstallApo] Warning setting DisableProtectedAudioDG: {ex.Message}");
            }

            progressCallback?.Invoke("Staging APO driver package into Windows DriverStore...", 45);
            try
            {
                var infPath = Path.Combine(destinationDir, "PermadBApo.inf");
                if (!File.Exists(infPath))
                {
                    infPath = Path.Combine(destinationDir, "driver", "PermadBApo.inf");
                }

                if (File.Exists(infPath))
                {
                    var pnpPsi = new ProcessStartInfo("pnputil.exe", $"/add-driver \"{infPath}\" /install")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    var pnpProc = Process.Start(pnpPsi);
                    if (pnpProc != null)
                    {
                        var output = pnpProc.StandardOutput.ReadToEnd();
                        pnpProc.WaitForExit(10000);
                        Console.WriteLine($"[InstallApo] pnputil output:\n{output}");

                        var match = Regex.Match(output, @"Published Name\s*:\s*(oem\d+\.inf)", RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            var oemInf = match.Groups[1].Value.Trim();
                            try
                            {
                                using var permadbKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\PermadB", true);
                                permadbKey?.SetValue("DriverOemInf", oemInf, RegistryValueKind.String);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[InstallApo] Warning staging driver with pnputil: {ex.Message}");
            }

            progressCallback?.Invoke("Registering PermadB Limiter APO...", 50);
            var apoDllPath = Path.Combine(destinationDir, "PermadBApo.dll");
            if (File.Exists(apoDllPath))
            {
                try
                {
                    var psi = new ProcessStartInfo("regsvr32.exe", $"/s \"{apoDllPath}\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    var proc = Process.Start(psi);
                    proc?.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[InstallApo] Warning running regsvr32: {ex.Message}");
                }
            }

            // COM & AudioEngine registration for the APO
            try
            {
                using var clsidKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Classes\CLSID\{968ff234-1895-49f1-8b97-9d9075eb25b6}", true);
                if (clsidKey != null)
                {
                    clsidKey.SetValue("", "PermadB Limiter APO", RegistryValueKind.String);
                    using var inprocKey = clsidKey.CreateSubKey("InProcServer32", true);
                    if (inprocKey != null)
                    {
                        inprocKey.SetValue("", apoDllPath, RegistryValueKind.String);
                        inprocKey.SetValue("ThreadingModel", "Both", RegistryValueKind.String);
                    }
                }

                using var apoKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{968ff234-1895-49f1-8b97-9d9075eb25b6}", true);
                if (apoKey != null)
                {
                    apoKey.SetValue("FriendlyName", "PermadB Limiter APO", RegistryValueKind.String);
                    apoKey.SetValue("Copyright", "PermadB", RegistryValueKind.String);
                    apoKey.SetValue("MajorVersion", 1, RegistryValueKind.DWord);
                    apoKey.SetValue("MinorVersion", 0, RegistryValueKind.DWord);
                    apoKey.SetValue("Flags", 15, RegistryValueKind.DWord);
                    apoKey.SetValue("MinInputConnections", 1, RegistryValueKind.DWord);
                    apoKey.SetValue("MaxInputConnections", 1, RegistryValueKind.DWord);
                    apoKey.SetValue("MinOutputConnections", 1, RegistryValueKind.DWord);
                    apoKey.SetValue("MaxOutputConnections", 1, RegistryValueKind.DWord);
                    apoKey.SetValue("MaxInstances", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
                    apoKey.SetValue("NumAPOInterfaces", 1, RegistryValueKind.DWord);
                    apoKey.SetValue("APOInterface0", "{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}", RegistryValueKind.String);
                    try { apoKey.DeleteValue("APOInterface1", false); } catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[InstallApo] Warning registering AudioProcessingObjects key: {ex.Message}");
            }

            progressCallback?.Invoke("Associating APO with audio endpoints...", 60);
            ConfigureEndpointFxProperties();

            progressCallback?.Invoke("Refreshing Windows Audio Service...", 70);
            RestartAudioService();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[InstallApo] Unexpected error: {ex.Message}");
        }
    }

    private static void ConfigureEndpointFxProperties()
    {
        const string clsid = "{968ff234-1895-49f1-8b97-9d9075eb25b6}";
        const string pkeyEfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7";
        const string pkeyCompositeEfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15";
        const string pkeySfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},5";
        const string pkeyEfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},7";

        try
        {
            using var renderKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render");
            if (renderKey == null) return;

            var subKeyNames = renderKey.GetSubKeyNames();
            foreach (var deviceId in subKeyNames)
            {
                try
                {
                    using var devKey = renderKey.OpenSubKey(deviceId);
                    if (devKey == null) continue;

                    var stateVal = devKey.GetValue("DeviceState");
                    // DeviceState == 1 indicates active render device
                    if (stateVal is int state && state == 1)
                    {
                        var fxSubPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{deviceId}\FxProperties";
                        RegistryKey? fxKey = null;
                        try
                        {
                            fxKey = Registry.LocalMachine.OpenSubKey(
                                fxSubPath,
                                RegistryKeyPermissionCheck.ReadWriteSubTree,
                                System.Security.AccessControl.RegistryRights.SetValue | System.Security.AccessControl.RegistryRights.QueryValues);
                        }
                        catch
                        {
                            try
                            {
                                fxKey = Registry.LocalMachine.CreateSubKey(fxSubPath, true);
                            }
                            catch { }
                        }

                        if (fxKey != null)
                        {
                            using (fxKey)
                            {
                                // 1. Preserve and configure PKEY_FX_EndpointEffectClsid (,7)
                                var existingEfx = fxKey.GetValue(pkeyEfx) as string;
                                if (!string.IsNullOrEmpty(existingEfx) && !string.Equals(existingEfx, clsid, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (fxKey.GetValue("PermadB_Backup_EFX") == null)
                                    {
                                        fxKey.SetValue("PermadB_Backup_EFX", existingEfx, RegistryValueKind.String);
                                    }
                                }
                                fxKey.SetValue(pkeyEfx, clsid, RegistryValueKind.String);

                                // 2. Preserve and configure PKEY_CompositeFX_EndpointEffectClsid (,15)
                                var existingComp = fxKey.GetValue(pkeyCompositeEfx) as string[];
                                if (existingComp != null && existingComp.Length > 0)
                                {
                                    if (!existingComp.Any(s => string.Equals(s, clsid, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        if (fxKey.GetValue("PermadB_Backup_CompositeEFX") == null)
                                        {
                                            fxKey.SetValue("PermadB_Backup_CompositeEFX", existingComp, RegistryValueKind.MultiString);
                                        }
                                        var newComp = existingComp.Append(clsid).ToArray();
                                        fxKey.SetValue(pkeyCompositeEfx, newComp, RegistryValueKind.MultiString);
                                    }
                                }
                                else
                                {
                                    fxKey.SetValue("PermadB_Created_CompositeEFX", 1, RegistryValueKind.DWord);
                                    fxKey.SetValue(pkeyCompositeEfx, new string[] { clsid }, RegistryValueKind.MultiString);
                                }

                                // 3. Preserve and configure PKEY_EFX_ProcessingModes_Supported_For_Streaming (,7)
                                var existingEfxModes = fxKey.GetValue(pkeyEfxModes) as string[];
                                var sfxModes = fxKey.GetValue(pkeySfxModes) as string[];
                                var targetModes = sfxModes != null && sfxModes.Length > 0
                                    ? sfxModes
                                    : new string[] { "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}" };

                                if (existingEfxModes != null && existingEfxModes.Length > 0)
                                {
                                    if (fxKey.GetValue("PermadB_Backup_EFXModes") == null)
                                    {
                                        fxKey.SetValue("PermadB_Backup_EFXModes", existingEfxModes, RegistryValueKind.MultiString);
                                    }
                                    var mergedModes = existingEfxModes.Union(targetModes, StringComparer.OrdinalIgnoreCase).ToArray();
                                    fxKey.SetValue(pkeyEfxModes, mergedModes, RegistryValueKind.MultiString);
                                }
                                else
                                {
                                    fxKey.SetValue("PermadB_Created_EFXModes", 1, RegistryValueKind.DWord);
                                    fxKey.SetValue(pkeyEfxModes, targetModes, RegistryValueKind.MultiString);
                                }

                                fxKey.SetValue("PermadB_Installed_DeviceId", deviceId, RegistryValueKind.String);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ConfigureEndpointFxProperties] Error configuring device {deviceId}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ConfigureEndpointFxProperties] Error: {ex.Message}");
        }
    }

    private static void RestartAudioService()
    {
        try
        {
            // Terminate any audiodg.exe so that audio engine reloads APO on next playback
            foreach (var p in Process.GetProcessesByName("audiodg"))
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(2000);
                }
                catch { }
            }

            // Restart audiosrv service
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Restart-Service -Name audiosrv -Force\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RestartAudioService] Error: {ex.Message}");
        }
    }

    private static void KillExistingProcesses()
    {
        foreach (var proc in Process.GetProcessesByName("PermadB"))
        {
            try
            {
                proc.Kill();
                proc.WaitForExit(3000);
            }
            catch { }
        }

        foreach (var proc in Process.GetProcessesByName("audiodg"))
        {
            try
            {
                proc.Kill();
                proc.WaitForExit(2000);
            }
            catch { }
        }
    }

    private static void ExtractPayload(string destinationDir)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("PermadB.Installer.payload.zip");
        if (stream == null)
        {
            throw new InvalidOperationException("Embedded payload.zip was not found in installer assembly.");
        }

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(Path.Combine(destinationDir, entry.FullName));
                continue;
            }

            var destPath = Path.Combine(destinationDir, entry.FullName);
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            try
            {
                entry.ExtractToFile(destPath, overwrite: true);
            }
            catch (IOException)
            {
                try
                {
                    var oldPath = destPath + ".old";
                    if (File.Exists(oldPath)) { try { File.Delete(oldPath); } catch { } }
                    File.Move(destPath, oldPath);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
                catch { }
            }
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDir)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetPath;
                shortcut.WorkingDirectory = workingDir;
                shortcut.Description = "PermadB - Permanent Audio Decibel Guard";
                shortcut.IconLocation = $"{targetPath},0";
                shortcut.Save();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Shortcut] Error creating shortcut {shortcutPath}: {ex.Message}");
        }
    }

    private static void SetStartupRegistry(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.SetValue("PermadB", $"\"{exePath}\" --minimized");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] Error setting Run key: {ex.Message}");
        }
    }

    private static void RegisterAddRemovePrograms(string destinationDir, string exePath)
    {
        try
        {
            using var baseKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true);
            if (baseKey == null) return;

            using var subKey = baseKey.CreateSubKey("PermadB");
            if (subKey == null) return;

            var uninstallerPath = Path.Combine(destinationDir, "Uninstall.exe");

            subKey.SetValue("DisplayName", "PermadB - Permanent Audio Decibel Guard");
            subKey.SetValue("DisplayVersion", "1.0.0");
            subKey.SetValue("Publisher", "PermadB");
            subKey.SetValue("InstallLocation", destinationDir);
            subKey.SetValue("UninstallString", $"\"{uninstallerPath}\"");
            subKey.SetValue("DisplayIcon", $"{exePath},0");
            subKey.SetValue("EstimatedSize", 2048, RegistryValueKind.DWord);
            subKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
            subKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UninstallRegistry] Error: {ex.Message}");
        }
    }
}
