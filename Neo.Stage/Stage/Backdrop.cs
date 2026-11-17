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
}
