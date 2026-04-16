using System;
using System.IO;
using System.Threading;

namespace Neo.ExcelMcp.AddIn;

/// <summary>
/// Tiny file logger. Writes to %LOCALAPPDATA%\NeoExcelMcp\addin.log.
/// Thread-safe. Never throws — logging must not break the host.
/// Tail from another terminal:
///   powershell "Get-Content $env:LOCALAPPDATA\NeoExcelMcp\addin.log -Wait -Tail 30"
/// </summary>
internal static class Log
{
    private static readonly object _lock = new();
    public static string LogPath { get; }

    static Log()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NeoExcelMcp");
        try { Directory.CreateDirectory(dir); } catch { /* ignore */ }
        LogPath = Path.Combine(dir, "addin.log");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Thread.CurrentThread.ManagedThreadId}] {message}{Environment.NewLine}";
            lock (_lock)
            {
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // logging must never throw
        }
    }
}
