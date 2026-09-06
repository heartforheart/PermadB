using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using PermadB.Audio;
using PermadB.Config;
using PermadB.Server;
using PermadB.Tray;

namespace PermadB;

static class Program
{
    private const string MutexName = @"Global\PermadB_SingleInstance_Mutex";
    private static Mutex? _mutex;

    [STAThread]
    static void Main(string[] args)
    {
        // Enforce single instance
        _mutex = new Mutex(true, MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            // Another instance is already running; open dashboard in browser and exit
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "http://127.0.0.1:49220/",
                    UseShellExecute = true
                });
            }
            catch { }
            return;
        }

        ApplicationConfiguration.Initialize();

        var config = new ConfigManager();
        var engine = new AudioEngine(config);
        var server = new ApiServer(config, engine);
        server.Start();

        var tray = new TrayIconManager(config, engine);

        tray.OnOpenDashboardRequested += () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"http://127.0.0.1:{config.Settings.ApiPort}/",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Program] Failed to open dashboard: {ex.Message}");
            }
        };

        tray.OnExitRequested += () =>
        {
            Application.Exit();
        };

        // Welcome notification if started normally
        bool isSilent = args.Length > 0 && args[0].Contains("minimized", StringComparison.OrdinalIgnoreCase);
        if (!isSilent && config.Settings.NotificationsEnabled)
        {
            var profile = engine.GetActiveProfile();
            tray.ShowNotification(
                "PermadB Decibel Guard Active",
                $"Permanently protecting your hearing on: {profile.DeviceName} (Cap: {profile.SafeCeilingPercent:F0}%)",
                ToolTipIcon.Info
            );
        }

        Application.ApplicationExit += (s, e) =>
        {
            tray.Dispose();
            server.Dispose();
            engine.Dispose();
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        };

        Application.Run();
    }
}
