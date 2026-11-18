using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace Neo.Stage;

/// <summary>
/// A calm, dark sky behind everything a demo shows: it covers the work area of the screen, so that none of the person's
/// own windows, icons or wallpaper is in the picture. The windows the demo opens come in front of it; the stage stays on
/// top of all. Not in the taskbar, and the stage's brain does not see it (it is the stage's own window).
/// </summary>
internal sealed class Backdrop : Window
{
    public Backdrop(Screen screen)
    {
        Title = "N.E.O. Stage backdrop";
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        Topmost = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var area = screen.WorkingArea;
        Position = area.Position;
        Width = area.Width / screen.Scaling;
        Height = area.Height / screen.Scaling;
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x0A, 0x0E, 0x1C), 0),
                new GradientStop(Color.FromRgb(0x14, 0x12, 0x2E), 0.55),
                new GradientStop(Color.FromRgb(0x08, 0x0B, 0x16), 1),
            },
        };
        // A faint light low in the middle, where the stage's ring will be.
        Content = new Border
        {
            Background = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.92, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.92, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.45, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.45, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Palette.Listen.WithAlpha(0.10), 0),
                    new GradientStop(Palette.Listen.WithAlpha(0), 1),
                },
            },
        };
    }

    public IntPtr Handle => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    /// <summary>
    /// Over the person's windows, once: by its place among the windows, never by taking the focus. A backdrop that could
    /// be activated came forward again whenever the stage began to listen, over the windows the demo had opened.
    /// </summary>
    public void Cover()
    {
        if (!OperatingSystem.IsWindows() || Handle == IntPtr.Zero)
            return;
        SetWindowLongPtr(Handle, GWL_EXSTYLE, GetWindowLongPtr(Handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        SetWindowPos(Handle, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private readonly HashSet<IntPtr> _lifted = [];

    /// <summary>
    /// The windows the demo shows go into the layer of windows that stay on top, once each, for as long as the demo runs:
    /// the backdrop, below that layer, cannot cover them whatever brings it forward (it came forward whenever the stage
    /// began to listen). True when one was lifted now: the stage must then go back over them.
    /// </summary>
    public bool Lift(IEnumerable<IntPtr> windows)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        var lifted = false;
        foreach (var window in windows)
        {
            if (window == IntPtr.Zero || !_lifted.Add(window))
                continue;
            SetWindowPos(window, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            App.Log($"backdrop: '{OpenWindows.Title(window)}' lifted over it");
            lifted = true;
        }
        return lifted;
    }

    /// <summary>The lifted windows back among the others, when the demo ends: those still open stay on the person's screen.</summary>
    public void Lower()
    {
        foreach (var window in _lifted)
            SetWindowPos(window, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        _lifted.Clear();
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern long SetWindowLongPtr(IntPtr window, int index, long value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
