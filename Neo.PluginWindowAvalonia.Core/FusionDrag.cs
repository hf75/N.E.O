using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Neo.App;
using Neo.IPC;

namespace Neo.PluginWindowAvalonia
{
    /// <summary>
    /// Notices that this window is dragged over the window of another N.E.O. app: that app then shows what a
    /// drop would do. Letting go on its left half merges the two, on its right half links them. Windows only:
    /// there the system moves a window in a loop of its own, and says when that starts and ends.
    /// </summary>
    internal sealed class FusionDrag
    {
        // Passing over an app on the way somewhere else is no drop.
        private static readonly TimeSpan MinHover = TimeSpan.FromMilliseconds(300);

        private readonly MainWindow _window;
        private readonly Func<bool> _canMerge;
        private readonly Action<AppDescriptor, PixelPoint, bool> _dropped;

        private DispatcherTimer? _timer;
        private IReadOnlyList<AppDescriptor> _apps = [];
        private AppDescriptor? _over;
        private bool _overLinkHalf;
        private DateTime _overSince;
        private volatile string? _takesMerges; // the app that answered: not every program on the network takes one
        private bool _moving;
        private PixelPoint _startedAt;

        /// <param name="dropped">The app it was dropped on, where this window was before the drag, and whether to link instead of merge.</param>
        public FusionDrag(MainWindow window, Func<bool> canMerge, Action<AppDescriptor, PixelPoint, bool> dropped)
        {
            _window = window;
            _canMerge = canMerge;
            _dropped = dropped;

            if (OperatingSystem.IsWindows())
                Win32Properties.AddWndProcHookCallback(window, OnWindowMessage);
        }

        // Moving and resizing are the same loop of the system; the command that starts it tells them apart.
        private IntPtr OnWindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (message)
            {
                case WM_SYSCOMMAND:
                    _moving = (wParam.ToInt64() & 0xFFF0) == SC_MOVE;
                    break;
                case WM_ENTERSIZEMOVE:
                    if (_moving && _canMerge()) Start();
                    break;
                case WM_EXITSIZEMOVE:
                    Finish();
                    break;
            }
            return IntPtr.Zero;
        }

        private void Start()
        {
            try { _apps = Apps.Running(); }
            catch (Exception) { _apps = []; }
            if (_apps.Count == 0) return;

            _startedAt = _window.Position;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        private void Tick()
        {
            var linkHalf = false;
            var under = GetCursorPos(out var cursor) ? AppUnder(cursor, out linkHalf) : null;
            if (under == null) linkHalf = false;
            if (under?.Id == _over?.Id)
            {
                if (under != null && linkHalf != _overLinkHalf) Hover(under, true, linkHalf);
                _overLinkHalf = linkHalf;
                return;
            }

            Hover(_over, false, false);
            Hover(under, true, linkHalf);
            _over = under;
            _overLinkHalf = linkHalf;
            _overSince = DateTime.UtcNow;
        }

        private void Finish()
        {
            if (_timer == null) return;
            _timer.Stop();
            _timer = null;

            var target = _over;
            var link = _overLinkHalf;
            _over = null;
            Hover(target, false, false);

            // Escape puts the window back where it was: the user changed their mind.
            if (target == null || _window.Position == _startedAt) return;
            if (DateTime.UtcNow - _overSince < MinHover || _takesMerges != target.Id) return;

            // The system is still in its loop here.
            var cameFrom = _startedAt;
            Dispatcher.UIThread.Post(() => _dropped(target, cameFrom, link));
        }

        // The first window a person sees below this one under the pointer; another window in between is no target.
        // What lies above this one, while it is being dragged, are overlays that stay on top of everything.
        private AppDescriptor? AppUnder(Point cursor, out bool linkHalf)
        {
            var own = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            var passedOwn = false;
            var found = IntPtr.Zero;
            var middle = 0;
            EnumWindows((window, _) =>
            {
                if (window == own) { passedOwn = true; return true; }
                if (!passedOwn || !IsWindowVisible(window) || IsIconic(window) || GetWindowTextLength(window) == 0) return true;
                if ((GetWindowLongPtr(window, GWL_EXSTYLE) & (WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return true;
                if (DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                if (!GetWindowRect(window, out var rect) || !rect.Contains(cursor)) return true;
                found = window;
                middle = (rect.Left + rect.Right) / 2;
                return false;
            }, IntPtr.Zero);

            linkHalf = cursor.X > middle;
            if (found == IntPtr.Zero) return null;
            GetWindowThreadProcessId(found, out var processId);
            return _apps.FirstOrDefault(a => a.ProcessId == (int)processId);
        }

        private void Hover(AppDescriptor? app, bool on, bool linkHalf)
        {
            if (app == null) return;
            var payload = Json.ToJson(new FusionHoverMessage(on ? DisplayName() : null, linkHalf));
            _ = SendHoverAsync(app, payload, on);
        }

        private async System.Threading.Tasks.Task SendHoverAsync(AppDescriptor app, string payload, bool on)
        {
            try
            {
                await AppNetwork.SendSystemAsync(app, FusionMessages.Hover, payload, TimeSpan.FromSeconds(2));
                if (on) _takesMerges = app.Id;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Fusion] " + ex.Message); }
        }

        private static string DisplayName() => AppNetwork.Published?.Name is { Length: > 0 } name && !name.StartsWith("N.E.O.", StringComparison.Ordinal) ? name : "this program";

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct Point(int X, int Y);

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct Rect(int Left, int Top, int Right, int Bottom)
        {
            public bool Contains(Point p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
        }

        private const uint WM_SYSCOMMAND = 0x0112, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;
        private const long SC_MOVE = 0xF010;
        private const int GWL_EXSTYLE = -20, DWMWA_CLOAKED = 14;
        private const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr(IntPtr window, int index);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    }
}
