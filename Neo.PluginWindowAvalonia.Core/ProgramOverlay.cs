using System;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Neo.PluginWindowAvalonia
{
    /// <summary>
    /// What a drop would do, shown over the window of the other program this app is the interface of: a program dragged
    /// there merges into the interface on the left half and links with it on the right half, as over the app's own window.
    /// A window of its own, just above the program's and below the one being dragged, which it does not take the input of.
    /// </summary>
    internal sealed class ProgramOverlay
    {
        private Window? _overlay;
        private Border _merge = null!, _link = null!, _mergePill = null!, _linkPill = null!;
        private TextBlock _mergeText = null!, _linkText = null!;
        private Grid _halves = null!;

        /// <summary>Another program is dragged over <paramref name="program"/>'s window (its name), or not any more (null).</summary>
        public void Show(IntPtr program, string? name, bool link, bool linkOnly)
        {
            if (name == null || program == IntPtr.Zero || !GetFrame(program, out var frame))
            {
                _overlay?.Hide();
                return;
            }

            var overlay = _overlay ??= Create();
            _mergeText.Text = $"Drop to merge {name} into the interface of this program";
            _linkText.Text = $"Drop to link {name} with this program";
            _merge.IsVisible = !linkOnly;
            Grid.SetColumn(_link, linkOnly ? 0 : 1);
            Grid.SetColumnSpan(_link, linkOnly ? 2 : 1);
            Zone(_merge, _mergePill, "#1565C0", on: !link && !linkOnly);
            Zone(_link, _linkPill, "#00796B", on: link || linkOnly);

            var scaling = overlay.Screens.ScreenFromPoint(new PixelPoint(frame.Left, frame.Top))?.Scaling ?? 1;
            overlay.Position = new PixelPoint(frame.Left, frame.Top);
            overlay.Width = (frame.Right - frame.Left) / scaling;
            overlay.Height = (frame.Bottom - frame.Top) / scaling;
            if (!overlay.IsVisible) overlay.Show();

            if (overlay.TryGetPlatformHandle()?.Handle is { } own && own != IntPtr.Zero)
            {
                // Not a window to drop on, nor one that takes the keyboard.
                SetWindowLongPtr(own, GWL_EXSTYLE, GetWindowLongPtr(own, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
                var above = GetWindow(program, GW_HWNDPREV);
                SetWindowPos(own, above == IntPtr.Zero || above == own ? HWND_TOP : above, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }

        public void Hide() => _overlay?.Hide();

        private Window Create()
        {
            _mergeText = Pill(out _mergePill, "#1565C0");
            _linkText = Pill(out _linkPill, "#00796B");
            _merge = new Border { BorderBrush = Brush.Parse("#1565C0"), Child = _mergePill };
            _link = new Border { BorderBrush = Brush.Parse("#00796B"), Child = _linkPill };
            _halves = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), IsHitTestVisible = false };
            _halves.Children.Add(_merge);
            Grid.SetColumn(_link, 1);
            _halves.Children.Add(_link);

            return new Window
            {
                Title = "N.E.O. drop",
                WindowDecorations = WindowDecorations.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                CanResize = false,
                Background = Brushes.Transparent,
                TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
                Content = _halves,
            };
        }

        private static TextBlock Pill(out Border pill, string color)
        {
            var text = new TextBlock { Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            pill = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 16),
                Padding = new Thickness(16, 10), CornerRadius = new CornerRadius(18), Background = Brush.Parse(color), Child = text,
            };
            return text;
        }

        // What the drop would do stands out; the program shows through both halves, because it is what keeps running.
        private static void Zone(Border zone, Border pill, string color, bool on)
        {
            zone.Background = Brush.Parse(on ? "#50" + color[1..] : "#14" + color[1..]);
            zone.BorderThickness = new Thickness(on ? 4 : 1);
            pill.Opacity = on ? 1 : 0.55;
        }

        // The window rectangle includes the invisible resize border.
        private static bool GetFrame(IntPtr window, out Rect frame) =>
            DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out frame, Marshal.SizeOf<Rect>()) == 0 || GetWindowRect(window, out frame);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        private const int GWL_EXSTYLE = -20, DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        private const uint GW_HWNDPREV = 3, SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern long SetWindowLongPtr(IntPtr window, int index, long value);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
    }
}
