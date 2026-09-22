using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// Visible top-level windows via Win32. Owned windows (Avalonia dialogs, the player's app window)
/// are not always UIA root children, and scanning the root by process id trips over processes the
/// test cannot open.
/// </summary>
internal static class TopLevelWindows
{
    public static AutomationElement? Find(Func<(int Pid, string Title), bool> predicate)
    {
        AutomationElement? found = null;
        EnumWindows((hwnd, _) =>
        {
            if (found != null || !IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            var title = new StringBuilder(512);
            GetWindowText(hwnd, title, title.Capacity);
            if (predicate((pid, title.ToString())))
            {
                try { found = AutomationElement.FromHandle(hwnd); } catch { }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
}
