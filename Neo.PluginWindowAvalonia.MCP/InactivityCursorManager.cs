#define WINDOWS

using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Neo.PluginWindowAvalonia.MCP
{
    public static class InactivityCursorManager
    {
#if WINDOWS
        // --- WIN32 API IMPORTE NUR UNTER WINDOWS ---
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }
#endif

        private const double InactivityThresholdSeconds = 3.0;
        private const int PollingIntervalMilliseconds = 250;

        private static DispatcherTimer? _pollingTimer;
#if WINDOWS
        private static POINT _lastMousePosition;
#endif
        private static DateTime _lastActivityTime;
        private static bool _isCursorHidden;
        private static bool _isRunning;

        // Das Fenster (oder UserControl.TopLevel), dessen Cursor wir steuern
        private static TopLevel? _targetTopLevel;

        public static void Start(TopLevel targetTopLevel)
        {
            if (_isRunning) return;

            _targetTopLevel = targetTopLevel;

#if WINDOWS
            GetCursorPos(out _lastMousePosition);
#endif
            _lastActivityTime = DateTime.UtcNow;
            _isCursorHidden = false;

            _pollingTimer = new DispatcherTimer
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
            if (_pollingTimer != null)
            {
                _pollingTimer.Tick -= OnPollingTick;
            }
            _pollingTimer = null;

            if (_isCursorHidden)
            {
                RestoreOriginalCursor();
            }

            _isRunning = false;
            _targetTopLevel = null;
        }

        private static void OnPollingTick(object? sender, EventArgs e)
        {
            if (_targetTopLevel == null)
                return;

#if WINDOWS
            // Unter Windows: globale Mausposition
            if (!GetCursorPos(out POINT currentPos))
                return;

            if (currentPos.X != _lastMousePosition.X || currentPos.Y != _lastMousePosition.Y)
            {
                _lastActivityTime = DateTime.UtcNow;
                _lastMousePosition = currentPos;

                if (_isCursorHidden)
                {
                    RestoreOriginalCursor();
                    _isCursorHidden = false;
                }
                return;
            }
#else
            // Unter Linux/macOS könntest du hier z. B. Pointer-Bewegungen
            // über Events registrieren und nur den Timer für "Zeit seit letzter Aktivität" nutzen.
            // Ohne globalen Maus-API kannst du hier auch einfach nur prüfen,
            // ob schon lange keine Aktivität registriert wurde.
#endif
            // Wenn wir hier sind: keine neue Aktivität erkannt
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

        public static void NotifyActivity()
        {
            // Falls du z. B. PointerMoved-Events des Fensters direkt an diese Methode leitest:
            _lastActivityTime = DateTime.UtcNow;

            if (_isCursorHidden)
            {
                RestoreOriginalCursor();
                _isCursorHidden = false;
            }
        }

        private static void SetInvisibleCursor()
        {
            if (_targetTopLevel == null)
                return;

            _targetTopLevel.Cursor = new Cursor(StandardCursorType.None);
        }

        private static void RestoreOriginalCursor()
        {
            if (_targetTopLevel == null)
                return;

            _targetTopLevel.Cursor = new Cursor(StandardCursorType.Arrow);
            // oder: _targetTopLevel.Cursor = null; // Default-Systemcursor
        }
    }
}
