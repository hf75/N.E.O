using Neo.IPC;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using static Neo.App.NativeMethods;
using System.Windows.Interop;
using System.IO;
using System.Reflection.Metadata;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;

namespace Neo.App
{
    public interface IChildProcessService : IAsyncDisposable
    {
        /// <summary>
        /// Startet den Child-Prozess neu. Nützlich für Konfigurationsänderungen (z.B. Sandbox-Modus).
        /// </summary>
        Task RestartAsync();

        /// <summary>
        /// Aktualisiert die Position und Größe des Child-Fensters, um es an den Host-Container anzupassen.
        /// </summary>
        public void UpdatePosition(bool useTopMostTrick = false);

        /// <summary>
        /// Sendet die kompilierten DLLs an den Child-Prozess und weist ihn an, das UserControl zu laden.
        /// </summary>
        /// <param name="mainDllPath">Pfad zur Haupt-DLL mit dem UserControl.</param>
        /// <param name="nugetDlls">Liste der abhängigen NuGet-DLLs.</param>
        /// <param name="additionalDlls">Liste weiterer abhängiger DLLs.</param>
        Task<bool> DisplayControlAsync(string mainDllPath, IEnumerable<string> nugetDlls, IEnumerable<string> additionalDlls);

        /// <summary>
        /// Prüft, ob der aktuelle UI-Fokus innerhalb des Child-Fensters oder eines seiner untergeordneten Elemente liegt.
        /// </summary>
        bool IsFocusInsideChild();

        /// <summary>
        /// Konfiguriert die Sandbox-Einstellungen für den nächsten Start/Neustart des Child-Prozesses.
        /// </summary>
        void ConfigureSandbox(bool useSandbox, SandboxSettings settings);

        /// <summary>
        /// Konfiguriert die Crossplatform-Einstellungen für den nächsten Start/Neustart des Child-Prozesses.
        /// </summary>
        void ConfigureCrossplatformSettings(CrossplatformSettings settings);

        /// <summary>
        /// Informiert den Service über eine Änderung des Zustands des Elternfensters (z.B. minimiert).
        /// </summary>        
        void NotifyParentWindowStateChanged(WindowState newState);

        /// <summary>
        /// Teilt dem Child-Prozess mit, ob der Cursor sichtbar sein soll (für den Vollbildmodus).
        /// </summary>
        Task SetCursorVisibilityAsync(bool isVisible);

        /// <summary>
        /// Teilt dem Child-Prozess mit ob er Modal sein soll oder nicht.
        /// </summary>
        Task SetChildModalityAsync(bool isModal);

        /// <summary>
        /// Aktiviert/Deaktiviert den Click-to-Edit Designer Mode im Child.
        /// </summary>
        Task SetDesignerModeAsync(bool enabled);

        /// <summary>
        /// Weist den Child-Prozess an, das aktuelle UserControl zu entladen.
        /// Verhindert Exceptions vom alten Control während der Code-Generierung.
        /// </summary>
        Task UnloadControlAsync();

        /// <summary>
        /// Captures a screenshot of the child window and returns it as a frozen BitmapSource.
        /// Returns null if the child window is not available.
        /// </summary>
        System.Windows.Media.Imaging.BitmapSource? CaptureChildScreenshot();

        /// <summary>
        /// True if a control was successfully loaded via DisplayControlAsync.
        /// </summary>
        bool HasLoadedControl { get; }

        /// <summary>
        /// Child ausblenden
        /// </summary>
        void HideChild();

        /// <summary>
        /// Child einblenden
        /// </summary>
        void ShowChild();

        IntPtr GetChildHwnd();

        /// <summary>6
        /// Wird ausgelöst, wenn der Child-Prozess unerwartet abstürzt oder nicht mehr reagiert.
        /// Der Handler ist dafür verantwortlich, die Wiederherstellungslogik zu starten.
        /// </summary>
        event Func<CrashReason, ErrorMessage, Task> ChildProcessCrashed;

        /// <summary>
        /// Wird ausgelöst, wenn der Child-Prozess eine Log-Nachricht sendet.
        /// </summary>
        event Action<LogMessage> ChildLogReceived;

        event Action<DesignerSelectionMessage> DesignerSelectionReceived;
    }

    public class ChildProcessService : IChildProcessService
    {
        //
        // Events for communication back to the UI
        //

        public event Func<CrashReason, ErrorMessage, Task>? ChildProcessCrashed;
        public event Action<LogMessage>? ChildLogReceived;
        public event Action<DesignerSelectionMessage>? DesignerSelectionReceived;

        //
        // Deps
        //

        private readonly Window _parentWindow;
        private readonly FrameworkElement _hostContainer;
        private readonly Dispatcher _dispatcher;

        //
        // State
        //

        public IntPtr _childHwnd { get; set; } = IntPtr.Zero;
        private readonly SemaphoreSlim _restartLock = new(1, 1);
        private volatile bool _isShuttingDown = false;
        private CancellationTokenSource _pipeCts = new();
        private System.Threading.Timer? _hbWatch;
        private Process? _childProcess;
        private PipeServer? _pipeServer;
        private Task? _recvLoopTask;
        private int _pipeSessionCounter = 0;
        private readonly PendingRequests _pending = new();
        private long _lastHeartbeatTicks;

        private bool _allowChildIsVisible = true;
        public bool HasLoadedControl { get; private set; }

        private static IntPtr _globalJob = IntPtr.Zero;

        private enum EndIntent { None = 0, Restarting = 1, AppExit = 2 }

        // „Wille“ für das Ende der AKTUELLEN Sitzung
        private volatile EndIntent _endIntent = EndIntent.None;

        // Zum Entkoppeln von spät eintreffenden Events
        private int _currentSessionId = 0;


        //
        // Settings
        // 

        private bool _useSandboxing = false;
        private SandboxSettings _sandboxSettings = SandboxSettings.MaximumSecurity;
        private AppContainerProfile? _currentAcProfile;
        private CrossplatformSettings _crossplatformSettings = new();

        // Remove childHwnd
        // Nur parent und container müssen übergeben werden wenn alles funktioniert!!!
        public ChildProcessService(Window parentWindow, FrameworkElement hostContainer)
        {
            _parentWindow = parentWindow;
            _hostContainer = hostContainer;
            _dispatcher = parentWindow.Dispatcher;
        }

        public IntPtr GetChildHwnd()
        { return _childHwnd; }

        public async Task RestartAsync()
        {
            // nicht 0 ms — wir wollen seriell warten, nicht stumm abbrechen
            await _restartLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Intent sauber setzen (unterdrückt Crash-Dialoge)
                _endIntent = EndIntent.Restarting;

                await StopInternalAsync(isRestart: true).ConfigureAwait(false);
                await RestartChildProcessCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _restartLock.Release();
            }
        }

        private async Task RestartChildProcessCoreAsync()
        {
            _pipeCts = new CancellationTokenSource();
            _pipeSessionCounter++;
            _currentSessionId = _pipeSessionCounter;   // Session-ID für Watcher/Crash-Filter
            _endIntent = EndIntent.None;               // Neustart beginnt – ab jetzt „normaler“ Betrieb

            string pipeName = $"appembed-{Environment.ProcessId}-{_pipeSessionCounter}";

            string childWindowAsmToStart = "Neo.PluginWindowWPF.exe";
            if (_crossplatformSettings.UseAvalonia == true)
                childWindowAsmToStart = "Neo.PluginWindowAvalonia.exe";

            if (_useSandboxing)
            {
                _currentAcProfile = AppContainerProfile.CreateNewGuid();
                _pipeServer = new PipeServer(pipeName, _currentAcProfile.Sid);
                _childProcess = ProcessFactory.StartInAppContainer(
                    childWindowAsmToStart,
                    $"--pipe \"{pipeName}\" --parentPid {Environment.ProcessId}",
                    _sandboxSettings,
                    _currentAcProfile);
            }
            else
            {
                _pipeServer = new PipeServer(pipeName);
                _childProcess = ProcessFactory.StartProcess(
                    childWindowAsmToStart,
                    $"--pipe \"{pipeName}\" --parentPid {Environment.ProcessId}",
                    false,
                    SandboxSettings.MaximumSecurity);
            }

            if (_childProcess == null)
            {
                await _dispatcher.InvokeAsync(() =>
                    System.Windows.MessageBox.Show("Failed to start child process.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error));
                return;
            }

            // Kill-Job nur für Nicht-AppContainer
            TryAddChildToKillJob(_childProcess);

            try
            {
                const int maxConnectAttempts = 3;
                for (int attempt = 1; attempt <= maxConnectAttempts; attempt++)
                {
                    await _pipeServer!.WaitForClientAsync(_pipeCts.Token).ConfigureAwait(false);

                    if (_childProcess == null)
                        return;

                    try
                    {
                        if (_useSandboxing && _currentAcProfile != null)
                            _pipeServer.VerifyClientOrThrow(_childProcess.Id, _currentAcProfile.Sid);
                        else
                            _pipeServer.VerifyClientPidOrThrow(_childProcess.Id);

                        break; // verified
                    }
                    catch (Exception ex) when (attempt < maxConnectAttempts)
                    {
                        Debug.WriteLine($"Pipe client verification failed (attempt {attempt}/{maxConnectAttempts}): {ex.Message}");
                        try { _pipeServer.Underlying.Disconnect(); } catch { /* best-effort disconnect before retry */ }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                await _dispatcher.InvokeAsync(() =>
                    System.Windows.MessageBox.Show($"Failed to connect to child process: {ex.Message}",
                        "Pipe Error", MessageBoxButton.OK, MessageBoxImage.Error));
                return;
            }

            var helloEnv = await _pipeServer.ReceiveAsync(_pipeCts.Token).ConfigureAwait(false);
            if (helloEnv?.Type != IpcTypes.Hello) return;

            var hello = IPC.Json.FromJson<HelloMessage>(helloEnv.PayloadJson)!;
            _childHwnd = hello.Hwnd.HasValue
                ? new IntPtr(hello.Hwnd.Value)
                : FindTopLevelWindowForPid(_childProcess.Id);

            if (_childHwnd != IntPtr.Zero)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    ConfigureEmbeddedChildWindow(_childHwnd);
                    UpdatePosition();
                });
            }

            // Receive-Loop starten (kein UI nötig)
            _recvLoopTask = Task.Run(() => ParentReceiveLoopAsync(_pipeCts.Token));

            var helloReply = await SendRequestAsync(
                IpcTypes.Hello,
                new HelloMessage("Parent", Environment.ProcessId),
                TimeSpan.FromSeconds(5),
                _pipeCts.Token).ConfigureAwait(false);

            if (helloReply?.Type != IpcTypes.Ack)
            {
                Debug.WriteLine("Did not receive Hello ACK from child.");
                return;
            }

            // Erst für V4. Da ist noch ein Problem das wenn es einen Dialog gibt im Child
            // der auf eine eingabe wartet (Datei existiert etc...). Siehe FileBasket Example
            // StartHeartbeatWatcher(); // setzt intern lastTicks; kein UI
        }


        public void ConfigureSandbox(bool useSandbox, SandboxSettings settings)
        {
            _useSandboxing = useSandbox;
            _sandboxSettings = settings;
        }

        public void ConfigureCrossplatformSettings(CrossplatformSettings settings)
        {
            _crossplatformSettings = settings;
        }

        public void UpdatePosition(bool useTopMostTrick = false)
        {
            if (_childHwnd == IntPtr.Zero ||
                _allowChildIsVisible == false ||
                _parentWindow == null ||
                _parentWindow.WindowState == WindowState.Minimized)
                return;

            try
            {
                var parentHwnd = new System.Windows.Interop.WindowInteropHelper(_parentWindow).Handle;
                var nextHwnd = NativeMethods.GetWindow(parentHwnd, NativeMethods.GW_HWNDPREV);

                NativeMethods.ShowWindow(_childHwnd, NativeMethods.SW_SHOWNOACTIVATE);

                var container = _hostContainer;
                if (container.ActualWidth < 1 || container.ActualHeight < 1) return;

                var presentationSource = PresentationSource.FromVisual(_parentWindow);
                if (presentationSource == null) return;

                var transform = presentationSource.CompositionTarget.TransformToDevice;
                var containerTopLeft = container.PointToScreen(new System.Windows.Point(0, 0));
                var pixelSize = (System.Windows.Size)transform.Transform(new Vector(container.ActualWidth, container.ActualHeight));

                if (useTopMostTrick == true)
                {
                    NativeMethods.SetWindowPos(
                        _childHwnd,
                        HWND_TOPMOST,
                        (int)containerTopLeft.X,
                        (int)containerTopLeft.Y,
                        (int)Math.Max(0, pixelSize.Width),
                        (int)Math.Max(0, pixelSize.Height),
                        NativeMethods.SWP_NOACTIVATE
                    );

                    // Verhindert das Schieben über der Taskbar!
                    NativeMethods.SetWindowPos(
                        _childHwnd,
                        HWND_NOTOPMOST,
                        0, 0, 0, 0,
                        NativeMethods.SWP_NOACTIVATE |
                        NativeMethods.SWP_NOMOVE |
                        NativeMethods.SWP_NOSIZE |
                        NativeMethods.SWP_NOSENDCHANGING |
                        NativeMethods.SWP_NOREDRAW
                    );
                }
                else
                {
                    NativeMethods.SetWindowPos(
                        _childHwnd,
                        nextHwnd,
                        (int)containerTopLeft.X,
                        (int)containerTopLeft.Y,
                        (int)Math.Max(0, pixelSize.Width),
                        (int)Math.Max(0, pixelSize.Height),
                        NativeMethods.SWP_NOACTIVATE
                    );
                }
            }
            catch (Exception)
            {
            }
        }

        private IntPtr GetGlobalFocusHwnd()
        {
            var info = new NativeMethods.GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
            return NativeMethods.GetGUIThreadInfo(0, ref info)
                ? (info.hwndFocus != IntPtr.Zero ? info.hwndFocus : info.hwndActive)
                : IntPtr.Zero;
        }

        public bool IsFocusInsideChild()
        {
            if (_childHwnd == IntPtr.Zero) return false;
            var focused = GetGlobalFocusHwnd();
            if (focused == IntPtr.Zero) return false;
            // true, wenn der Fokus direkt auf dem Child-Fenster oder einem seiner (grand-)Children liegt
            return focused == _childHwnd || NativeMethods.IsChild(_childHwnd, focused);
        }

        public void NotifyParentWindowStateChanged(WindowState newState)
        {
            // Wir brauchen hier keinen Dispatcher, da ShowWindow thread-sicher ist.
            if (_childHwnd != IntPtr.Zero && _allowChildIsVisible == true )
            {
                if (newState == WindowState.Minimized)
                {
                    // Verstecke das Child-Fenster, wenn das Hauptfenster minimiert wird.
                    ShowWindow(_childHwnd, SW_HIDE);
                }
                else
                {
                    // Zeige das Child-Fenster wieder an, wenn das Hauptfenster
                    // wiederhergestellt oder maximiert wird.
                    ShowWindow(_childHwnd, SW_SHOWNOACTIVATE);

                    // Es ist eine gute Idee, die Position nach einer Zustandsänderung neu zu synchronisieren.
                    UpdatePosition();
                }
            }
        }

        public async Task SetCursorVisibilityAsync(bool isVisible)
        {
            if (_pipeServer == null || !_pipeServer.IsConnected) return;
            try
            {
                await SendRequestAsync(
                    IpcTypes.CursorVisible,
                    new IsCursorVisible(isVisible ? 1 : 0),
                    timeout: TimeSpan.FromMilliseconds(500),
                    ct: _pipeCts.Token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SetCursorVisibilityAsync] Failed: {ex.Message}");
            }
        }

        public async Task SetChildModalityAsync(bool isModal)
        {
            if (_pipeServer == null || !_pipeServer.IsConnected) return;
            try
            {
                await SendRequestAsync(
                    IpcTypes.SetChildModal,
                    new IsChildModal(isModal ? 1 : 0),
                    timeout: TimeSpan.FromMilliseconds(500),
                    ct: _pipeCts.Token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SetCursorVisibilityAsync] Failed: {ex.Message}");
            }
        }

        public async Task SetDesignerModeAsync(bool enabled)
        {
            if (_pipeServer == null || !_pipeServer.IsConnected) return;
            try
            {
                await SendRequestAsync(
                    IpcTypes.SetDesignerMode,
                    new SetDesignerModeMessage(enabled),
                    timeout: TimeSpan.FromMilliseconds(500),
                    ct: _pipeCts.Token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SetDesignerModeAsync] Failed: {ex.Message}");
            }
        }

        public async Task UnloadControlAsync()
        {
            HasLoadedControl = false;
            if (_pipeServer == null || !_pipeServer.IsConnected) return;
            try
            {
                await SendRequestAsync(
                    IpcTypes.UnloadControl,
                    new AckMessage("Unload"),
                    timeout: TimeSpan.FromSeconds(3),
                    ct: _pipeCts.Token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UnloadControlAsync] Failed: {ex.Message}");
            }
        }

        public async Task<bool> DisplayControlAsync(string mainDllPath, IEnumerable<string> nugetDlls, IEnumerable<string> additionalDlls)
        {
            if (_pipeServer == null || !_pipeServer.IsConnected) return false;

            try
            {
                // 1. Send all DLLs as a bundle of blobs
                await SendDllBundleAsync(mainDllPath, nugetDlls, additionalDlls, _pipeCts.Token);

                // 2. Send the command to load the control from the received DLLs
                var reply = await SendRequestAsync(
                    IpcTypes.LoadControl,
                    new LoadControlRequest(Path.GetFileName(mainDllPath), "DynamicUserControl", new List<string>(), null),
                    timeout: TimeSpan.FromSeconds(8),
                    ct: _pipeCts.Token);

                if (reply.Type == IpcTypes.Ack)
                {
                    HasLoadedControl = true;
                    // Force the child window to the front after loading content
                    UpdatePosition(true);
                    UpdatePosition(false);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DisplayControlAsync] Failed: {ex.Message}");
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopInternalAsync(isRestart: false);
        }

        // --- Internal Implementation ---

        private async Task StopInternalAsync(bool isRestart)
        {
            if (!isRestart)
            {
                _isShuttingDown = true;
            }

            _endIntent = isRestart ? EndIntent.Restarting : EndIntent.AppExit;

            _hbWatch?.Dispose();
            _hbWatch = null;
            _pipeCts.Cancel();

            if (_childProcess != null && !_childProcess.HasExited)
            {
                try
                {
                    if (_childProcess != null && !_childProcess.HasExited)
                    {
                        try
                        {
                            // sanft probieren (optional)
                            try { _childProcess.CloseMainWindow(); } catch { /* best-effort; process will be killed next */ }

                            // nach kurzem Warten hart töten (inkl. Kindbaum)
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                            try { await _childProcess.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { }

                            if (!_childProcess.HasExited)
                                _childProcess.Kill(entireProcessTree: true);

                            // noch kurz warten, aber nicht ewig
                            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                            try { await _childProcess.WaitForExitAsync(cts2.Token); } catch (OperationCanceledException) { }
                        }
                        catch (Exception ex) { Debug.WriteLine($"[Shutdown] Kill child process failed: {ex.Message}"); }
                    }

                    // --- KORREKTUR HIER ---
                    // Erstelle eine CancellationTokenSource, die nach 1 Sekunde abbricht.
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                    {
                        try
                        {
                            // Warte auf den Prozess-Exit, aber nur so lange, bis der Token abbricht.
                            await _childProcess!.WaitForExitAsync(cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            // Das ist OK. Der Timeout wurde erreicht. Wir machen einfach weiter.
                        }
                    }
                    // --- ENDE DER KORREKTUR ---
                }
                catch (Exception ex) { Debug.WriteLine($"[Shutdown] Cleanup failed: {ex.Message}"); }
            }
            _childProcess = null;
            _childHwnd = IntPtr.Zero;
            HasLoadedControl = false;

            if (_pipeServer != null)
            {
                await _pipeServer.DisposeAsync();
                _pipeServer = null;
            }

            if (_recvLoopTask != null && !_recvLoopTask.IsCompleted)
            {
                try { await _recvLoopTask; } catch (OperationCanceledException) { } catch (Exception ex) { Debug.WriteLine($"[Shutdown] RecvLoop error: {ex.Message}"); }
            }
        }

        //
        // Helpers
        //

        public static string ToLogicalName(string fullPath, string? root = null)
        {
            try
            {
                if (!string.IsNullOrEmpty(root))
                {
                    var rel = Path.GetRelativePath(root, fullPath)
                                  .Replace('\\', '/')
                                  .TrimStart('/');
                    if (!string.IsNullOrWhiteSpace(rel))
                        return rel;
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[ToLogicalName] {ex.Message}"); }

            // Fallback: nur Dateiname
            return Path.GetFileName(fullPath);
        }

        public static bool IsBrokenPipe(Exception ex)
        {
            if (ex is IOException io)
            {
                // ERROR_BROKEN_PIPE = 109  -> 0x8007006D als HRESULT
                const int HR_BROKEN_PIPE = unchecked((int)0x8007006D);
                if (io.HResult == HR_BROKEN_PIPE) return true;
                var msg = io.Message?.ToLowerInvariant() ?? "";
                if (msg.Contains("broken pipe") || msg.Contains("pipe is broken"))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Baut eine de-duplizierte Liste der zu übertragenden DLLs
        /// (Haupt-DLL + NuGet-DLLs + optionale Zusatz-DLLs),
        /// filtert sauber auf *.dll und existierende Pfade.
        /// </summary>
        public static List<string> BuildDllList(string mainDllPath, IEnumerable<string> nugetDlls, IEnumerable<string>? extraDlls)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddIfDll(string p)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                if (!p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return;
                if (!File.Exists(p)) return;
                set.Add(Path.GetFullPath(p));
            }

            // Haupt-DLL
            AddIfDll(mainDllPath);

            // NuGet-DLLs
            if (nugetDlls != null)
                foreach (var p in nugetDlls) AddIfDll(p);

            // Optionale Zusatz-DLLs (falls du nativ z. B. sqlite etc. mitgibst)
            if (extraDlls != null)
                foreach (var p in extraDlls) AddIfDll(p);

            return set.ToList();
        }

        public static void ConfigureEmbeddedChildWindow(IntPtr hwnd)
        {
            // 0) Sicherstellen, dass es gerade nicht sichtbar ist (keine Animationen/Zucken)
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);

            // 1) Normale Styles: Child setzen, TopLevel-Bits raus
            long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();

            style &= ~NativeMethods.WS_POPUP;
            style &= ~NativeMethods.WS_CAPTION;
            style &= ~NativeMethods.WS_THICKFRAME;
            style &= ~NativeMethods.WS_BORDER;
            style &= ~NativeMethods.WS_SYSMENU;
            style &= ~NativeMethods.WS_MINIMIZEBOX;
            style &= ~NativeMethods.WS_MAXIMIZEBOX;
            style &= ~NativeMethods.WS_MINIMIZE;
            style &= ~NativeMethods.WS_MAXIMIZE;

            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));

            // 2) Extended Styles: raus aus Taskbar, keine Dialog-/Kantenstile
            long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();

            exStyle &= ~NativeMethods.WS_EX_APPWINDOW;   // kein Taskbar-Button
            exStyle |= NativeMethods.WS_EX_TOOLWINDOW;  // "Toolwindow", reduziert Taskbar-Präsenz
            exStyle &= ~NativeMethods.WS_EX_DLGMODALFRAME;
            exStyle &= ~NativeMethods.WS_EX_WINDOWEDGE;
            exStyle &= ~NativeMethods.WS_EX_CLIENTEDGE;
            exStyle &= ~NativeMethods.WS_EX_STATICEDGE;

            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

            // 3) Änderungen durchsetzen (ohne Größen-/Positionswechsel)
            NativeMethods.SetWindowPos(
                hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_FRAMECHANGED |
                NativeMethods.SWP_NOMOVE |
                NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOOWNERZORDER |
                NativeMethods.SWP_NOACTIVATE);

            // 4) DWM-Optik optional bereinigen (ein Ort, ein Mal)
            try
            {
                // Animationen/Transitions aus
                int disable = 1;
                NativeMethods.DwmSetWindowAttribute(
                    hwnd,
                    NativeMethods.DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED,
                    ref disable,
                    sizeof(int));

                // Nonclient-Rendering aus
                int policy = (int)NativeMethods.DWMNCRENDERINGPOLICY.DWMNCRP_DISABLED;
                NativeMethods.DwmSetWindowAttribute(
                    hwnd,
                    NativeMethods.DWMWINDOWATTRIBUTE.DWMWA_NCRENDERING_POLICY,
                    ref policy,
                    sizeof(int));

                // Glasrand komplett entfernen
                var margins = new NativeMethods.MARGINS { cxLeftWidth = 0, cxRightWidth = 0, cyTopHeight = 0, cyBottomHeight = 0 };
                NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            }
            catch
            {
                // dwmapi ggf. nicht verfügbar – unkritisch
            }
        }

        public static IntPtr FindTopLevelWindowForPid(int pid)
        {
            IntPtr found = IntPtr.Zero;

            NativeMethods.EnumWindows((h, l) =>
            {
                NativeMethods.GetWindowThreadProcessId(h, out uint p);
                if (p == (uint)pid)
                {
                    // erstes Top-Level-Fenster des Prozesses nehmen
                    found = h;
                    return false; // stop
                }
                return true; // continue
            }, IntPtr.Zero);

            return found;
        }

        private static void EnsureGlobalKillJob()
        {
            if (_globalJob != IntPtr.Zero) return;

            _globalJob = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (_globalJob == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT.KILL_ON_JOB_CLOSE;

            int cb = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(cb);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!NativeMethods.SetInformationJobObject(
                        _globalJob,
                        NativeMethods.JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                        ptr, (uint)cb))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        public bool TryAddChildToKillJob(Process proc)
        {
            // AppContainer → hier kein Job möglich (Fallback unten)
            try
            {
                if (ProcessIntrospection.IsAppContainerProcess(proc))
                {
                    Debug.WriteLine("AppContainer → Job-Zuordnung übersprungen (verwende Pipe-Disconnect-Exit im Child).");
                    return false;
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[AssignToJob] AppContainer check failed: {ex.Message}"); }

            EnsureGlobalKillJob();

            // WICHTIG: SafeProcessHandle direkt übergeben (keine File-Handles!)
            if (!NativeMethods.AssignProcessToJobObject(_globalJob, proc.SafeHandle))
            {
                int err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"AssignProcessToJobObject failed: {err}");
                return false; // → Fallback (siehe unten)
            }
            return true;
        }

        private async Task<IpcEnvelope> SendRequestAsync<TPayload>(
            string type,
            TPayload payload,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            if (_pipeServer == null) throw new InvalidOperationException("Pipe not connected.");

            // Pro-Request-Cancellation mit globalem CTS linken
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _pipeCts.Token);

            Exception? lastError = null;
            bool attemptedRetry = false;

            while (true)
            {
                if (_isShuttingDown) throw new OperationCanceledException("Shutting down.");

                // neue CorrelationId pro Versuch
                var corr = Guid.NewGuid().ToString("N");
                var env = new IpcEnvelope(type, corr, IPC.Json.ToJson(payload));

                // Registrierung VOR dem Senden (keine Race Condition)
                var waitTask = _pending.Register(corr, timeout, linkedCts.Token);

                // --- Senden ---
                try
                {
                    await _pipeServer.SendAsync(env, linkedCts.Token);
                }
                catch (Exception ex) when (!linkedCts.IsCancellationRequested)
                {
                    lastError = ex;
                    // Registrierung wieder aufräumen (sonst hängt ein Pending)
                    _pending.TryCancel(corr, ex);

                    // Retry-Bedingung nur bei potentiell transienten Fällen
                    bool canRetry =
                        !attemptedRetry &&
                        !_isShuttingDown &&
                        IsBrokenPipe(ex) &&
                        _pipeServer.IsConnected; // nur wenn Stream noch connected meldet

                    if (canRetry)
                    {
                        attemptedRetry = true;
                        await Task.Delay(50, linkedCts.Token); // kurzer Backoff
                        continue; // nächster Loop mit neuer CorrelationId
                    }

                    throw; // kein Retry möglich -> Fehler weiterreichen
                }

                // --- Antwort abwarten ---
                try
                {
                    var reply = await waitTask; // wird in Receive-Loop via _pending.TryComplete erfüllt
                    return reply;
                }
                catch (TimeoutException tex)
                {
                    lastError = tex;

                    // Retry bei Timeout nur, wenn Pipe noch verbunden und noch kein Retry gemacht
                    bool canRetry =
                        !attemptedRetry &&
                        !_isShuttingDown &&
                        _pipeServer.IsConnected;

                    if (canRetry)
                    {
                        attemptedRetry = true;
                        // Pending ist schon per Timeout ausgetragen, wir senden mit neuer CorrelationId neu
                        await Task.Delay(50, linkedCts.Token);
                        continue;
                    }

                    throw; // zweites Timeout oder keine Verbindung -> hochreichen
                }
            }
        }

        private void StartHeartbeatWatcher()
        {
            _hbWatch?.Dispose();
            Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
            _hbWatch = new System.Threading.Timer(_ =>
            {
                // Sitzung „capturen“, um Race mit Restart zu vermeiden
                int sid = _currentSessionId;
                if (_endIntent != EndIntent.None) return; // geplanter Abbau → kein Crash
                if ((DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastHeartbeatTicks), DateTimeKind.Utc)).TotalSeconds <= 5)
                    return;

                // Timer stoppen, um Doppelmeldungen zu vermeiden
                _hbWatch?.Change(Timeout.Infinite, Timeout.Infinite);

                // Session noch gültig?
                if (sid != _currentSessionId) return;

                // Jetzt wirklich als Crash melden
                if (ChildProcessCrashed != null)
                {
                    var err = new ErrorMessage("The plugin process became unresponsive (heartbeat timeout).", "TimeoutException", "");
                    _ = _dispatcher.InvokeAsync(() => ChildProcessCrashed.Invoke(CrashReason.HeartbeatTimeout, err));
                }
            }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));
        }

        // In ChildProcessService.cs

        private async Task ParentReceiveLoopAsync(CancellationToken ct)
        {
            int mySession = _currentSessionId;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var env = await _pipeServer!.ReceiveAsync(ct);
                    if (env == null)
                    {
                        // Pipe wurde geschlossen, Schleife beenden.
                        break;
                    }

                    // 1) Ist dies eine Antwort auf eine unserer Anfragen?
                    if (_pending.TryComplete(env))
                    {
                        continue;
                    }

                    // 2) Unaufgeforderte Nachrichten vom Child-Prozess.
                    switch (env.Type)
                    {
                        case IpcTypes.Heartbeat:
                            Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
                            break;

                        case IpcTypes.ChildActivated:
                            // Wenn der Benutzer in das Child-Fenster klickt, wird es aktiviert.
                            // Wir müssen sicherstellen, dass unser Hauptfenster in der Z-Ordnung
                            // direkt dahinter liegt, um visuelle Glitches zu vermeiden.
                            if (_childHwnd != IntPtr.Zero)
                            {
                                await _dispatcher.InvokeAsync(() =>
                                {
                                    var parentHwnd = new WindowInteropHelper(_parentWindow).Handle;
                                    BringTopmostNoActivate(_childHwnd);
                                    UpdatePosition(false);
                                }, DispatcherPriority.Render);
                            }
                            break;
                        case IpcTypes.Log:
                            var log = IPC.Json.FromJson<LogMessage>(env.PayloadJson)!;
                            ChildLogReceived?.Invoke(log);
                            break;

                        case IpcTypes.DesignerSelection:
                            var sel = IPC.Json.FromJson<DesignerSelectionMessage>(env.PayloadJson)!;
                            _ = _dispatcher.InvokeAsync(() => DesignerSelectionReceived?.Invoke(sel));
                            break;

                        case IpcTypes.Error:
                            var err = IPC.Json.FromJson<ErrorMessage>(env.PayloadJson)!;
                            // Event mit dem Grund "UnhandledException" auslösen.
                            _ = _dispatcher.InvokeAsync(() => ChildProcessCrashed?.Invoke(CrashReason.UnhandledException, err));
                            break;

                        // Leere Cases für bekannte, aber hier nicht behandelte Nachrichten.
                        case IpcTypes.Hello:
                        case IpcTypes.CursorVisible:
                            break;

                        default:
                            Debug.WriteLine($"[ParentReceiveLoop] Received unknown unsolicited message type: {env.Type}");
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Erwartetes Verhalten beim Herunterfahren.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ParentReceiveLoopAsync] Unhandled exception: {ex.Message}");
                _pending.FailAll(ex);
            }
            finally
            {
                // Nur crashen, wenn:
                //  - kein geplanter Restart/Exit
                //  - und diese Callback-Instanz noch zur aktuellen Session gehört
                if (_endIntent == EndIntent.None && mySession == _currentSessionId)
                {
                    var err = new ErrorMessage("Pipe disconnected unexpectedly.", "IOException", "");
                    _ = _dispatcher.InvokeAsync(() => ChildProcessCrashed?.Invoke(CrashReason.PipeDisconnected, err));
                }
            }
        }

        /// <summary>
        /// Sendet die gesamte DLL-Nutzlast (managed + native, Parent weiß es nicht)
        /// an den Child. Es wird bewusst KEIN Manifest übertragen.
        /// </summary>
        private async Task SendDllBundleAsync(
            string mainDllPath,
            IEnumerable<string> nugetDlls,
            IEnumerable<string>? extraDlls,
            CancellationToken ct)
        {
            if (_pipeServer == null) throw new InvalidOperationException("Pipe not connected.");

            var all = BuildDllList(mainDllPath, nugetDlls, extraDlls);

            // Optional: Haupt-DLL zuerst senden (rein für’s Auge/Debugging)
            var mainFull = Path.GetFullPath(mainDllPath);
            if (all.Remove(mainFull))
                all.Insert(0, mainFull);

            // Gemeinsamer „Root“ – reiner Schönheitsgewinn für logische Namen
            string? commonRoot = null;
            try
            {
                var dirs = all.Select(Path.GetDirectoryName)
                              .OfType<string>() // 1. Filtert alle null-Werte und ändert den Typ zu IEnumerable<string>
                              .Where(d => d.Length > 0) // 2. Jetzt sicher auf leere Strings prüfen (optional)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToList();

                if (dirs.Count > 0)
                {
                    // Keine Warnung mehr hier!
                    commonRoot = dirs.OrderBy(d => d.Length).First();
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[StreamFiles] Common root detection failed: {ex.Message}"); }

            // Seriell streamen → natürliches Backpressure der Pipe
            foreach (var file in all)
            {
                var logical = ToLogicalName(file, commonRoot);
                await SendFileAsBlobAsync(file, logical, ct);
            }
        }

        /// <summary>
        /// Sendet EINE Datei als BLOB (Start/Chunks/End) über die Pipe.
        /// </summary>
        private async Task SendFileAsBlobAsync(string filePath, string logicalName, CancellationToken ct)
        {
            if (_pipeServer == null) throw new InvalidOperationException("Pipe not connected.");
            if (!File.Exists(filePath)) return;

            // Wiederverwendeter CorrelationId erlaubt Debug-Zuordnung im Child-Log
            var corr = Guid.NewGuid();

            await using var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var meta = new IPC.BlobStartMeta(Name: logicalName, Length: fs.Length, Sha256: null, Compressed: false);

            // Stream-freundliches Read-Delegate (keine LOH-Arrays erzeugen)
            async ValueTask<int> Reader(Memory<byte> dst, CancellationToken token)
                => await fs.ReadAsync(dst, token).ConfigureAwait(false);

            await _pipeServer.Messenger.SendBlobAsync(
                correlationId: corr,
                meta: meta,
                read: Reader,
                chunkSize: 512 * 1024,
                ct: ct);
        }

        public System.Windows.Media.Imaging.BitmapSource? CaptureChildScreenshot()
        {
            if (_childHwnd == IntPtr.Zero)
                return null;

            if (!HasLoadedControl)
                return null;

            if (!NativeMethods.GetWindowRect(_childHwnd, out var rect))
                return null;

            int width = rect.right - rect.left;
            int height = rect.bottom - rect.top;
            if (width <= 0 || height <= 0)
                return null;

            IntPtr screenDC = IntPtr.Zero;
            IntPtr memDC = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                screenDC = NativeMethods.GetDC(IntPtr.Zero);
                memDC = NativeMethods.CreateCompatibleDC(screenDC);
                hBitmap = NativeMethods.CreateCompatibleBitmap(screenDC, width, height);
                oldBitmap = NativeMethods.SelectObject(memDC, hBitmap);

                NativeMethods.PrintWindow(_childHwnd, memDC, NativeMethods.PW_RENDERFULLCONTENT);

                NativeMethods.SelectObject(memDC, oldBitmap);

                var bmpSource = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                bmpSource.Freeze();
                return bmpSource;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
                if (memDC != IntPtr.Zero) NativeMethods.DeleteDC(memDC);
                if (screenDC != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        public void HideChild()
        {
            _allowChildIsVisible = false;

            if ( _childHwnd != IntPtr.Zero )
                NativeMethods.ShowWindow(_childHwnd, NativeMethods.SW_HIDE);
        }

        public void ShowChild()
        {
            _allowChildIsVisible = true;

            if (_childHwnd != IntPtr.Zero)
            {
                // UpdatePosition calls ShowWindow(SW_SHOWNOACTIVATE) and fixes z-order.
                // Without this, the window may appear behind the parent after HideChild/ShowChild
                // because SW_SHOW alone does not change z-order.
                UpdatePosition(true);
                UpdatePosition(false);
            }
        }

        public void ChildNoTopmost()
        {
            SetWindowPos(_childHwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        public void BringTopmostNoActivate(IntPtr hwnd)
        {
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        void BringFrontNoActivate(IntPtr hwnd)
        {
            // Schritt 1: nach ganz oben, ohne zu aktivieren
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);

            // Schritt 2: Topmost wieder weg (Fenster bleibt nun über normalen Fenstern),
            // weiterhin ohne Aktivierung
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }
}
