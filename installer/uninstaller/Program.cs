using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PermadB.Uninstaller;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        bool silent = args.Length > 0 && (args[0].Equals("/S", StringComparison.OrdinalIgnoreCase) || args[0].Equals("--silent", StringComparison.OrdinalIgnoreCase));

        if (!silent)
        {
            var result = MessageBox.Show(
                "Are you sure you want to completely uninstall PermadB from your computer?",
                "PermadB Uninstall",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result != DialogResult.Yes)
            {
                return;
            }
        }

        try
        {
            // 1. Terminate running PermadB processes
            foreach (var proc in Process.GetProcessesByName("PermadB"))
            {
                try
                {
                    proc.Kill();
                    proc.WaitForExit(3000);
                }
                catch { }
            }

            // 2. Remove Windows Startup Registry Key
            try
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                runKey?.DeleteValue("PermadB", false);
            }
            catch { }

            // 3. Remove Windows Add/Remove Programs Registry Key
            try
            {
                using var uninstallKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true);
                uninstallKey?.DeleteSubKeyTree("PermadB", false);
            }
            catch { }

            // 4. Remove Shortcuts
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var desktopLnk = Path.Combine(desktopDir, "PermadB.lnk");
            if (File.Exists(desktopLnk))
            {
                try { File.Delete(desktopLnk); } catch { }
            }

            var startMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var startMenuLnk = Path.Combine(startMenuDir, "PermadB.lnk");
            if (File.Exists(startMenuLnk))
            {
                try { File.Delete(startMenuLnk); } catch { }
            }

            // 4b. Remove User AppData Configuration (ensures clean reinstall)
            try
            {
                var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PermadB");
                if (Directory.Exists(appDataDir))
                {
                    Directory.Delete(appDataDir, true);
                }
            }
            catch { }

            // 5. Unregister and remove PermadB Limiter APO
            var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            UninstallApo(appDir);

            // 6. Self-cleanup: launch cmd to delete directory after this process exits
            var cmdArgs = $"/C ping 127.0.0.1 -n 2 > nul & rmdir /s /q \"{appDir}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdArgs,
                CreateNoWindow = true,
                UseShellExecute = false
            });

            if (!silent)
            {
                MessageBox.Show(
                    "PermadB was successfully uninstalled from your computer.",
                    "Uninstall Complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                MessageBox.Show($"Error during uninstall: {ex.Message}", "Uninstall Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private static void UninstallApo(string appDir)
    {
        const string clsid = "{968ff234-1895-49f1-8b97-9d9075eb25b6}";
        const string pkeyEfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7";
        const string pkeyCompositeEfx = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15";

        // 1. Remove APO associations from all render endpoints
        try
        {
            using var renderKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render");
            if (renderKey != null)
            {
                var subKeyNames = renderKey.GetSubKeyNames();
                foreach (var deviceId in subKeyNames)
                {
                    try
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
                        catch { }

                        if (fxKey != null)
                        {
                            using (fxKey)
                            {
                                const string pkeyEfxModes = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},7";

                                // 1. Restore PKEY_FX_EndpointEffectClsid (,7)
                                var currentEfx = fxKey.GetValue(pkeyEfx) as string;
                                if (string.Equals(currentEfx, clsid, StringComparison.OrdinalIgnoreCase))
                                {
                                    var backupEfx = fxKey.GetValue("PermadB_Backup_EFX") as string;
                                    if (!string.IsNullOrEmpty(backupEfx))
                                    {
                                        fxKey.SetValue(pkeyEfx, backupEfx, RegistryValueKind.String);
                                        fxKey.DeleteValue("PermadB_Backup_EFX", false);
                                    }
                                    else
                                    {
                                        fxKey.DeleteValue(pkeyEfx, false);
                                    }
                                }

                                // 2. Restore PKEY_CompositeFX_EndpointEffectClsid (,15)
                                var currentComp = fxKey.GetValue(pkeyCompositeEfx) as string[];
                                if (currentComp != null && currentComp.Any(s => string.Equals(s, clsid, StringComparison.OrdinalIgnoreCase)))
                                {
                                    var backupComp = fxKey.GetValue("PermadB_Backup_CompositeEFX") as string[];
                                    if (backupComp != null && backupComp.Length > 0)
                                    {
                                        fxKey.SetValue(pkeyCompositeEfx, backupComp, RegistryValueKind.MultiString);
                                        fxKey.DeleteValue("PermadB_Backup_CompositeEFX", false);
                                    }
                                    else
                                    {
                                        var remainingComp = currentComp.Where(s => !string.Equals(s, clsid, StringComparison.OrdinalIgnoreCase)).ToArray();
                                        if (remainingComp.Length > 0)
                                        {
                                            fxKey.SetValue(pkeyCompositeEfx, remainingComp, RegistryValueKind.MultiString);
                                        }
                                        else
                                        {
                                            fxKey.DeleteValue(pkeyCompositeEfx, false);
                                        }
                                    }
                                }
                                fxKey.DeleteValue("PermadB_Created_CompositeEFX", false);

                                // 3. Restore PKEY_EFX_ProcessingModes_Supported_For_Streaming (,7)
                                var backupEfxModes = fxKey.GetValue("PermadB_Backup_EFXModes") as string[];
                                if (backupEfxModes != null && backupEfxModes.Length > 0)
                                {
                                    fxKey.SetValue(pkeyEfxModes, backupEfxModes, RegistryValueKind.MultiString);
                                    fxKey.DeleteValue("PermadB_Backup_EFXModes", false);
                                }
                                else if (fxKey.GetValue("PermadB_Created_EFXModes") != null)
                                {
                                    fxKey.DeleteValue(pkeyEfxModes, false);
                                    fxKey.DeleteValue("PermadB_Created_EFXModes", false);
                                }

                                fxKey.DeleteValue("PermadB_Installed_DeviceId", false);
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 2. Unregister COM DLL
        try
        {
            var apoDllPath = Path.Combine(appDir, "PermadBApo.dll");
            if (File.Exists(apoDllPath))
            {
                var psi = new ProcessStartInfo("regsvr32.exe", $"/u /s \"{apoDllPath}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
            }
        }
        catch { }

        // 3. Remove AudioEngine AudioProcessingObjects & CLSID registry entries
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{968ff234-1895-49f1-8b97-9d9075eb25b6}", false);
            Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Classes\CLSID\{968ff234-1895-49f1-8b97-9d9075eb25b6}", false);
        }
        catch { }

        // 4. Restart Windows Audio Service to release DLL
        try
        {
            foreach (var p in Process.GetProcessesByName("audiodg"))
            {
                try { p.Kill(); p.WaitForExit(2000); } catch { }
            }

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
        catch { }

        // 5. Clean up public telemetry file and logs
        try
        {
            const string telemPath = @"C:\Users\Public\permadb_apo_telemetry.dat";
            if (File.Exists(telemPath)) File.Delete(telemPath);
        }
        catch { }

        try
        {
            const string logPath = @"C:\Users\Public\permadb_apo.log";
            if (File.Exists(logPath)) File.Delete(logPath);
        }
        catch { }

        // 6. Delete staged driver package from DriverStore via pnputil
        try
        {
            string? oemInf = null;
            try
            {
                using var permadbKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PermadB");
                oemInf = permadbKey?.GetValue("DriverOemInf") as string;
            }
            catch { }

            if (string.IsNullOrEmpty(oemInf))
            {
                try
                {
                    var psiEnum = new ProcessStartInfo("pnputil.exe", "/enum-drivers")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true
                    };
                    var procEnum = Process.Start(psiEnum);
                    if (procEnum != null)
                    {
                        var text = procEnum.StandardOutput.ReadToEnd();
                        procEnum.WaitForExit(5000);

                        var matches = Regex.Matches(
                            text,
                            @"Published Name\s*:\s*(oem\d+\.inf)[\s\S]*?Original Name\s*:\s*PermadBApo\.inf",
                            RegexOptions.IgnoreCase
                        );
                        if (matches.Count > 0)
                        {
                            oemInf = matches[0].Groups[1].Value.Trim();
                        }
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(oemInf))
            {
                var psiDel = new ProcessStartInfo("pnputil.exe", $"/delete-driver {oemInf} /uninstall /force")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var procDel = Process.Start(psiDel);
                procDel?.WaitForExit(10000);
            }

            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\PermadB", false);
            }
            catch { }
        }
        catch { }
    }
}
