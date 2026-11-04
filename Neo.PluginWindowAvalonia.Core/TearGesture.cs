using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace Neo.PluginWindowAvalonia
{
    /// <summary>
    /// Fission, the other way round from Fusion: with Alt held, the part of the app under the pointer stands out, and dragged
    /// out of the window with Alt it is torn out, and N.E.O. makes a program of its own of it. Let go inside the window,
    /// nothing happens. A part is a piece of the layout that lies beside others, not a letter in a text.
    /// </summary>
    internal sealed class TearGesture
    {
        // How far the pointer goes before a press is a drag, in device-independent pixels.
        private const double StartAfter = 8;
        private const double MinWidth = 40, MinHeight = 24;

        private readonly MainWindow _window;
        private readonly Func<bool> _canTear;
        private readonly Action<Control, PixelPoint> _tornOut;

        private Control? _part;
        private bool _pressed, _dragging;
        private Point _pressedAt;
        private Point _grab; // where in the part the pointer took it, so that its picture stays under the pointer
        private Window? _ghost;

        /// <param name="tornOut">The part, and where on the screen it was let go.</param>
        public TearGesture(MainWindow window, Func<bool> canTear, Action<Control, PixelPoint> tornOut)
        {
            _window = window;
            _canTear = canTear;
            _tornOut = tornOut;

            // Before the app's own controls: the press that takes a part is not a click on it.
            window.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            window.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            window.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            window.AddHandler(InputElement.KeyUpEvent, (_, e) => { if (e.Key is Key.LeftAlt or Key.RightAlt && !_pressed) Show(null); }, RoutingStrategies.Tunnel, handledEventsToo: true);
            window.PointerExited += (_, _) => { if (!_pressed) Show(null); };
        }

        private static bool Alt(PointerEventArgs e) => e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        private void OnMoved(object? sender, PointerEventArgs e)
        {
            if (_pressed)
            {
                var moved = e.GetPosition(_window) - _pressedAt;
                if (!_dragging && Math.Sqrt(moved.X * moved.X + moved.Y * moved.Y) > StartAfter) StartDrag();
                if (_dragging) MoveGhost(e);
                e.Handled = true;
                return;
            }
            Show(Alt(e) && _canTear() ? PartAt(e.GetPosition(_window)) : null);
        }

        private void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!Alt(e) || !e.GetCurrentPoint(_window).Properties.IsLeftButtonPressed || !_canTear()) return;
            if (PartAt(e.GetPosition(_window)) is not { } part) return;

            Show(part);
            _pressed = true;
            _pressedAt = e.GetPosition(_window);
            _grab = part.TranslatePoint(new Point(0, 0), _window) is { } corner ? _pressedAt - corner : default;
            e.Pointer.Capture(_window);
            e.Handled = true;
        }

        private void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_pressed) return;
            e.Handled = true;
            _pressed = false;
            e.Pointer.Capture(null);
            var dragged = _dragging;
            _dragging = false;
            _ghost?.Close();
            _ghost = null;
            var part = _part;
            Show(null);
            if (!dragged || part == null) return;

            // Outside the window it is torn out; inside, the person changed their mind.
            var screen = _window.PointToScreen(e.GetPosition(_window));
            if (Inside(screen)) return;
            _tornOut(part, screen);
        }

        // The nearest piece of the layout under the pointer that lies beside others, and is big enough to be a part.
        private Control? PartAt(Point point)
        {
            if (_window.GetLoadedUserControl() is not { } root) return null;
            for (var visual = _window.InputHitTest(point) as Visual; visual != null && !ReferenceEquals(visual, root); visual = visual.GetVisualParent())
            {
                if (visual is Control { TemplatedParent: null } control && control.GetVisualParent() is Panel { Children.Count: >= 2 }
                    && control.Bounds.Width >= MinWidth && control.Bounds.Height >= MinHeight && root.IsVisualAncestorOf(control))
                    return control;
            }
            return null;
        }

        private void Show(Control? part)
        {
            if (ReferenceEquals(part, _part) && (part != null || !_window.TearTargetShown)) return;
            _part = part;
            _window.ShowTearTarget(part != null && part.TranslatePoint(new Point(0, 0), _window) is { } corner ? new Rect(corner, part.Bounds.Size) : null);
        }

        // What is being torn out goes along with the pointer, as a picture of itself.
        private void StartDrag()
        {
            if (_part is not { } part) return;
            _dragging = true;

            var scaling = _window.RenderScaling;
            var size = part.Bounds.Size;
            var picture = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)(size.Width * scaling)), Math.Max(1, (int)(size.Height * scaling))), new Vector(96 * scaling, 96 * scaling));
            picture.Render(part);

            _ghost = new Window
            {
                Title = "N.E.O. tear",
                WindowDecorations = WindowDecorations.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                CanResize = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                Opacity = 0.85,
                Background = Brushes.Transparent,
                TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
                Content = new Border
                {
                    BorderBrush = Brush.Parse("#7B1FA2"),
                    BorderThickness = new Thickness(2),
                    BoxShadow = BoxShadows.Parse("0 6 18 0 #60000000"),
                    Child = new Image { Source = picture, Width = size.Width, Height = size.Height, Stretch = Stretch.Fill },
                },
                IsHitTestVisible = false,
            };
            _ghost.Show();
        }

        private void MoveGhost(PointerEventArgs e)
        {
            if (_ghost == null) return;
            var screen = _window.PointToScreen(e.GetPosition(_window));
            var scaling = _window.RenderScaling;
            _ghost.Position = new PixelPoint(screen.X - (int)(_grab.X * scaling), screen.Y - (int)(_grab.Y * scaling));
        }

        private bool Inside(PixelPoint screen)
        {
            var size = _window.FrameSize ?? _window.ClientSize;
            var scaling = _window.RenderScaling;
            var position = _window.Position;
            return screen.X >= position.X && screen.Y >= position.Y
                && screen.X < position.X + size.Width * scaling && screen.Y < position.Y + size.Height * scaling;
        }
    }
}
