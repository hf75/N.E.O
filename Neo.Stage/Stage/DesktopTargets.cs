using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Neo.Stage;

/// <summary>A window on the desktop: where it is in screen pixels, and what it is called for a person.</summary>
internal sealed record DesktopWindow(IntPtr Handle, PixelRect Bounds, string Name);

/// <summary>
/// The windows a dry run points at: real ones on this screen, from the top down. Named by their program
/// ("Microsoft Word"), not by their title, which may say more than the stage should show. Only read, never touched.
/// </summary>
internal static class DesktopTargets
{
    public static IReadOnlyList<DesktopWindow> Find(PixelRect screen)
    {
        var found = new List<DesktopWindow>();
        var own = Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            if (Candidate(window, screen, own) is { } candidate)
                found.Add(candidate);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Two windows that can both be seen: the top one, and the next one that it does not mostly cover.</summary>
    public static (DesktopWindow First, DesktopWindow? Second) PickTwo(IReadOnlyList<DesktopWindow> windows)
    {
        var first = windows[0];
        var second = windows.Skip(1).FirstOrDefault(window =>
        {
            var overlap = Intersect(window.Bounds, first.Bounds);
            return (double)overlap.Width * overlap.Height < 0.45 * window.Bounds.Width * window.Bounds.Height;
        });
        return (first, second);
    }

    private static DesktopWindow? Candidate(IntPtr window, PixelRect screen, int own)
    {
        if (!IsWindowVisible(window) || IsIconic(window) || GetWindowTextLength(window) == 0)
            return null;
        if ((GetWindowLongPtr(window, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0)
            return null;
        if (DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return null;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == own)
            return null;
        var className = ClassName(window);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return null;
        if (DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out NativeRect frame, Marshal.SizeOf<NativeRect>()) != 0 && !GetWindowRect(window, out frame))
            return null;

        var bounds = new PixelRect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
        if (bounds.Width < 320 || bounds.Height < 200)
            return null;
        var visible = Intersect(bounds, screen);
        if ((double)visible.Width * visible.Height < 0.6 * bounds.Width * bounds.Height)
            return null;
        return new DesktopWindow(window, visible, ProgramName(processId) ?? className);
    }

    private static PixelRect Intersect(PixelRect a, PixelRect b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return right > left && bottom > top ? new PixelRect(left, top, right - left, bottom - top) : default;
    }

    // "Microsoft Word", "Windows-Explorer": the description the program gives itself.
    private static string? ProgramName(uint processId)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var length = buffer.Capacity;
            if (!QueryFullProcessImageName(process, 0, buffer, ref length))
                return null;
            var path = buffer.ToString();
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            return string.IsNullOrWhiteSpace(description) ? Path.GetFileNameWithoutExtension(path) : description.Trim();
        }
        catch (Exception exception) when (exception is FileNotFoundException or UnauthorizedAccessException) { return null; }
        finally { CloseHandle(process); }
    }

    private static string ClassName(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    /// <summary>
    /// A picture of the window as it is, drawn by the window itself (PrintWindow), so that windows above it do not
    /// show. Null when it cannot be had. It is only shown on this screen.
    /// </summary>
    public static Bitmap? Picture(DesktopWindow window)
    {
        if (!GetWindowRect(window.Handle, out var whole))
            return null;
        var width = whole.Right - whole.Left;
        var height = whole.Bottom - whole.Top;
        if (width <= 0 || height <= 0 || width > 8000 || height > 8000)
            return null;

        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var info = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var section = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (section == IntPtr.Zero)
                return null;
            var previous = SelectObject(memory, section);
            var drawn = PrintWindow(window.Handle, memory, PW_RENDERFULLCONTENT);
            SelectObject(memory, previous);
            if (!drawn)
                return null;

            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255; // GDI leaves alpha at 0

            // Only the part inside the frame: the window rectangle includes the invisible resize border.
            var crop = new PixelRect(window.Bounds.X - whole.Left, window.Bounds.Y - whole.Top, window.Bounds.Width, window.Bounds.Height);
            if (crop.X < 0 || crop.Y < 0 || crop.Right > width || crop.Bottom > height)
                crop = new PixelRect(0, 0, width, height);
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var start = handle.AddrOfPinnedObject() + (crop.Y * width + crop.X) * 4;
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, start, new PixelSize(crop.Width, crop.Height), new Vector(96, 96), width * 4);
            }
            finally { handle.Free(); }
        }
        finally
        {
            if (section != IntPtr.Zero)
                DeleteObject(section);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    private const int GWL_EXSTYLE = -20, DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PW_RENDERFULLCONTENT = 2;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr deviceContext);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr deviceContext, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr gdiObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr gdiObject);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
}
