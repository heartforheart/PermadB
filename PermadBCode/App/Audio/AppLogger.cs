using System;
using System.IO;

namespace PermadB.Audio;

public static class AppLogger
{
    private static readonly object _fileLock = new();
    private static readonly string LogPath = @"C:\Users\Public\permadb_debug.log";
    private static DateTime _lastLogClean = DateTime.MinValue;

    public static void Log(string message)
    {
        try
        {
            lock (_fileLock)
            {
                var now = DateTime.Now;
                // Auto-trim if log exceeds 10MB
                if ((now - _lastLogClean).TotalMinutes > 15)
                {
                    _lastLogClean = now;
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 10 * 1024 * 1024)
                    {
                        File.Delete(LogPath);
                    }
                }

                string line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, line);
            }
        }
        catch { }
    }
}
