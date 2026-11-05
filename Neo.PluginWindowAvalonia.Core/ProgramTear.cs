using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

using Neo.Shared;

namespace Neo.PluginWindowAvalonia
{
    /// <summary>
    /// Fission out of another program. This app is the interface of a program; with Alt held over that program's window,
    /// the part under the pointer stands out in a window of N.E.O.'s own that lies over the part and takes the press, so
    /// that the program never gets it. Dragged out of the program's window and let go, the part becomes a small program of
    /// its own that uses the program through this interface. The mouse wheel goes to smaller parts and back, as in an app.
    /// </summary>
    internal sealed class ProgramTear
    {
        private const double StartAfter = 8;  // device-independent pixels before a press is a drag
        private const double PillRoom = 44;   // above the part, for what it says

        private readonly Func<bool> _canTear;
        private readonly Action<string, IntPtr, ForeignWindows.ForeignPart, PixelPoint> _tornOut;

        private string? _program;
        private IntPtr _programWindow;
        private DateTime _lookedForWindow;
        private DispatcherTimer? _timer;
        private Window? _frame, _ghost;
        private TextBlock _say = null!;
        private Border _pill = null!, _box = null!;
        private List<ForeignWindows.ForeignPart> _parts = [];
        private int _level; // 0: the biggest part under the pointer
        private ForeignWindows.ForeignPart? _shown;
        private bool _looking, _pressed, _dragging, _masked;
        private (int X, int Y) _lookedAt = (int.MinValue, int.MinValue);
        private PixelPoint _pressedAt, _grab;

        /// <param name="tornOut">The program, its window, the part, and where on the screen it was let go.</param>
        public ProgramTear(Func<bool> canTear, Action<string, IntPtr, ForeignWindows.ForeignPart, PixelPoint> tornOut)
        {
            _canTear = canTear;
            _tornOut = tornOut;
        }

        /// <summary>This app is (now) the interface of <paramref name="program"/>, or of none (null). On the UI thread.</summary>
        public void Follow(string? program)
        {
            if (!OperatingSystem.IsWindows() || program == _program) return;
            _program = program;
            _programWindow = IntPtr.Zero;
            Stop();
            _timer?.Stop();
            _timer = null;
            if (program == null) return;

            // The program has the keyboard, not this app: Alt is read where it is.
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        private static bool AltHeld() => (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

        private void Tick()
        {
            if (_pressed) return;
            var alt = AltHeld();
            if (!alt) _masked = false;
            if (!alt || !_canTear() || !GetCursorPos(out var cursor))
            {
                Stop();
                return;
            }
            if ((_programWindow == IntPtr.Zero || !IsWindow(_programWindow)) && DateTime.UtcNow - _lookedForWindow > TimeSpan.FromSeconds(1) && _program is { } program)
            {
                // It walks every window on the screen: not on the UI thread either.
                _lookedForWindow = DateTime.UtcNow;
                _ = Task.Run(() => ForeignWindows.Find(program)?.Handle ?? IntPtr.Zero)
                    .ContinueWith(found => Dispatcher.UIThread.Post(() => { if (program == _program && found.IsCompletedSuccessfully) _programWindow = found.Result; }));
            }
            var under = GetAncestor(WindowFromPoint(cursor), GA_ROOT);
            if (_programWindow == IntPtr.Zero || under != _programWindow && under != HandleOf(_frame))
            {
                Stop();
                return;
            }
            if ((cursor.X, cursor.Y) != _lookedAt) Look(cursor.X, cursor.Y);
        }

        // The other program answers slowly or not at all: never on the UI thread.
        private void Look(int x, int y)
        {
            if (_looking) return;
            _looking = true;
            var window = _programWindow;
            Task.Run(() => ForeignWindows.PartsAt(window, x, y)).ContinueWith(found => Dispatcher.UIThread.Post(() =>
            {
                _looking = false;
                _lookedAt = (x, y);
                if (_pressed) return;
                _parts = found.IsCompletedSuccessfully ? found.Result : [];
                if (!AltHeld())
                {
                    Stop();
                    return;
                }
                Show(_parts.Count == 0 ? null : _parts[Math.Min(_level, _parts.Count - 1)]);
            }));
        }

        private void Stop()
        {
            if (_pressed) return;
            _level = 0;
            _shown = null;
            _parts = [];
            _lookedAt = (int.MinValue, int.MinValue);
            _frame?.Hide();
        }

        private void Show(ForeignWindows.ForeignPart? part)
        {
            _shown = part;
            if (part == null)
            {
                _frame?.Hide();
                return;
            }
            // Alt is N.E.O.'s now: a key of no meaning while it is held, so that letting it go opens neither the program's
            // menu nor the key tips of its ribbon.
            if (!_masked)
            {
                _masked = true;
                keybd_event(VK_NO_MEANING, 0, 0, UIntPtr.Zero);
                keybd_event(VK_NO_MEANING, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }

            var frame = _frame ??= CreateFrame();
            var scaling = frame.Screens.ScreenFromPoint(new PixelPoint(part.Left, part.Top))?.Scaling ?? 1;
            var room = (int)Math.Round(PillRoom * scaling);
            _say.Text = $"Drag it out with Alt: a small program that uses {ShownName()}";
            frame.Position = new PixelPoint(part.Left, part.Top - room);
            // What it says may be wider than a narrow part: the window then reaches beyond the part, the box does not.
            _pill.Measure(Size.Infinity);
            _box.Width = part.Width / scaling;
            frame.Width = Math.Max(_box.Width, _pill.DesiredSize.Width + _pill.Margin.Left + 2);
            frame.Height = (part.Height + room) / scaling;
            if (!frame.IsVisible) frame.Show();

            if (HandleOf(frame) is var own && own != IntPtr.Zero)
            {
                // Not a window that takes the keyboard; just above the program, under whatever lies above it.
                SetWindowLongPtr(own, GWL_EXSTYLE, GetWindowLongPtr(own, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                var above = GetWindow(_programWindow, GW_HWNDPREV);
                SetWindowPos(own, above == IntPtr.Zero || above == own ? HWND_TOP : above, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }

        // The program as its title bar names it ("Word", not "WINWORD").
        private string ShownName() => ForeignWindows.ProgramPart(ForeignWindows.TitleOf(_programWindow)) is { Length: > 0 } title ? title : _program ?? "";

        private Window CreateFrame()
        {
            _say = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
            _pill = new Border
            {
                Padding = new Thickness(10, 4), CornerRadius = new CornerRadius(10), Background = Brush.Parse("#E07B1FA2"),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 0, 2),
                Child = new StackPanel { Children = { _say, new TextBlock { Text = "Mouse wheel: a bigger or a smaller part", Foreground = Brush.Parse("#D0FFFFFF"), FontSize = 11 } } },
            };
            // Not quite transparent: the press over the part has to land here, not in the program.
            _box = new Border
            {
                BorderBrush = Brush.Parse("#7B1FA2"), BorderThickness = new Thickness(3), Background = Brush.Parse("#207B1FA2"), CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var layout = new Grid { RowDefinitions = new RowDefinitions($"{PillRoom},*") };
            layout.Children.Add(_pill);
            Grid.SetRow(_box, 1);
            layout.Children.Add(_box);

            var frame = new Window
            {
                Title = "N.E.O. tear frame",
                WindowDecorations = WindowDecorations.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                CanResize = false,
                Background = Brushes.Transparent,
                TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
                Content = layout,
            };
            frame.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            frame.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            frame.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            frame.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
            return frame;
        }

        private void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_frame == null || _shown is not { } part || !e.GetCurrentPoint(_frame).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            // Alt let go a moment ago: the frame goes, and the press with it.
            if (!AltHeld())
            {
                Stop();
                return;
            }
            _pressed = true;
            _pressedAt = _frame.PointToScreen(e.GetPosition(_frame));
            _grab = new PixelPoint(_pressedAt.X - part.Left, _pressedAt.Y - part.Top);
            e.Pointer.Capture(_frame);
        }

        private void OnMoved(object? sender, PointerEventArgs e)
        {
            if (!_pressed || _frame == null) return;
            var at = _frame.PointToScreen(e.GetPosition(_frame));
            var moved = Math.Sqrt(Math.Pow(at.X - _pressedAt.X, 2) + Math.Pow(at.Y - _pressedAt.Y, 2));
            if (!_dragging && moved > StartAfter * _frame.RenderScaling) StartDrag();
            if (_ghost != null) _ghost.Position = new PixelPoint(at.X - _grab.X, at.Y - _grab.Y);
            e.Handled = true;
        }

        private void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_pressed || _frame == null) return;
            e.Handled = true;
            e.Pointer.Capture(null);
            var at = _frame.PointToScreen(e.GetPosition(_frame));
            var dragged = _dragging;
            var part = _shown;
            _pressed = _dragging = false;
            _ghost?.Close();
            _ghost = null;
            Stop();
            if (!dragged || part == null || _program == null) return;

            // Let go over the program, the person changed their mind.
            if (GetWindowRect(_programWindow, out var window) && at.X >= window.Left && at.X < window.Right && at.Y >= window.Top && at.Y < window.Bottom) return;
            _tornOut(_program, _programWindow, part, at);
        }

        // With Alt held, the wheel turned towards you takes a smaller part, away from you a bigger one.
        private void OnWheel(object? sender, PointerWheelEventArgs e)
        {
            if (_pressed || _parts.Count == 0 || e.Delta.Y == 0) return;
            _level = Math.Clamp(Math.Min(_level, _parts.Count - 1) + (e.Delta.Y > 0 ? -1 : 1), 0, _parts.Count - 1);
            Show(_parts[_level]);
            e.Handled = true;
        }

        // What is being torn out goes along with the pointer, as a picture of itself.
        private void StartDrag()
        {
            if (_shown is not { } part || _frame == null) return;
            _dragging = true;
            var scaling = _frame.RenderScaling;
            var size = new Size(part.Width / scaling, part.Height / scaling);
            Control picture = PictureOf(_programWindow, part) is { } bitmap
                ? new Image { Source = bitmap, Width = size.Width, Height = size.Height, Stretch = Stretch.Fill }
                : new Border { Width = size.Width, Height = size.Height, Background = Brush.Parse("#307B1FA2") };
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
                Content = new Border { BorderBrush = Brush.Parse("#7B1FA2"), BorderThickness = new Thickness(2), BoxShadow = BoxShadows.Parse("0 6 18 0 #60000000"), Child = picture },
                IsHitTestVisible = false,
            };
            _ghost.Show();
        }

        // The program draws itself into a bitmap (without the frame over it); the part is cut out of that.
        private static Bitmap? PictureOf(IntPtr window, ForeignWindows.ForeignPart part)
        {
            if (!GetWindowRect(window, out var whole) || whole.Right <= whole.Left || whole.Bottom <= whole.Top) return null;
            var screen = GetDC(IntPtr.Zero);
            var all = CreateCompatibleDC(screen);
            var allBitmap = CreateCompatibleBitmap(screen, whole.Right - whole.Left, whole.Bottom - whole.Top);
            var allBefore = SelectObject(all, allBitmap);
            var cut = CreateCompatibleDC(screen);
            var header = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = part.Width, Height = -part.Height, Planes = 1, BitCount = 32 };
            var cutBitmap = CreateDIBSection(cut, ref header, 0, out var bits, IntPtr.Zero, 0);
            var cutBefore = SelectObject(cut, cutBitmap);
            try
            {
                if (cutBitmap == IntPtr.Zero || !PrintWindow(window, all, PW_RENDERFULLCONTENT)) return null;
                BitBlt(cut, 0, 0, part.Width, part.Height, all, part.Left - whole.Left, part.Top - whole.Top, SRCCOPY);
                GdiFlush();
                var picture = new WriteableBitmap(new PixelSize(part.Width, part.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                using var target = picture.Lock();
                var row = new byte[part.Width * 4];
                for (int y = 0; y < part.Height; y++)
                {
                    Marshal.Copy(bits + y * row.Length, row, 0, row.Length);
                    Marshal.Copy(row, 0, target.Address + y * target.RowBytes, row.Length);
                }
                return picture;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ExternalException)
            {
                return null;
            }
            finally
            {
                SelectObject(cut, cutBefore);
                DeleteObject(cutBitmap);
                DeleteDC(cut);
                SelectObject(all, allBefore);
                DeleteObject(allBitmap);
                DeleteDC(all);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private static IntPtr HandleOf(Window? window) => window?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

        [StructLayout(LayoutKind.Sequential)] private struct Point32 { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct Rect32 { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        }

        private const int VK_MENU = 0x12, GWL_EXSTYLE = -20, PW_RENDERFULLCONTENT = 2, SRCCOPY = 0x00CC0020;
        private const byte VK_NO_MEANING = 0xE8; // unassigned, the usual key to keep a lone Alt from opening a menu
        private const uint GA_ROOT = 2, GW_HWNDPREV = 3, SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, KEYEVENTF_KEYUP = 0x2;
        private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point32 point);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point32 point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect32 rect);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern long SetWindowLongPtr(IntPtr window, int index, long value);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, int flags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int operation);
        [DllImport("gdi32.dll")] private static extern bool GdiFlush();
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr gdiObject);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    }
}
