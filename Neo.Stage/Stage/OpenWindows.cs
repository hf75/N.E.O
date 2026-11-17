using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;

namespace Neo.Stage;

/// <summary>
/// A window a person could mean by name: its title, its program and how it lies. <see cref="Number"/> is its place in
/// the list the brain got, the front one first.
/// </summary>
internal sealed record AppWindow(int Number, IntPtr Handle, string Title, string Program, string State, bool Front, uint ProcessId = 0)
{
    /// <summary>What a person would call it: the title, cut short.</summary>
    public string Label => Title.Length <= 48 ? Title : Title[..47] + "…";
}

/// <summary>
/// The windows of programs that are open, as the taskbar and Alt+Tab show them: visible, with a title, not a tool window,
/// not on another virtual desktop, the stage's own and the shell's left out. Minimized ones are in it too, since a person
/// may want them back. Only read here; <see cref="Hands"/> acts on them.
/// </summary>
internal static class OpenWindows
{
    /// <summary>The state of a minimized window, as the list says it.</summary>
    public static string Minimized => Language.Pick("minimiert", "minimized");

    /// <summary>The windows from the top of the screen down; the one in front is marked.</summary>
    /// <param name="only">Keeps only some (a check that must not see the person's windows).</param>
    public static IReadOnlyList<AppWindow> List(Func<string, string, bool>? only = null, int most = 24)
    {
        var front = GetForegroundWindow();
        var own = (uint)Environment.ProcessId;
        var found = new List<AppWindow>();
        EnumWindows((window, _) =>
        {
            if (found.Count < most && Candidate(window, own) is { } candidate && (only == null || only(candidate.Title, candidate.Program)))
                found.Add(new AppWindow(found.Count + 1, window, candidate.Title, candidate.Program, State(window), window == front, candidate.ProcessId));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Waits until a window appears that was not in <paramref name="before"/>, or one of them takes up
    /// <paramref name="hint"/> in its title (a page opened in a new tab, a folder in Explorer), and returns it; null when
    /// none did in time.
    /// </summary>
    public static async Task<AppWindow?> AppearedAsync(IReadOnlyList<AppWindow> before, string? hint, TimeSpan within)
    {
        var known = before.GroupBy(window => window.Handle).ToDictionary(group => group.Key, group => group.First().Title);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < within)
        {
            await Task.Delay(150);
            foreach (var window in List(most: 64))
            {
                if (!known.TryGetValue(window.Handle, out var title))
                {
                    if (window.State != Minimized)
                        return window;
                }
                else if (hint is { Length: > 1 } && title != window.Title && window.Title.Contains(hint, StringComparison.OrdinalIgnoreCase))
                {
                    return window;
                }
            }
        }
        return null;
    }

    /// <summary>The list as the brain reads it, one line per window.</summary>
    public static string Describe(IReadOnlyList<AppWindow> windows)
    {
        if (windows.Count == 0)
            return Language.Pick("Offene Fenster: keine.", "Open windows: none.");
        var text = new StringBuilder(Language.Pick("Offene Fenster (das oberste zuerst):", "Open windows (the top one first):"));
        foreach (var window in windows)
        {
            text.AppendLine();
            text.Append($"{window.Number}. „{window.Title}“ – {window.Program}, {window.State}");
            if (window.Front)
                text.Append(Language.Pick(", im Vordergrund", ", in front"));
        }
        return text.ToString();
    }

    /// <summary>Where the window is seen, in screen pixels, without the invisible border; null when it is minimized or gone.</summary>
    public static PixelRect? Seen(IntPtr window)
    {
        if (!IsWindow(window) || !IsWindowVisible(window) || IsIconic(window))
            return null;
        if (DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out NativeRect frame, Marshal.SizeOf<NativeRect>()) != 0 && !GetWindowRect(window, out frame))
            return null;
        return frame.Right > frame.Left && frame.Bottom > frame.Top ? new PixelRect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top) : null;
    }

    private static (string Title, string Program, uint ProcessId)? Candidate(IntPtr window, uint own)
    {
        if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0)
            return null;
        var style = GetWindowLongPtr(window, GWL_EXSTYLE);
        if ((style & WS_EX_TOOLWINDOW) != 0)
            return null;
        if (GetWindow(window, GW_OWNER) != IntPtr.Zero && (style & WS_EX_APPWINDOW) == 0)
            return null; // a dialog of another window
        if (DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return null; // on another virtual desktop, or a store app's hidden frame
        GetWindowThreadProcessId(window, out var processId);
        if (processId == own)
            return null;
        var className = ClassName(window);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return null;
        if (!IsIconic(window) && GetWindowRect(window, out var rect) && (rect.Right - rect.Left < 120 || rect.Bottom - rect.Top < 60))
            return null;

        // A store app lives in a frame of ApplicationFrameHost; its own process is the one of the window inside.
        if (className == "ApplicationFrameWindow")
            processId = InnerProcess(window, processId);
        var title = Title(window);
        var program = DesktopTargets.ProgramName(processId) ?? className;
        return (title.Length <= 140 ? title : title[..139] + "…", program, processId);
    }

    // minimiert, maximiert, linke or rechte Bildschirmhälfte, else where it lies; and the screen when there are several.
    private static string State(IntPtr window)
    {
        if (IsIconic(window))
            return Minimized;
        var monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var screen = GetMonitorInfo(monitor, ref info) && (info.Flags & MONITORINFOF_PRIMARY) == 0 ? Language.Pick(", auf dem zweiten Bildschirm", ", on the second screen") : "";
        if (IsZoomed(window))
            return Language.Pick("maximiert", "maximized") + screen;
        if (Seen(window) is not { } seen)
            return Language.Pick("unsichtbar", "not visible");
        var work = info.Work;
        var half = (work.Right - work.Left) / 2;
        bool Near(int a, int b) => Math.Abs(a - b) <= 16;
        if (Near(seen.Y, work.Top) && Near(seen.Bottom, work.Bottom) && Near(seen.Width, half))
        {
            if (Near(seen.X, work.Left))
                return Language.Pick("linke Bildschirmhälfte", "left half of the screen") + screen;
            if (Near(seen.Right, work.Right))
                return Language.Pick("rechte Bildschirmhälfte", "right half of the screen") + screen;
        }
        var invariant = CultureInfo.InvariantCulture;
        return string.Create(invariant, $"{Language.Pick("bei", "at")} x={seen.X - work.Left}, y={seen.Y - work.Top}, {seen.Width}×{seen.Height} {Language.Pick("Pixel von", "pixels of")} {work.Right - work.Left}×{work.Bottom - work.Top}{screen}");
    }

    private static uint InnerProcess(IntPtr frame, uint frameProcess)
    {
        var inner = frameProcess;
        EnumChildWindows(frame, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var childProcess);
            if (childProcess == frameProcess)
                return true;
            inner = childProcess;
            return false;
        }, IntPtr.Zero);
        return inner;
    }

    internal static string Title(IntPtr window)
    {
        var text = new StringBuilder(512);
        return GetWindowText(window, text, text.Capacity) > 0 ? text.ToString().Trim() : "";
    }

    internal static string ClassName(IntPtr window)
    {
        var name = new StringBuilder(256);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    private const int GWL_EXSTYLE = -20, DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    private const uint GW_OWNER = 4, MONITOR_DEFAULTTONEAREST = 2, MONITORINFOF_PRIMARY = 1;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
