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
/// under the mouse belongs to the target; otherwise the step fails. Something else on the desktop
/// (a dialog of another app or of the same one, a firewall prompt) must never receive a test's input.</para>
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
        if (!IsWithin(under, element) && !IsWithin(element, under))
            throw new InvalidOperationException($"'{under.Current.Name}' ({under.Current.ControlType.ProgrammaticName}) lies over '{element.Current.Name}'; not clicking.");
        SetCursorPos((int)point.X, (int)point.Y);
        Thread.Sleep(80);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>
    /// A real drag with the mouse, from a point of <paramref name="window"/>: only if that point really is on it.
    /// <paramref name="whileHeld"/> runs at the end of the way, with the button still down. <paramref name="inFront"/> false:
    /// the window is one that takes no activation (N.E.O.'s frame over another program); a person's press leaves the
    /// window in front where it is, and so does this one.
    /// </summary>
    public static async Task DragAsync(AutomationElement window, int fromX, int fromY, int toX, int toY, Func<Task>? whileHeld = null, bool inFront = true)
    {
        if (inFront) Foreground(window);
        var under = AutomationElement.FromPoint(new System.Windows.Point(fromX, fromY));
        if (under == null || under.Current.ProcessId != window.Current.ProcessId)
            throw new InvalidOperationException($"Another window lies at {fromX},{fromY} (process {under?.Current.ProcessId}); not dragging.");

        SetCursorPos(fromX, fromY);
        await Task.Delay(120);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        try
        {
            await Task.Delay(120);
            const int Steps = 25;
            for (int step = 1; step <= Steps; step++)
            {
                SetCursorPos(fromX + (toX - fromX) * step / Steps, fromY + (toY - fromY) * step / Steps);
                await Task.Delay(15);
            }
            await Task.Delay(100);
            if (whileHeld != null) await whileHeld();
            await Task.Delay(350);
        }
        finally
        {
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        }
    }

    public static bool IsForeground(AutomationElement window) => GetForegroundWindow() == (IntPtr)window.Current.NativeWindowHandle;

    /// <summary>The pointer onto a point of <paramref name="window"/>, no button: only if that point really is on it.</summary>
    public static void Hover(AutomationElement window, int x, int y)
    {
        Foreground(window);
        var under = AutomationElement.FromPoint(new System.Windows.Point(x, y));
        if (under == null || under.Current.ProcessId != window.Current.ProcessId)
            throw new InvalidOperationException($"Another window lies at {x},{y} (process {under?.Current.ProcessId}); not moving there.");
        SetCursorPos(x, y);
    }

    /// <summary>One notch of the mouse wheel, towards the user (negative) or away, over <paramref name="window"/> only.</summary>
    public static void Wheel(AutomationElement window, int notches)
    {
        Foreground(window);
        GetCursorPos(out var at);
        var under = AutomationElement.FromPoint(new System.Windows.Point(at.X, at.Y));
        if (under == null || under.Current.ProcessId != window.Current.ProcessId)
            throw new InvalidOperationException($"Another window lies under the pointer (process {under?.Current.ProcessId}); not turning the wheel.");
        mouse_event(0x0800, 0, 0, unchecked((uint)(notches * 120)), UIntPtr.Zero);
    }

    // The point may report a part of the target (the text in a button) or what contains it (a control
    // without a hit test). Anything else, a dialog of the same process for one, is in the way.
    private static bool IsWithin(AutomationElement inner, AutomationElement outer)
    {
        for (var e = inner; e != null; e = TreeWalker.RawViewWalker.GetParent(e))
            if (Automation.Compare(e, outer)) return true;
        return false;
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
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
}
