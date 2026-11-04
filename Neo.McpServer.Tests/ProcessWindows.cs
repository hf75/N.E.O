using System.Runtime.InteropServices;
using System.Text;

namespace Neo.Tests;

/// <summary>
/// The visible top-level windows of a process, through Win32: an exported app's windows can be counted,
/// read by title and closed without any input — no mouse, no keys, no foreground. Used by the export
/// tests of Neo.McpServer.Tests and Neo.App.Core.Tests.
/// </summary>
internal static class ProcessWindows
{
    public static List<(IntPtr Handle, string Title)> Of(int processId)
    {
        var found = new List<(IntPtr, string)>();
        EnumWindows((window, _) =>
        {
            if (GetWindowThreadProcessId(window, out var owner) != 0 && owner == processId && IsWindowVisible(window))
                found.Add((window, Title(window)));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Asks the window to close, as its close button does.</summary>
    public static void Close(IntPtr window) => PostMessage(window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    private static string Title(IntPtr window)
    {
        var text = new StringBuilder(GetWindowTextLength(window) + 1);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    private const uint WM_CLOSE = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
