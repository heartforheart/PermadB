using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
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

            // 5. Self-cleanup: launch cmd to delete directory after this process exits
            var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
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
}
