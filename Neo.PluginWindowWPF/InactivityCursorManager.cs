using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace Neo.PluginWindowWPF
{
    public static class InactivityCursorManager
    {
        // --- WIN32 API IMPORTE ---
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private const double InactivityThresholdSeconds = 3.0;
        private const int PollingIntervalMilliseconds = 250;

        private static DispatcherTimer? _pollingTimer;
        private static POINT _lastMousePosition;
        private static DateTime _lastActivityTime;
        private static bool _isCursorHidden;
        private static bool _isRunning;

        public static void Start()
        {
            if (_isRunning) return;

            GetCursorPos(out _lastMousePosition);
            _lastActivityTime = DateTime.UtcNow;
            _isCursorHidden = false;

            _pollingTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(PollingIntervalMilliseconds)
            };

            _pollingTimer.Tick += OnPollingTick;
            _pollingTimer.Start();
            _isRunning = true;
        }

        public static void Stop()
        {
            if (!_isRunning) return;

            _pollingTimer?.Stop();
            _pollingTimer = null!;

            if (_isCursorHidden)
            {
                RestoreOriginalCursor();
            }

            _isRunning = false;
        }

        private static void OnPollingTick(object? sender, EventArgs e)
        {
            GetCursorPos(out POINT currentPos);

            if (currentPos.X != _lastMousePosition.X || currentPos.Y != _lastMousePosition.Y)
            {
                _lastActivityTime = DateTime.UtcNow;
                _lastMousePosition = currentPos;

                if (_isCursorHidden)
                {
                    RestoreOriginalCursor();
                    _isCursorHidden = false;
                }
            }
            else
            {
                if (!_isCursorHidden)
                {
                    var elapsed = DateTime.UtcNow - _lastActivityTime;
                    if (elapsed > TimeSpan.FromSeconds(InactivityThresholdSeconds))
                    {
                        SetInvisibleCursor();
                        _isCursorHidden = true;
                    }
                }
            }
        }

        private static void SetInvisibleCursor()
        {
            Mouse.OverrideCursor = Cursors.None;
        }

        private static void RestoreOriginalCursor()
        {
            Mouse.OverrideCursor = Cursors.Arrow;
        }
    }
}
