using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Neo.App
{
    /// <summary>
    /// Eine statische Klasse zur Verwaltung des Vollbildmodus für WPF-Fenster.
    /// Diese Methode ist robust und funktioniert auch mit komplexen Inhalten wie WebView2.
    /// </summary>
    public static class FullScreenManager
    {
        // Ein Dictionary, um den ursprünglichen Zustand für jedes Fenster zu speichern.
        private static readonly Dictionary<MainWindow, WindowStateInfo> _originalStates = new Dictionary<MainWindow, WindowStateInfo>();

        /// <summary>
        /// Schaltet den Vollbildmodus für das angegebene Fenster um.
        /// </summary>
        /// <param name="window">Das Fenster, dessen Vollbildmodus umgeschaltet werden soll.</param>
        public static void ToggleFullScreen(MainWindow window)
        {
            if (IsInFullScreen(window))
            {
                ExitFullScreen(window);
            }
            else
            {
                EnterFullScreen(window);
            }
        }

        /// <summary>
        /// Aktiviert den Vollbildmodus für das angegebene Fenster.
        /// </summary>
        /// <param name="window">Das Fenster, das in den Vollbildmodus versetzt werden soll.</param>
        public static void EnterFullScreen(MainWindow window)
        {
            if (window == null || _originalStates.ContainsKey(window))
            {
                return; // Fenster ist bereits im Vollbildmodus oder ungültig
            }

            // Speichere den aktuellen Zustand des Fensters
            var stateInfo = new WindowStateInfo
            {
                WindowStyle = window.WindowStyle,
                WindowState = window.WindowState,
                ResizeMode = window.ResizeMode
            };
            _originalStates.Add(window, stateInfo);

            // Gehe in den Vollbildmodus
            window.WindowStyle = WindowStyle.None;
            // Wichtig: Zuerst den Style ändern, dann maximieren.
            // Wenn das Fenster bereits maximiert war, muss es eventuell kurz auf "Normal" gesetzt werden,
            // damit die Änderung des WindowStyle korrekt übernommen wird.
            if (window.WindowState == WindowState.Maximized)
            {
                window.WindowState = WindowState.Normal;
            }
            window.WindowState = WindowState.Maximized;
            window.ResizeMode = ResizeMode.NoResize;

            // InactivityCursorManager.Start();
        }

        /// <summary>
        /// Beendet den Vollbildmodus für das angegebene Fenster.
        /// </summary>
        /// <param name="window">Das Fenster, dessen Vollbildmodus beendet werden soll.</param>
        public static void ExitFullScreen(MainWindow window)
        {
            if (window == null || !_originalStates.TryGetValue(window, out var stateInfo))
            {
                return; // Fenster ist nicht im Vollbildmodus oder ungültig
            }

            //InactivityCursorManager.Stop();

            // Stelle den ursprünglichen Zustand wieder her
            window.WindowStyle = stateInfo.WindowStyle;
            window.WindowState = stateInfo.WindowState;
            window.ResizeMode = stateInfo.ResizeMode;

            // Entferne den Eintrag aus dem Dictionary
            _originalStates.Remove(window);

            window.Cursor = System.Windows.Input.Cursors.Arrow;
        }

        /// <summary>
        /// Überprüft, ob sich ein Fenster aktuell im Vollbildmodus befindet.
        /// </summary>
        /// <param name="window">Das zu überprüfende Fenster.</param>
        /// <returns>True, wenn das Fenster im Vollbildmodus ist, andernfalls False.</returns>
        public static bool IsInFullScreen(MainWindow window)
        {
            return window != null && _originalStates.ContainsKey(window);
        }

        /// <summary>
        /// Eine private Klasse zur Speicherung des Fensterzustands.
        /// </summary>
        private class WindowStateInfo
        {
            public WindowStyle WindowStyle { get; set; }
            public WindowState WindowState { get; set; }
            public ResizeMode ResizeMode { get; set; }
        }
    }

    public static class InactivityCursorManager
    {
        // --- WIN32 API IMPORTE ---
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetSystemCursor(IntPtr hcur, uint id);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, uint lpCursorName);

        [DllImport("user32.dll")]
        private static extern IntPtr CopyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private const uint OCR_NORMAL = 32512;
        private const uint IDC_ARROW = 32512;

        private const double InactivityThresholdSeconds = 3.0;
        private const int PollingIntervalMilliseconds = 250;

        private static DispatcherTimer? _pollingTimer;
        private static POINT _lastMousePosition;
        private static DateTime _lastActivityTime;
        private static bool _isCursorHidden;
        private static bool _isRunning;

        // Diese Handles sind unsere "Master-Vorlagen". Wir geben sie NIE direkt an SetSystemCursor.
        private static IntPtr _masterInvisibleCursorHandle = IntPtr.Zero;
        private static IntPtr _masterOriginalCursorHandle = IntPtr.Zero;

        public static void Start()
        {
            if (_isRunning) return;

            // Lade die Master-Vorlage für den unsichtbaren Cursor
            if (_masterInvisibleCursorHandle == IntPtr.Zero)
            {
                try
                {
                    var streamInfo = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Cursors/inv_cursor.cur"));
                    using (var stream = streamInfo.Stream)
                    {
                        // Wichtig: WinForms-Verweis muss im Projekt vorhanden sein!
                        var cursor = new System.Windows.Forms.Cursor(stream);
                        _masterInvisibleCursorHandle = cursor.Handle;
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show("Failed to load invisible cursor: " + ex.Message);
                    return;
                }
            }

            // Lade die Master-Vorlage für den originalen Cursor
            if (_masterOriginalCursorHandle == IntPtr.Zero)
            {
                IntPtr loadedHandle = LoadCursor(IntPtr.Zero, IDC_ARROW);
                _masterOriginalCursorHandle = CopyIcon(loadedHandle);
            }

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
            _pollingTimer = null;

            if (_isCursorHidden)
            {
                RestoreOriginalCursor();
            }

            // Gib unsere Master-Vorlage für den Original-Cursor frei
            if (_masterOriginalCursorHandle != IntPtr.Zero)
            {
                DestroyIcon(_masterOriginalCursorHandle);
                _masterOriginalCursorHandle = IntPtr.Zero;
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
                        // Erstelle eine KOPIE des unsichtbaren Cursors und übergib sie.
                        // Das System wird diese Kopie zerstören, unsere Master-Vorlage bleibt unberührt.
                        IntPtr invisibleCopy = CopyIcon(_masterInvisibleCursorHandle);
                        SetSystemCursor(invisibleCopy, OCR_NORMAL);
                        _isCursorHidden = true;
                    }
                }
            }
        }

        private static void RestoreOriginalCursor()
        {
            if (_masterOriginalCursorHandle != IntPtr.Zero)
            {
                // Erstelle eine KOPIE des originalen Cursors und übergib sie.
                // Das System wird diese Kopie zerstören, unsere Master-Vorlage bleibt unberührt.
                IntPtr originalCopy = CopyIcon(_masterOriginalCursorHandle);
                SetSystemCursor(originalCopy, OCR_NORMAL);
            }
        }
    }
}
