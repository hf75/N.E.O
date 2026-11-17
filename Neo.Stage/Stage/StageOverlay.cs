using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace Neo.Stage;

/// <summary>Draws the current <see cref="StageFrame"/>.</summary>
internal sealed class StageCanvas : Control
{
    public StageFrame Frame { get; set; } = StageFrame.Empty;

    public override void Render(DrawingContext context) => StageRenderer.Draw(context, Frame);
}

/// <summary>
/// The stage: a window over one whole screen that shows light and words and nothing else. Clicks and the keyboard go
/// through it to the programs below; it never takes the focus, has no place in the taskbar or Alt+Tab, and stays on top.
/// </summary>
internal sealed class StageOverlay : Window
{
    private readonly StageCanvas _canvas = new() { IsHitTestVisible = false };

    public StageOverlay(Screen screen)
    {
        Title = "N.E.O. Stage";
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = _canvas;
        ScreenBounds = screen.Bounds;
        Scaling = screen.Scaling;
        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / screen.Scaling;
        Height = screen.Bounds.Height / screen.Scaling;
        WorkArea = FromScreen(screen.WorkingArea);
        Opened += (_, _) => LetInputThrough();
    }

    /// <summary>The screen this stage covers, in screen pixels.</summary>
    public PixelRect ScreenBounds { get; }

    /// <summary>The screen without the taskbar, in the stage's own coordinates.</summary>
    public Rect WorkArea { get; }

    public double Scaling { get; }

    public Size StageSize => new(Width, Height);

    /// <summary>A rectangle in screen pixels as the stage's own coordinates.</summary>
    public Rect FromScreen(PixelRect rect)
        => new((rect.X - ScreenBounds.X) / Scaling, (rect.Y - ScreenBounds.Y) / Scaling, rect.Width / Scaling, rect.Height / Scaling);

    public void Display(StageFrame frame)
    {
        _canvas.Frame = frame;
        _canvas.InvalidateVisual();
    }

    public IntPtr Handle => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    private void LetInputThrough()
    {
        if (!OperatingSystem.IsWindows() || Handle == IntPtr.Zero)
            return;
        // Layered and transparent together: Windows hit-tests through it. Tool window: not in Alt+Tab. No activate: never the focus.
        SetWindowLongPtr(Handle, GWL_EXSTYLE, GetWindowLongPtr(Handle, GWL_EXSTYLE) | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern long SetWindowLongPtr(IntPtr window, int index, long value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
