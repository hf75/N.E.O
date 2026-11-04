using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

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
    /// <para>The window of another program counts as the window of its interface: a drop on it goes to the app that is
    /// its interface. And when this app is the interface of a program, dragging that program's window links it too:
    /// it cannot be merged into anything, its code is not here.</para>
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
        private IntPtr _overProgram; // the program's window, when _over is its interface and the pointer is over that
        private DateTime _overSince;
        private volatile string? _takesMerges; // the app that answered: not every program on the network takes one
        private System.Threading.Tasks.Task _lastHover = System.Threading.Tasks.Task.CompletedTask;
        private bool _moving;
        private PixelPoint _startedAt;

        // The other program this app is the interface of: then its window may be the one dragged.
        private string? _program;
        private IntPtr _programWindow;
        private IntPtr _hook;
        private WinEventProc? _onWinEvent; // kept alive as long as the hook
        private DispatcherTimer? _programTimer;
        private bool _draggingProgram;
        private Rect _programStartedAt;
        private readonly Dictionary<uint, string> _processNames = new();

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
                    if (_moving && _canMerge()) Start(draggingProgram: false);
                    break;
                case WM_EXITSIZEMOVE:
                    Finish();
                    break;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// This app is (now) the interface of <paramref name="program"/>, or of none (null): from now on a drag of that
        /// program's window counts as a drag of this app. On the UI thread.
        /// </summary>
        public void FollowProgram(string? program)
        {
            if (!OperatingSystem.IsWindows() || program == _program) return;
            _program = program;
            Unhook();
            if (program == null)
            {
                _programTimer?.Stop();
                _programTimer = null;
                return;
            }

            // The program may open later, or open again: look for its window now and then.
            HookProgram();
            if (_programTimer != null) return;
            _programTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _programTimer.Tick += (_, _) => { if (_programWindow == IntPtr.Zero || !IsWindow(_programWindow)) HookProgram(); };
            _programTimer.Start();
        }

        private void HookProgram()
        {
            Unhook();
            if (_program == null || Neo.Shared.ForeignWindows.Find(_program) is not { } found) return;
            _programWindow = found.Handle;
            _onWinEvent = OnWinEvent;
            // Out of context: the other program loads nothing of N.E.O. The UI thread pumps the messages the events come with.
            _hook = SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _onWinEvent, (uint)found.ProcessId, 0, WINEVENT_OUTOFCONTEXT);
        }

        private void Unhook()
        {
            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            _programWindow = IntPtr.Zero;
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
        {
            if (window != _programWindow || objectId != 0) return;
            if (eventType == EVENT_SYSTEM_MOVESIZESTART && AppNetwork.Published?.Program != null)
            {
                GetWindowRect(window, out _programStartedAt);
                Start(draggingProgram: true);
            }
            else if (eventType == EVENT_SYSTEM_MOVESIZEEND && _draggingProgram)
                Finish();
        }

        private void Start(bool draggingProgram)
        {
            try { _apps = Apps.Running(); }
            catch (Exception) { _apps = []; }
            if (_apps.Count == 0) return;

            _draggingProgram = draggingProgram;
            _startedAt = _window.Position;
            _timer?.Stop();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        private IntPtr Dragged => _draggingProgram ? _programWindow : _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

        private void Tick()
        {
            var linkHalf = false;
            var overProgram = IntPtr.Zero;
            // A program window whose size changes is being resized, not moved.
            var resizing = _draggingProgram && GetWindowRect(_programWindow, out var now) && (now.Width != _programStartedAt.Width || now.Height != _programStartedAt.Height);
            var under = !resizing && GetCursorPos(out var cursor) ? AppUnder(cursor, out linkHalf, out overProgram) : null;
            if (under == null) linkHalf = false;
            if (_draggingProgram) linkHalf = true;
            if (under?.Id == _over?.Id && overProgram == _overProgram)
            {
                if (under != null && linkHalf != _overLinkHalf) Hover(under, true, linkHalf, overProgram);
                _overLinkHalf = linkHalf;
                return;
            }

            Hover(_over, false, false, IntPtr.Zero);
            Hover(under, true, linkHalf, overProgram);
            _over = under;
            _overLinkHalf = linkHalf;
            _overProgram = overProgram;
            _overSince = DateTime.UtcNow;
        }

        private void Finish()
        {
            if (_timer == null) return;
            _timer.Stop();
            _timer = null;

            var target = _over;
            var link = _overLinkHalf;
            var draggedProgram = _draggingProgram;
            _over = null;
            _overProgram = IntPtr.Zero;
            _draggingProgram = false;
            Hover(target, false, false, IntPtr.Zero);

            // Escape puts the window back where it was: the user changed their mind.
            if (target == null) return;
            if (draggedProgram ? !GetWindowRect(_programWindow, out var end) || (end.Left == _programStartedAt.Left && end.Top == _programStartedAt.Top) : _window.Position == _startedAt) return;
            if (DateTime.UtcNow - _overSince < MinHover) return;

            // A program linked with another goes back to where it was, like a linked app.
            if (draggedProgram)
                SetWindowPos(_programWindow, IntPtr.Zero, _programStartedAt.Left, _programStartedAt.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

            // The system is still in its loop here. And on a busy PC the other app may have shown the hint and its answer still be on the way.
            var cameFrom = draggedProgram ? _window.Position : _startedAt;
            var answer = _lastHover;
            Dispatcher.UIThread.Post(async () =>
            {
                await answer;
                if (_takesMerges == target.Id) _dropped(target, cameFrom, link || draggedProgram);
            });
        }

        // The first window a person sees below the dragged one under the pointer; another window in between is no target.
        // What lies above it, while it is being dragged, are overlays that stay on top of everything.
        private AppDescriptor? AppUnder(Point cursor, out bool linkHalf, out IntPtr overProgram)
        {
            var dragged = Dragged;
            var self = (uint)Environment.ProcessId;
            var passedDragged = false;
            var found = IntPtr.Zero;
            var middle = 0;
            EnumWindows((window, _) =>
            {
                if (window == dragged) { passedDragged = true; return true; }
                if (!passedDragged || !IsWindowVisible(window) || IsIconic(window) || GetWindowTextLength(window) == 0) return true;
                if ((GetWindowLongPtr(window, GWL_EXSTYLE) & (WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return true;
                if (DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                // This app's own window, beside the program it is the interface of, is no target.
                if (GetWindowThreadProcessId(window, out var owner) != 0 && owner == self) return true;
                if (!GetWindowRect(window, out var rect) || !rect.Contains(cursor)) return true;
                found = window;
                middle = (rect.Left + rect.Right) / 2;
                return false;
            }, IntPtr.Zero);

            linkHalf = cursor.X > middle;
            overProgram = IntPtr.Zero;
            if (found == IntPtr.Zero) return null;
            GetWindowThreadProcessId(found, out var processId);
            if (_apps.FirstOrDefault(a => a.ProcessId == (int)processId) is { } app) return app;

            // The window of another program: its interface takes the drop.
            var serving = _apps.FirstOrDefault(a => a.Program != null && Serves(a.Program, processId, found));
            if (serving != null) overProgram = found;
            return serving;
        }

        // The way ForeignApp finds a program: its process name, a part of it, or a part of the window's title.
        private bool Serves(string program, uint processId, IntPtr window)
        {
            if (!_processNames.TryGetValue(processId, out var process))
            {
                try { using var p = Process.GetProcessById((int)processId); process = p.ProcessName; }
                catch (Exception) { process = ""; }
                _processNames[processId] = process;
            }
            if (process.Contains(program, StringComparison.OrdinalIgnoreCase)) return true;
            var title = new StringBuilder(GetWindowTextLength(window) + 1);
            GetWindowText(window, title, title.Capacity);
            return title.ToString().Contains(program, StringComparison.OrdinalIgnoreCase);
        }

        private void Hover(AppDescriptor? app, bool on, bool linkHalf, IntPtr overProgram)
        {
            if (app == null) return;
            var payload = Json.ToJson(new FusionHoverMessage(on ? DisplayName() : null, linkHalf, on ? overProgram.ToInt64() : 0, _draggingProgram));
            var sending = SendHoverAsync(app, payload, on);
            if (on) _lastHover = sending;
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
            public int Width => Right - Left;
            public int Height => Bottom - Top;
            public bool Contains(Point p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
        }

        private const uint WM_SYSCOMMAND = 0x0112, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;
        private const long SC_MOVE = 0xF010;
        private const int GWL_EXSTYLE = -20, DWMWA_CLOAKED = 14;
        private const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A, EVENT_SYSTEM_MOVESIZEEND = 0x000B, WINEVENT_OUTOFCONTEXT = 0;
        private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    }
}
