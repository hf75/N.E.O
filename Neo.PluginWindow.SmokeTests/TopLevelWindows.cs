using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// Visible top-level windows via Win32, and input that can only land in the window it is meant
/// for. Owned windows (Avalonia dialogs, the player's app window) are not always UIA root
/// children, and scanning the root by process id trips over processes the test cannot open.
///
/// <para>Clicks and keystrokes go out only when the target window really is in front and the point
/// under the mouse belongs to its process; otherwise the step fails. Something else on the desktop
/// (a dialog of another app, a firewall prompt) must never receive a test's input.</para>
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

    /// <summary>Brings the window to the front, or throws saying what keeps the foreground.</summary>
    public static void Foreground(AutomationElement window)
    {
        var hwnd = (IntPtr)window.Current.NativeWindowHandle;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var current = GetForegroundWindow();
            if (current == hwnd) return;
            uint currentThread = GetWindowThreadProcessId(current, out _);
            uint me = GetCurrentThreadId();
            bool attached = currentThread != 0 && currentThread != me && AttachThreadInput(me, currentThread, true);
            ShowWindow(hwnd, 5);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            if (attached) AttachThreadInput(me, currentThread, false);
            if (GetForegroundWindow() != hwnd && attempt >= 2) SwitchToThisWindow(hwnd, true);
            Thread.Sleep(150);
        }
        if (GetForegroundWindow() == hwnd) return;

        GetWindowThreadProcessId(GetForegroundWindow(), out var holderPid);
        var holderTitle = new StringBuilder(256);
        GetWindowText(GetForegroundWindow(), holderTitle, holderTitle.Capacity);
        string holder;
        try { holder = Process.GetProcessById(holderPid).ProcessName; } catch { holder = holderPid.ToString(); }
        throw new InvalidOperationException(
            $"'{holder}' ('{holderTitle}') keeps the foreground; this UI test does not click or type into other windows. Close or answer that window and run the test again.");
    }

    /// <summary>A real mouse click on <paramref name="element"/> — only if nothing else covers it.</summary>
    public static void Click(AutomationElement window, AutomationElement element)
    {
        Foreground(window);
        var r = element.Current.BoundingRectangle;
        var point = new System.Windows.Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        var under = AutomationElement.FromPoint(point);
        if (under == null || under.Current.ProcessId != window.Current.ProcessId)
            throw new InvalidOperationException($"Another window covers '{element.Current.Name}' (process {under?.Current.ProcessId}); not clicking.");
        SetCursorPos((int)point.X, (int)point.Y);
        Thread.Sleep(80);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>Keystrokes (SendKeys syntax) into <paramref name="window"/> — only while it is in front.</summary>
    public static void Keys(AutomationElement window, string keys)
    {
        Foreground(window);
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        if (pid != window.Current.ProcessId)
            throw new InvalidOperationException("The foreground window belongs to another process; not typing.");
        System.Windows.Forms.SendKeys.SendWait(keys);
    }

    /// <summary>True when <paramref name="upper"/> lies above <paramref name="lower"/> in the z-order.</summary>
    public static bool IsAbove(IntPtr upper, IntPtr lower)
    {
        for (var hwnd = GetTopWindow(IntPtr.Zero); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, 2 /* GW_HWNDNEXT */))
        {
            if (hwnd == upper) return true;
            if (hwnd == lower) return false;
        }
        return false;
    }

    /// <summary>Ends the plugin windows a host started — and only those, not the ones of a N.E.O. someone is using.</summary>
    public static void KillChildrenOf(int hostPid)
    {
        foreach (var name in new[] { "Neo.PluginWindowAvalonia", "Neo.PluginWindowWPF" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { if (ParentProcessId(p.Id) == hostPid) p.Kill(); } catch { }
            }
    }

    public static int ParentProcessId(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var info = new PROCESS_BASIC_INFORMATION();
            return NtQueryInformationProcess(p.Handle, 0, ref info, Marshal.SizeOf(info), out _) == 0 ? info.InheritedFromUniqueProcessId.ToInt32() : -1;
        }
        catch { return -1; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1, PebBaseAddress, Reserved2_0, Reserved2_1, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, ref PROCESS_BASIC_INFORMATION info, int size, out int returned);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint command);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
