using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace PermadB.Config;

public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "PermadB";

    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            var value = key?.GetValue(AppName) as string;
            return !string.IsNullOrEmpty(value);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] Error checking startup registry key: {ex.Message}");
            return false;
        }
    }

    public static bool SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return false;

            if (enable)
            {
                var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    // Quote the executable path to handle spaces safely
                    key.SetValue(AppName, $"\"{exePath}\" --minimized");
                    return true;
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] Error updating startup registry key: {ex.Message}");
        }

        return false;
    }
}
