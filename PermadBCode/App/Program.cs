using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using PermadB.Audio;
using PermadB.Config;
using PermadB.Forms;
using PermadB.Server;
using PermadB.Tray;

namespace PermadB;

static class Program
{
    private const string MutexName = @"Global\PermadB_SingleInstance_Mutex";
    private const string ShowDashboardSignalName = @"Global\PermadB_ShowDashboard_Signal";
    private static Mutex? _mutex;
    private static EventWaitHandle? _showSignal;

    [STAThread]
    static void Main(string[] args)
    {
        AppLogger.Log($"[MAIN_ENTRY] Args: '{string.Join(" ", args)}', ProcessId: {Environment.ProcessId}");
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            AppLogger.Log($"[FATAL] AppDomain UnhandledException: {e.ExceptionObject}");
        };
        Application.ThreadException += (s, e) =>
        {
            AppLogger.Log($"[FATAL] Application ThreadException: {e.Exception}");
        };

        // Enforce single instance
        _mutex = new Mutex(true, MutexName, out bool isNewInstance);
        AppLogger.Log($"[MAIN_MUTEX] isNewInstance={isNewInstance}");
        if (!isNewInstance)
        {
            AppLogger.Log($"[MAIN_MUTEX] Another instance already running, exiting.");
            // Another instance is already running; signal it to show its native dashboard popup and exit
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowDashboardSignalName);
                signal.Set();
            }
            catch { }
            return;
        }

        ApplicationConfiguration.Initialize();
        AppLogger.Log("[MAIN_STEP] ApplicationConfiguration.Initialize done");

        var config = new ConfigManager();
        var engine = new AudioEngine(config);
        var dashboardForm = new DashboardForm(config, engine);
        var tray = new TrayIconManager(config, engine);
        var apiServer = new ApiServer(config, engine);
        apiServer.Start();

        tray.OnOpenDashboardRequested += () =>
        {
            dashboardForm.SafeToggleNearTray();
        };

        tray.OnOpenAppManagerRequested += () =>
        {
            dashboardForm.ShowAppManagerView();
        };

        tray.OnExitRequested += () =>
        {
            Application.Exit();
        };

        // Single-instance activator listener thread
        try
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowDashboardSignalName);
            var signalThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _showSignal.WaitOne();
                        if (dashboardForm.IsDisposed) break;
                        dashboardForm.SafeShowNearTray();
                    }
                    catch { break; }
                }
            }) { IsBackground = true };
            signalThread.Start();
        }
        catch { }

        // Welcome notification if started normally
        bool isSilent = args.Length > 0 && args[0].Contains("minimized", StringComparison.OrdinalIgnoreCase);
        if (!isSilent && config.Settings.NotificationsEnabled)
        {
            var profile = engine.GetActiveProfile();
            tray.ShowNotification(
                "PermadB Decibel Guard Active",
                $"Protecting your hearing on: {profile.DeviceName} (Limiter: {profile.SafeCeilingPercent:F0}% | Windows Vol: 100%)",
                ToolTipIcon.Info
            );
        }

        AppLogger.Log($"[STARTUP] PermadB process starting up (PID {Environment.ProcessId})...");
        Application.ApplicationExit += (s, e) =>
        {
            AppLogger.Log($"[SHUTDOWN] Application.ApplicationExit triggered (PID {Environment.ProcessId}).");
            apiServer.Dispose();
            dashboardForm.Dispose();
            tray.Dispose();
            engine.Dispose();
            _showSignal?.Dispose();
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        };

        Application.Run();
    }
}
