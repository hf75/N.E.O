using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Neo.Stage;

/// <summary>
/// What the stage may do with windows: show them smaller, larger, as they were, or on one half of their screen. All of it
/// can be undone with a click. The shell's own windows (taskbar, desktop) are left alone. Also the pictures of what was
/// pointed at, which the brain gets with the words.
/// </summary>
internal static class Hands
{
    public static readonly string[] Actions = ["minimize", "maximize", "restore", "left_half", "right_half"];

    /// <summary>The top-level window a pointed thing belongs to.</summary>
    public static IntPtr TopWindow(PointedThing thing) => thing.Window == IntPtr.Zero ? IntPtr.Zero : GetAncestor(thing.Window, GA_ROOT);

    public static string Title(IntPtr window)
    {
        var text = new StringBuilder(512);
        return GetWindowText(window, text, text.Capacity) > 0 ? text.ToString() : "";
    }

    /// <summary>Does <paramref name="action"/> to the window of <paramref name="thing"/>; the sentence says what happened.</summary>
    public static string Arrange(PointedThing thing, string action, bool pretend)
    {
        var window = TopWindow(thing);
        if (window == IntPtr.Zero)
            return "Dazu gibt es kein Fenster.";
        var className = ClassName(window);
        if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW")
            return "Das gehört zu Windows selbst (Taskleiste oder Desktop), das ordne ich nicht.";
        var name = Title(window) is { Length: > 0 } title ? $"„{title}“" : "das Fenster";
        if (pretend)
            return $"Probe: {name} würde {Verb(action)}.";

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

    private static string Verb(string action) => action switch
    {
        "minimize" => "minimiert",
        "maximize" => "maximiert",
        "restore" => "wiederhergestellt",
        "left_half" => "auf die linke Hälfte gelegt",
        "right_half" => "auf die rechte Hälfte gelegt",
        _ => action,
    } + " werden";

    private static string Done(string action) => action switch
    {
        "minimize" => "minimiert",
        "maximize" => "maximiert",
        "restore" => "wiederhergestellt",
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
        using var whole = DesktopTargets.Picture(new DesktopWindow(window, new PixelRect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top), ""));
        if (whole == null)
            return null;

        const int margin = 40;
        var crop = new PixelRect(thing.Bounds.X - margin - frame.Left, thing.Bounds.Y - margin - frame.Top, thing.Bounds.Width + 2 * margin, thing.Bounds.Height + 2 * margin);
        var size = whole.PixelSize;
        var left = Math.Clamp(crop.X, 0, size.Width - 1);
        var top = Math.Clamp(crop.Y, 0, size.Height - 1);
        crop = new PixelRect(left, top, Math.Clamp(crop.Right, left + 1, size.Width) - left, Math.Clamp(crop.Bottom, top + 1, size.Height) - top);
        var scale = Math.Min(1, Math.Min(900.0 / crop.Width, 700.0 / crop.Height));
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

    private static string ClassName(IntPtr window)
    {
        var name = new StringBuilder(256);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    private const uint GA_ROOT = 2, MONITOR_DEFAULTTONEAREST = 2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    private const int SW_MAXIMIZE = 3, SW_MINIMIZE = 6, SW_RESTORE = 9, DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);
}
