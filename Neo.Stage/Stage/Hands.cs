using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Neo.Stage;

/// <summary>
/// What the stage may do with windows: show them smaller, larger, as they were, in front, or on one half of their
/// screen. All of it can be undone with a click. The shell's own windows (taskbar, desktop) are left alone. Also the
/// pictures the brain looks at: of what was pointed at, or of a whole window when it is asked what is there.
/// </summary>
internal static class Hands
{
    public static readonly string[] Actions = ["minimize", "maximize", "restore", "front", "left_half", "right_half"];

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

    // Windows lets only the program a person is using bring a window to the front. The stage borrows the input of the
    // window in front for a moment, which counts as that.
    private static bool Front(IntPtr window)
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

    private const uint GA_ROOT = 2, MONITOR_DEFAULTTONEAREST = 2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    private const int SW_MAXIMIZE = 3, SW_MINIMIZE = 6, SW_RESTORE = 9, DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
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
