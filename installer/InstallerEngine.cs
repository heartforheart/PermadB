using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace PermadB.Installer;

public static class InstallerEngine
{
    public static string GetDefaultInstallDir()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Programs", "PermadB");
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

        progressCallback?.Invoke("Extracting PermadB files...", 35);
        ExtractPayload(destinationDir);

        progressCallback?.Invoke("Configuring shortcuts...", 70);
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

            entry.ExtractToFile(destPath, overwrite: true);
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
