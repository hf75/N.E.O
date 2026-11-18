using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Neo.Stage;

/// <summary>
/// What the stage may do with windows: show them smaller, larger, as they were, in front, or on one half of their
/// screen. All of it can be undone with a click. The shell's own windows (taskbar, desktop) are left alone. Also the
/// pictures the brain looks at: of what was pointed at, or of a whole window when it is asked what is there.
/// </summary>
internal static class Hands
{
    public static readonly string[] Actions = ["minimize", "maximize", "restore", "front", "left_half", "right_half", "tile"];

    /// <summary>The top-level window a pointed thing belongs to.</summary>
    public static IntPtr TopWindow(PointedThing thing) => thing.Window == IntPtr.Zero ? IntPtr.Zero : GetAncestor(thing.Window, GA_ROOT);

    public static string Title(IntPtr window) => OpenWindows.Title(window);

    /// <summary>
    /// Does <paramref name="action"/> to <paramref name="window"/>; the sentence says what happened. "front" needs a
    /// thread with a message queue (the UI thread).
    /// </summary>
    public static string Arrange(IntPtr window, string action, bool pretend)
    {
        if (window == IntPtr.Zero || !IsWindow(window))
            return "Dieses Fenster gibt es nicht mehr.";
        if (OpenWindows.ClassName(window) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW")
            return "Das gehört zu Windows selbst (Taskleiste oder Desktop), das ordne ich nicht.";
        var name = Title(window) is { Length: > 0 } title ? $"„{title}“" : "das Fenster";
        if (pretend)
            return $"Nur eine Probe, nichts wurde verändert: {name} würde {Verb(action)}.";

        switch (action)
        {
            case "minimize":
                ShowWindow(window, SW_MINIMIZE);
                break;
            case "maximize":
                ShowWindow(window, SW_MAXIMIZE);
                break;
            case "restore":
                ShowWindow(window, SW_RESTORE);
                break;
            case "front":
                if (!Front(window))
                    return $"{name} ließ sich nicht nach vorne holen; Windows hat es nur in der Taskleiste markiert.";
                break;
            case "left_half" or "right_half":
                ShowWindow(window, SW_RESTORE);
                var monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref info))
                    return "Den Bildschirm des Fensters finde ich nicht.";
                var area = info.Work;
                var half = (area.Right - area.Left) / 2;
                var left = action == "left_half" ? area.Left : area.Left + half;
                // Windows draws most frames with an invisible border for resizing; the visible window fills the half.
                var (l, t, r, b) = InvisibleBorder(window);
                SetWindowPos(window, IntPtr.Zero, left - l, area.Top - t, half + l + r, area.Bottom - area.Top + t + b, SWP_NOZORDER | SWP_NOACTIVATE);
                break;
            default:
                return $"„{action}“ kann ich mit Fenstern nicht.";
        }
        return $"{name} ist {Done(action)}.";
    }

    /// <summary>Does <paramref name="action"/> to the window of a pointed thing.</summary>
    public static string Arrange(PointedThing thing, string action, bool pretend)
        => TopWindow(thing) is var window && window != IntPtr.Zero ? Arrange(window, action, pretend) : "Dazu gibt es kein Fenster.";

    /// <summary>
    /// Lays the windows side by side over the work area of the first one's screen, in a grid that fits their number:
    /// two side by side, three in a row, four in two rows of two, and so on.
    /// </summary>
    /// <param name="aboveStage">Leaves the strip at the bottom free where the stage speaks, with a little air between the windows.</param>
    public static string Tile(IReadOnlyList<IntPtr> windows, bool pretend, bool aboveStage = false)
    {
        var tiled = windows.Where(window => window != IntPtr.Zero && IsWindow(window)
            && OpenWindows.ClassName(window) is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW")).Distinct().ToList();
        if (tiled.Count == 0)
            return "Keines dieser Fenster gibt es.";
        if (pretend)
            return $"Nur eine Probe, nichts wurde verändert: {tiled.Count} Fenster würden nebeneinander gelegt werden.";
        var monitor = MonitorFromWindow(tiled[0], MONITOR_DEFAULTTONEAREST);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return "Den Bildschirm finde ich nicht.";
        var area = info.Work;
        var gap = aboveStage ? 12 : 0;
        if (aboveStage)
            area = new NativeRect { Left = area.Left + gap, Top = area.Top + gap, Right = area.Right - gap, Bottom = area.Bottom - 300 };
        var columns = tiled.Count <= 3 ? tiled.Count : (int)Math.Ceiling(Math.Sqrt(tiled.Count));
        var rows = (int)Math.Ceiling(tiled.Count / (double)columns);
        var width = (area.Right - area.Left + gap) / columns - gap;
        var height = (area.Bottom - area.Top + gap) / rows - gap;
        for (var i = 0; i < tiled.Count; i++)
        {
            var window = tiled[i];
            ShowWindow(window, SW_RESTORE);
            var (l, t, r, b) = InvisibleBorder(window);
            SetWindowPos(window, IntPtr.Zero, area.Left + i % columns * (width + gap) - l, area.Top + i / columns * (height + gap) - t, width + l + r, height + t + b,
                SWP_NOZORDER | SWP_NOACTIVATE);
        }
        return $"{tiled.Count} Fenster liegen jetzt nebeneinander, in {rows} {(rows == 1 ? "Reihe" : "Reihen")}.";
    }

    /// <summary>
    /// Brings a window to the front and has it drawn anew, all of it, at once and a moment later again: some programs
    /// (Word among them) show a change made from outside only when something happens in their window.
    /// </summary>
    public static void Show(IntPtr window)
    {
        Front(window);
        Redraw(window);
        _ = Task.Delay(400).ContinueWith(_ => Redraw(window), TaskScheduler.Default);
    }

    private static void Redraw(IntPtr window)
    {
        if (!IsWindow(window))
            return;
        PostMessage(window, WM_NULL, IntPtr.Zero, IntPtr.Zero); // wakes its message loop, like any input would
        RedrawWindow(window, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
    }

    /// <summary>
    /// Puts a window the stage made in the upper part of its screen, large and centred, so that what the stage shows at the
    /// bottom (the ring, the words, the answer) does not cover it. Only when it would.
    /// </summary>
    public static void AboveStage(IntPtr window)
    {
        if (!GetWindowRect(window, out var frame))
            return;
        var monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return;
        var area = info.Work;
        const int strip = 300, margin = 60;
        if (frame.Bottom <= area.Bottom - strip)
            return;
        var height = Math.Max(360, area.Bottom - strip - area.Top - margin);
        var width = Math.Min(area.Right - area.Left - 2 * margin, (int)(height * 1.5));
        var (l, t, r, b) = InvisibleBorder(window);
        SetWindowPos(window, IntPtr.Zero, area.Left + (area.Right - area.Left - width) / 2 - l, area.Top + margin - t, width + l + r, height + t + b,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>The window the person is using now.</summary>
    public static IntPtr Foreground() => GetForegroundWindow();

    /// <summary>Asks a window to close, as its close button would; the program may still ask about unsaved work.</summary>
    public static void Close(IntPtr window)
    {
        if (IsWindow(window))
            PostMessage(window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// A program started for the person comes to the front when its window appears: a new window, or one that took up
    /// <paramref name="hint"/> in its title (a new tab). Not when the person has gone on to another window meanwhile.
    /// </summary>
    /// <param name="place">An app the stage had built: it goes above the strip at the bottom where the stage speaks.</param>
    public static void FrontWhenItAppears(IReadOnlyList<AppWindow> before, string? hint, TimeSpan within, Action<AppWindow>? shown = null, bool place = false)
    {
        var foreground = GetForegroundWindow();
        _ = Task.Run(async () =>
        {
            var window = await OpenWindows.AppearedAsync(before, hint, within);
            if (window == null)
            {
                App.Log($"front: no new window for '{hint}'");
                return;
            }
            var now = GetForegroundWindow();
            if (now != foreground && now != window.Handle && before.Any(earlier => earlier.Handle == now))
            {
                App.Log($"front: '{window.Title}' left behind, the person went on in another window");
                return;
            }
            var front = await Dispatcher.UIThread.InvokeAsync(() => Front(window.Handle));
            if (place)
                AboveStage(window.Handle);
            shown?.Invoke(window);
            App.Log($"front: '{window.Title}' {(front ? "is in front" : "could not be brought to the front")}");
        });
    }

    // Windows lets only the program a person is using bring a window to the front. The stage borrows the input of the
    // window in front for a moment, which counts as that. Needs a thread with a message queue (the UI thread).
    public static bool Front(IntPtr window)
    {
        if (IsIconic(window))
            ShowWindow(window, SW_RESTORE);
        var foreground = GetForegroundWindow();
        var own = GetCurrentThreadId();
        var theirs = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;
        var attached = theirs != 0 && theirs != own && AttachThreadInput(own, theirs, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
                AttachThreadInput(own, theirs, false);
        }
        if (GetForegroundWindow() != window)
            SwitchToThisWindow(window, true);
        return GetForegroundWindow() == window;
    }

    private static string Verb(string action) => action switch
    {
        "minimize" => "minimiert",
        "maximize" => "maximiert",
        "restore" => "wiederhergestellt",
        "front" => "nach vorne geholt",
        "left_half" => "auf die linke Hälfte gelegt",
        "right_half" => "auf die rechte Hälfte gelegt",
        _ => action,
    } + " werden";

    private static string Done(string action) => action switch
    {
        "minimize" => "minimiert",
        "maximize" => "maximiert",
        "restore" => "wiederhergestellt",
        "front" => "jetzt vorne",
        "left_half" => "jetzt auf der linken Hälfte",
        "right_half" => "jetzt auf der rechten Hälfte",
        _ => action,
    };

    /// <summary>
    /// A picture of what was pointed at, with some of its surroundings, as PNG: drawn by its window (PrintWindow), so the
    /// stage itself and windows above it do not show. At most about 900 × 700 pixels.
    /// </summary>
    public static byte[]? Picture(PointedThing thing)
    {
        var window = TopWindow(thing);
        if (window == IntPtr.Zero || !GetWindowRect(window, out var frame))
            return null;
        const int margin = 40;
        return Picture(window, new PixelRect(thing.Bounds.X - margin - frame.Left, thing.Bounds.Y - margin - frame.Top, thing.Bounds.Width + 2 * margin, thing.Bounds.Height + 2 * margin),
            900, 700);
    }

    /// <summary>A picture of the whole window as it is, drawn by itself, as PNG of at most <paramref name="most"/> pixels a side.</summary>
    public static byte[]? Picture(IntPtr window, int most = 1280)
    {
        if (window == IntPtr.Zero || IsIconic(window) || !GetWindowRect(window, out var frame))
            return null;
        // Without the invisible border, which the picture would show as a dark edge.
        var (l, t, r, b) = InvisibleBorder(window);
        return Picture(window, new PixelRect(l, t, frame.Right - frame.Left - l - r, frame.Bottom - frame.Top - t - b), most, most);
    }

    // Part of the window's own picture (crop is in window pixels), scaled down to fit, as PNG.
    private static byte[]? Picture(IntPtr window, PixelRect crop, int mostWidth, int mostHeight)
    {
        if (!GetWindowRect(window, out var frame))
            return null;
        using var whole = DesktopTargets.Picture(new DesktopWindow(window, new PixelRect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top), ""));
        if (whole == null)
            return null;

        var size = whole.PixelSize;
        var left = Math.Clamp(crop.X, 0, size.Width - 1);
        var top = Math.Clamp(crop.Y, 0, size.Height - 1);
        crop = new PixelRect(left, top, Math.Clamp(crop.Right, left + 1, size.Width) - left, Math.Clamp(crop.Bottom, top + 1, size.Height) - top);
        var scale = Math.Min(1, Math.Min((double)mostWidth / crop.Width, (double)mostHeight / crop.Height));
        var target = new PixelSize(Math.Max(1, (int)(crop.Width * scale)), Math.Max(1, (int)(crop.Height * scale)));

        using var picture = new RenderTargetBitmap(target, new Vector(96, 96));
        using (var context = picture.CreateDrawingContext())
            context.DrawImage(whole, new Rect(crop.X, crop.Y, crop.Width, crop.Height), new Rect(0, 0, target.Width, target.Height));
        using var png = new MemoryStream();
        picture.Save(png, PngBitmapEncoderOptions.Default);
        return png.ToArray();
    }

    // How far the window's rectangle reaches beyond what is seen of it, on each side.
    private static (int Left, int Top, int Right, int Bottom) InvisibleBorder(IntPtr window)
    {
        if (!GetWindowRect(window, out var whole) || DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out var seen, Marshal.SizeOf<NativeRect>()) != 0)
            return (0, 0, 0, 0);
        return (Math.Max(0, seen.Left - whole.Left), Math.Max(0, seen.Top - whole.Top), Math.Max(0, whole.Right - seen.Right), Math.Max(0, whole.Bottom - seen.Bottom));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    private const uint GA_ROOT = 2, MONITOR_DEFAULTTONEAREST = 2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, WM_NULL = 0, WM_CLOSE = 0x10;
    private const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100, RDW_FRAME = 0x400;
    private const int SW_MAXIMIZE = 3, SW_MINIMIZE = 6, SW_RESTORE = 9, DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr window, IntPtr update, IntPtr region, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr window, bool altTab);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool doAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);
}
