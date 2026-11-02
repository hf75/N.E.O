using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;

using Neo.IPC;

namespace Neo.App
{
    /// <summary>
    /// Avalonia implementation of IChildProcessService.
    /// Launches Neo.PluginWindowAvalonia as a separate window (no HWND embedding).
    /// Communicates via named pipes using FramedPipeMessenger from Neo.IPC.
    /// </summary>
    public class AvaloniaChildProcessService : IChildProcessService
    {
        private readonly MainWindow _mainWindow;
        private NamedPipeServerStream? _pipeStream;
        private FramedPipeMessenger? _messenger;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly SemaphoreSlim _restartLock = new(1, 1);
        private Process? _childProcess;
        private Task? _listenLoopTask;
        private CancellationTokenSource _pipeCts = new();
        private bool _hasLoadedControl;
        private bool _isDisposed;
        private bool _isShuttingDown;
        private bool _allowChildVisible = true;
        private SandboxSettings _sandboxSettings = SandboxSettings.MaximumSecurity;
        private CrossplatformSettings _crossplatformSettings = new();
        private int _sessionCounter;

        // What the current child process has received (it keeps every assembly until it exits).
        private readonly SentFileLedger _sentFiles = new();
        private IntPtr _childHwnd;

        public bool HasLoadedControl => _hasLoadedControl;

        public int? ChildProcessId
        {
            get { try { return _childProcess is { HasExited: false } p ? p.Id : null; } catch { return null; } }
        }

        public event Func<CrashReason, ErrorMessage, Task>? ChildProcessCrashed;
        public event Action<LogMessage>? ChildLogReceived;

        // ── Player mode: the host hides behind the app window and follows it ──

        /// <summary>
        /// True (default): the app window docks to the right of the host and follows it. False:
        /// the app window is a window of its own — the host follows it (see <see cref="ChildWindowBounds"/>).
        /// </summary>
        public bool DockChildToHost { get; set; } = true;

        /// <summary>Title and icon the app window is started with; null: the preview defaults.</summary>
        public string? ChildWindowTitle { get; set; }
        public string? ChildWindowIcon { get; set; }

        /// <summary>A line the app window shows for a few seconds once the app is first loaded.</summary>
        public string? ChildWindowHint { get; set; }

        /// <summary>The app window's last reported screen bounds (position in pixels, size in DIPs); null before it reported.</summary>
        public ParentWindowBoundsMessage? ChildWindowBounds { get; private set; }

        /// <summary>The app window moved or resized.</summary>
        public event Action<ParentWindowBoundsMessage>? ChildWindowBoundsChanged;

        /// <summary>The user pressed Ctrl+Shift+N in the app window.</summary>
        public event Action? HostToggleRequested;

        /// <summary>The app window went away without the host asking (the user closed it).</summary>
        public event Action? ChildExited;

        /// <summary>
        /// True (default): a preview window the user closes is reported as a crash, so the host
        /// offers to restore it. False (player mode): it is the app closing — <see cref="ChildExited"/> fires instead.
        /// </summary>
        public bool TreatCloseAsCrash { get; set; } = true;

        public AvaloniaChildProcessService(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
        }

        public async Task RestartAsync()
        {
            Debug.WriteLine("[ChildProcess] RestartAsync: acquiring lock...");

            // If another RestartAsync is in progress (e.g. from MainWindow_Loaded),
            // cancel it by killing the CTS so its pipe waits abort immediately.
            if (!_restartLock.Wait(0)) // non-blocking check
            {
                Debug.WriteLine("[ChildProcess] RestartAsync: lock held by another call, cancelling it...");
                try { _pipeCts.Cancel(); } catch { }
                // Now wait for the lock (the other call should exit quickly after cancel)
                if (!await _restartLock.WaitAsync(TimeSpan.FromSeconds(5)))
                {
                    Debug.WriteLine("[ChildProcess] RestartAsync: lock timeout after 5s, forcing...");
                    // Force cleanup: dispose pipe to unblock any pending reads
                    try { _pipeStream?.Dispose(); } catch { }
                    _pipeStream = null;
                    _messenger = null;
                    await _restartLock.WaitAsync(); // should succeed now
                }
            }

            Debug.WriteLine("[ChildProcess] RestartAsync: lock acquired");
            try
            {
                Debug.WriteLine("[ChildProcess] RestartAsync: DisposeChildAsync...");
                await DisposeChildAsync();
                Debug.WriteLine("[ChildProcess] RestartAsync: StartChildProcessCoreAsync...");
                await StartChildProcessCoreAsync();
                Debug.WriteLine("[ChildProcess] RestartAsync: done");
            }
            finally
            {
                _restartLock.Release();
                Debug.WriteLine("[ChildProcess] RestartAsync: lock released");
            }
        }

        private async Task StartChildProcessCoreAsync()
        {
            _pipeCts = new CancellationTokenSource();
            _sessionCounter++;
            _isShuttingDown = false;
            _sentFiles.Clear(); // a new process that has received nothing yet

            var pipeName = $"neo_avalonia_{Environment.ProcessId}_{_sessionCounter}";

            _pipeStream = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            _messenger = new FramedPipeMessenger(_pipeStream);

            // Find child executable — always use Avalonia child from this host
            var (childPath, useDotnet) = FindChildExecutable();
            if (childPath == null)
            {
                Debug.WriteLine("[AvaloniaChildProcessService] Child executable not found!");
                return;
            }

            var childArgs = $"--pipe {pipeName} --parentPid {Environment.ProcessId} --standalone";
            if (DockChildToHost)
                childArgs += " --no-activate";
            if (!string.IsNullOrWhiteSpace(ChildWindowTitle))
                childArgs += $" --title \"{ChildWindowTitle.Replace("\"", "")}\"";
            if (!string.IsNullOrWhiteSpace(ChildWindowIcon) && File.Exists(ChildWindowIcon))
                childArgs += $" --icon \"{ChildWindowIcon}\"";
            if (!string.IsNullOrWhiteSpace(ChildWindowHint))
                childArgs += $" --hint \"{ChildWindowHint.Replace("\"", "")}\"";

            var psi = new ProcessStartInfo
            {
                FileName = useDotnet ? "dotnet" : childPath,
                Arguments = useDotnet ? $"\"{childPath}\" {childArgs}" : childArgs,
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            Debug.WriteLine($"[ChildProcess] Starting: {psi.FileName} {psi.Arguments}");

            try
            {
                _childProcess = Process.Start(psi);
                if (_childProcess == null)
                {
                    Debug.WriteLine("[AvaloniaChildProcessService] Failed to start child process.");
                    return;
                }

                Debug.WriteLine("[ChildProcess] StartCore: waiting for child to connect...");

                // WaitForConnectionAsync does NOT honor CancellationToken on Windows.
                // Use a real timeout by racing against Task.Delay.
                var connectTask = _pipeStream.WaitForConnectionAsync(_pipeCts.Token);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), _pipeCts.Token);
                if (await Task.WhenAny(connectTask, timeoutTask) != connectTask)
                {
                    Debug.WriteLine("[ChildProcess] StartCore: connection timed out (10s)");
                    // Force-close the pipe to unblock WaitForConnectionAsync
                    try { _pipeStream.Dispose(); } catch { }
                    _pipeStream = null;
                    _messenger = null;
                    return;
                }
                await connectTask; // propagate any exception

                Debug.WriteLine("[ChildProcess] StartCore: child connected, starting listen loop");

                // Start listen loop immediately — don't block waiting for Hello.
                // The child sends Hello from its Opened event which fires asynchronously.
                // Hello is handled inside the ListenLoop.
                _listenLoopTask = Task.Run(() => ListenLoopAsync(_pipeCts.Token));

                Debug.WriteLine($"[ChildProcess] StartCore: done (PID: {_childProcess.Id})");
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[AvaloniaChildProcessService] Child connection timed out.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AvaloniaChildProcessService] RestartAsync failed: {ex.Message}");
            }
        }

        // Returns (fileName, arguments prefix) — on Linux the "exe" is actually
        // "dotnet" with the DLL path as argument.
        private (string? exePath, bool useDotnetLauncher) FindChildExecutable()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 1. Try native executable first (works on Windows, or after dotnet publish on Linux)
            var nativeName = OperatingSystem.IsWindows()
                ? "Neo.PluginWindowAvalonia.exe"
                : "Neo.PluginWindowAvalonia";

            var candidate = Path.Combine(baseDir, nativeName);
            if (File.Exists(candidate)) return (candidate, false);

            // 2. Try DLL (dotnet build on Linux only produces a DLL, not a native binary)
            var dllName = "Neo.PluginWindowAvalonia.dll";
            var dllCandidate = Path.Combine(baseDir, dllName);
            if (File.Exists(dllCandidate)) return (dllCandidate, true);

            // 3. Dev-time: sibling project output
            foreach (var config in Neo.Shared.NeoBuildInfo.DevBuildConfigurations)
            {
                var devNative = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                    "Neo.PluginWindowAvalonia", "bin", config, Neo.Shared.NeoBuildInfo.TargetFramework, nativeName));
                if (File.Exists(devNative)) return (devNative, false);

                var devDll = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                    "Neo.PluginWindowAvalonia", "bin", config, Neo.Shared.NeoBuildInfo.TargetFramework, dllName));
                if (File.Exists(devDll)) return (devDll, true);
            }

            Debug.WriteLine($"[AvaloniaChildProcessService] Child not found in: {baseDir}");
            return (null, false);
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            if (_messenger == null) return;

            try
            {
                await _messenger.ReceiveLoopAsync(
                    onControl: async env =>
                    {
                        switch (env.Type)
                        {
                            case IpcTypes.HotReloadResult:
                                if (_pendingHotReloads.TryRemove(env.CorrelationId, out var pendingHotReload))
                                    pendingHotReload.TrySetResult(Json.FromJson<HotReloadResultMessage>(env.PayloadJson));
                                break;

                            case IpcTypes.AppMessage:
                                // Not awaited: the answer may take this loop reading further messages.
                                if (Json.FromJson<AppMessage>(env.PayloadJson) is { } appMessage)
                                {
                                    var replyTo = env.CorrelationId;
                                    _ = Dispatcher.UIThread.InvokeAsync(async () =>
                                    {
                                        string? answer = null;
                                        try { if (AppMessageReceived is { } received) answer = await received(appMessage.Kind, appMessage.Payload); }
                                        catch (Exception ex) { Debug.WriteLine($"[AppMessage] {appMessage.Kind}: {ex.Message}"); }
                                        await SafeSendControlAsync(new IpcEnvelope(IpcTypes.AppMessageAnswer, replyTo, Json.ToJson(new AppMessageAnswer(answer))));
                                    });
                                }
                                break;

                            case IpcTypes.AppStateCaptured:
                            case IpcTypes.AppStateRestored:
                            case IpcTypes.LinkDescribed:
                            case IpcTypes.LinkStarted:
                                if (_pendingAppState.TryRemove(env.CorrelationId, out var pendingAppState))
                                    pendingAppState.TrySetResult(env.PayloadJson);
                                break;

                            case IpcTypes.Hello:
                                var hello = Json.FromJson<HelloMessage>(env.PayloadJson);
                                HotReloadCapabilities = hello?.HotReloadCapabilities;
                                _childHwnd = new IntPtr(hello?.Hwnd ?? 0);
                                Dispatcher.UIThread.Post(() =>
                                {
                                    WatchForeground();
                                    // The window opens in the middle of the screen and leaves the focus alone,
                                    // so no activation of N.E.O. follows that would tell it where it belongs.
                                    if (DockChildToHost) UpdatePosition();
                                });
                                Debug.WriteLine("[ChildProcess] Hello received from child");
                                await SafeSendControlAsync(new IpcEnvelope(
                                    IpcTypes.Ack, env.CorrelationId,
                                    Json.ToJson(new AckMessage("Hello acknowledged"))));
                                break;

                            case IpcTypes.Log:
                                var logMsg = Json.FromJson<LogMessage>(env.PayloadJson);
                                if (logMsg != null)
                                    ChildLogReceived?.Invoke(logMsg);
                                break;

                            case IpcTypes.Error:
                                var errMsg = Json.FromJson<ErrorMessage>(env.PayloadJson);
                                // Not awaited: a repair in place hot-reloads into this very child and
                                // waits for its answer, which this loop has to keep reading.
                                if (errMsg != null && ChildProcessCrashed != null)
                                    _ = Dispatcher.UIThread.InvokeAsync(() => ChildProcessCrashed.Invoke(CrashReason.UnhandledException, errMsg));
                                break;

                            case IpcTypes.ChildWindowBounds:
                                var childBounds = Json.FromJson<ParentWindowBoundsMessage>(env.PayloadJson);
                                if (childBounds != null)
                                {
                                    ChildWindowBounds = childBounds;
                                    ChildWindowBoundsChanged?.Invoke(childBounds);
                                }
                                break;

                            case IpcTypes.ToggleHostWindow:
                                HostToggleRequested?.Invoke();
                                break;

                            case IpcTypes.NotifyFirstChildVisibility:
                            case IpcTypes.ChildActivated:
                            case IpcTypes.Heartbeat:
                            case IpcTypes.Ack:
                                // Expected messages — no action needed
                                break;

                            default:
                                Debug.WriteLine($"[AvaloniaChildProcessService] Unhandled IPC: {env.Type}");
                                break;
                        }
                    },
                    onBlobStart: null!,
                    onBlobChunk: null!,
                    onBlobEnd: null!,
                    ct: ct);

                // The pipe closed cleanly: the app window was closed.
                if (_isShuttingDown || ct.IsCancellationRequested) return;
                if (TreatCloseAsCrash && ChildProcessCrashed != null)
                {
                    await ChildProcessCrashed.Invoke(CrashReason.PipeDisconnected,
                        new ErrorMessage("The preview window was closed.", "EndOfStreamException", ""));
                }
                else
                {
                    ChildExited?.Invoke();
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException) when (_isShuttingDown) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AvaloniaChildProcessService] ListenLoop error: {ex.Message}");
                // Child may have crashed — notify if we weren't shutting down
                if (!_isShuttingDown && ChildProcessCrashed != null)
                {
                    var err = new ErrorMessage(
                        $"Child process communication lost: {ex.Message}",
                        ex.GetType().FullName,
                        ex.ToString());
                    await ChildProcessCrashed.Invoke(CrashReason.PipeDisconnected, err);
                }
            }
        }

        private async Task SafeSendControlAsync(IpcEnvelope env)
        {
            if (_messenger == null || _pipeStream == null || !_pipeStream.IsConnected) return;
            await _sendLock.WaitAsync();
            try
            {
                await _messenger.SendControlAsync(env);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AvaloniaChildProcessService] Send failed: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task SendFileAsBlobAsync(string filePath, CancellationToken ct)
        {
            if (_messenger == null || !File.Exists(filePath)) return;

            var corr = Guid.NewGuid();
            var logicalName = Path.GetFileName(filePath);

            await using var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var meta = new BlobStartMeta(Name: logicalName, Length: fs.Length);

            await _sendLock.WaitAsync(ct);
            try
            {
                await _messenger.SendBlobAsync(
                    correlationId: corr,
                    meta: meta,
                    read: async (buffer, token) => await fs.ReadAsync(buffer, token),
                    chunkSize: 512 * 1024,
                    ct: ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void UpdatePosition(bool useTopMostTrick = false)
        {
            // Send parent bounds to child via IPC for magnetic docking
            SendParentBoundsAsync().ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
        }

        // ── One app to the user ──────────────────────────────────────────
        // N.E.O. and the app are two windows of two processes. Coming back from another program, the
        // one the user picks brings the other along. Asks Windows a few times a second because no event
        // is reliable: a window shown while another program has the foreground counts as active inside
        // its own process, and then reaches the front without Activated or a WinEvent.

        private DispatcherTimer? _foregroundWatch;
        private IntPtr _lastForeground;

        private void WatchForeground()
        {
            if (!OperatingSystem.IsWindows() || _foregroundWatch != null) return;

            _foregroundWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _foregroundWatch.Tick += (_, _) => KeepWindowsTogether();
            _foregroundWatch.Start();
        }

        private void KeepWindowsTogether()
        {
            var foreground = GetForegroundWindow();
            if (foreground == _lastForeground) return;
            _lastForeground = foreground;

            var host = _mainWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (host == IntPtr.Zero || _childHwnd == IntPtr.Zero) return;
            if (!_mainWindow.IsVisible || _mainWindow.WindowState == global::Avalonia.Controls.WindowState.Minimized) return;

            if (foreground == host)
                SetWindowPos(_childHwnd, host, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            else if (foreground == _childHwnd)
                SetWindowPos(host, _childHwnd, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private async Task SendParentBoundsAsync()
        {
            if (_messenger == null || _pipeStream == null || !_pipeStream.IsConnected) return;

            try
            {
                var pos = _mainWindow.Position;
                var msg = new ParentWindowBoundsMessage(
                    pos.X, pos.Y,
                    _mainWindow.Width, _mainWindow.Height,
                    _allowChildVisible && (!DockChildToHost || _mainWindow.WindowState != global::Avalonia.Controls.WindowState.Minimized),
                    Dock: DockChildToHost);

                await SafeSendControlAsync(new IpcEnvelope(
                    IpcTypes.ParentWindowBounds,
                    "",
                    Json.ToJson(msg)));
            }
            catch { /* best-effort positioning */ }
        }

        public async Task<bool> DisplayControlAsync(string mainDllPath, IEnumerable<string> nugetDlls, IEnumerable<string> additionalDlls)
        {
            if (_messenger == null || _pipeStream == null || !_pipeStream.IsConnected) return false;

            try
            {
                var ct = _pipeCts.Token;

                // Build full DLL list: main + nugets + additional
                var allDlls = new List<string>();

                // Main DLL first
                var mainFull = Path.GetFullPath(mainDllPath);
                if (File.Exists(mainFull))
                    allDlls.Add(mainFull);

                foreach (var dll in nugetDlls)
                    if (File.Exists(dll) && !allDlls.Contains(Path.GetFullPath(dll)))
                        allDlls.Add(Path.GetFullPath(dll));

                foreach (var dll in additionalDlls)
                {
                    var full = Path.GetFullPath(dll);
                    if (File.Exists(full) && !allDlls.Contains(full))
                        allDlls.Add(full);
                }

                // Stream all DLLs as blobs
                foreach (var dll in allDlls)
                {
                    await SendFileAsBlobAsync(dll, ct);
                    _sentFiles.MarkSent(dll);
                }

                // Send LoadControl command
                var req = new LoadControlRequest(
                    Path.GetFileName(mainDllPath),
                    "DynamicUserControl",
                    nugetDlls.Select(d => Path.GetFileName(d)!).ToList());

                await SafeSendControlAsync(new IpcEnvelope(
                    IpcTypes.LoadControl,
                    Guid.NewGuid().ToString("N"),
                    Json.ToJson(req)));

                _hasLoadedControl = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AvaloniaChildProcessService] DisplayControlAsync failed: {ex.Message}");
                return false;
            }
        }

        public bool IsFocusInsideChild() => false;

        public void ConfigureSandbox(bool useSandbox, SandboxSettings settings)
        {
            _sandboxSettings = settings;
            // Sandboxing not implemented for cross-platform Avalonia host
        }

        public void ConfigureCrossplatformSettings(CrossplatformSettings settings)
        {
            _crossplatformSettings = settings;
        }

        public void NotifyParentWindowStateChanged(HostWindowState newState)
        {
            if (newState == HostWindowState.Minimized)
                SendVisibilityAsync(false).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
            else if (_allowChildVisible)
                UpdatePosition();
        }

        public async Task SetCursorVisibilityAsync(bool isVisible)
        {
            await SafeSendControlAsync(new IpcEnvelope(
                IpcTypes.CursorVisible,
                Guid.NewGuid().ToString("N"),
                Json.ToJson(new IsCursorVisible(isVisible ? 1 : 0))));
        }

        public async Task SetChildModalityAsync(bool isModal)
        {
            await SafeSendControlAsync(new IpcEnvelope(
                IpcTypes.SetChildModal,
                Guid.NewGuid().ToString("N"),
                Json.ToJson(new IsChildModal(isModal ? 1 : 0))));
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<HotReloadResultMessage?>> _pendingHotReloads = new();

        public string? HotReloadCapabilities { get; private set; }

        public async Task<HotReloadResultMessage?> HotReloadAsync(HotReloadRequest request, IEnumerable<string> dependencyFiles)
        {
            if (_messenger == null || _pipeStream == null || !_pipeStream.IsConnected) return null;

            var correlationId = Guid.NewGuid().ToString("N");
            var pending = new TaskCompletionSource<HotReloadResultMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingHotReloads[correlationId] = pending;
            try
            {
                // A package added with this change: its assemblies have to be there before the
                // patched code first touches them.
                foreach (var file in _sentFiles.Unsent(dependencyFiles))
                {
                    await SendFileAsBlobAsync(file, _pipeCts.Token);
                    _sentFiles.MarkSent(file);
                }

                await SafeSendControlAsync(new IpcEnvelope(IpcTypes.HotReload, correlationId, Json.ToJson(request)));
                var finished = await Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                return finished == pending.Task ? await pending.Task : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HotReloadAsync] Failed: {ex.Message}");
                return null;
            }
            finally
            {
                _pendingHotReloads.TryRemove(correlationId, out _);
            }
        }

        public event Func<string, string?, Task<string?>>? AppMessageReceived;

        public Task SetAppWindowAsync(AppWindowMessage message) =>
            SafeSendControlAsync(new IpcEnvelope(IpcTypes.AppWindow, "", Json.ToJson(message)));

        public double? AppWindowWidth => ChildWindowBounds?.Width;

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingAppState = new();

        public async Task<AppStateCapturedMessage?> CaptureAppStateAsync() =>
            await AskForAppStateAsync(IpcTypes.CaptureAppState, "{}") is { } json ? Json.FromJson<AppStateCapturedMessage>(json) : null;

        public async Task<AppStateRestoredMessage?> RestoreAppStateAsync(RestoreAppStateRequest request) =>
            await AskForAppStateAsync(IpcTypes.RestoreAppState, Json.ToJson(request)) is { } json ? Json.FromJson<AppStateRestoredMessage>(json) : null;

        public async Task<string?> DescribeForLinkAsync(string code) =>
            await AskForAppStateAsync(IpcTypes.LinkDescribe, Json.ToJson(new LinkDescribeRequest(code))) is { } json ? Json.FromJson<LinkDescribedMessage>(json)?.Map : null;

        public async Task<LinkStartedMessage?> StartLinkAsync(LinkStartRequest request) =>
            await AskForAppStateAsync(IpcTypes.LinkStart, Json.ToJson(request)) is { } json ? Json.FromJson<LinkStartedMessage>(json) : null;

        private async Task<string?> AskForAppStateAsync(string type, string payload)
        {
            if (_messenger == null || _pipeStream == null || !_pipeStream.IsConnected) return null;

            var correlationId = Guid.NewGuid().ToString("N");
            var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAppState[correlationId] = pending;
            try
            {
                await SafeSendControlAsync(new IpcEnvelope(type, correlationId, payload));
                var finished = await Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                return finished == pending.Task ? await pending.Task : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppState] {type} failed: {ex.Message}");
                return null;
            }
            finally
            {
                _pendingAppState.TryRemove(correlationId, out _);
            }
        }

        public async Task UnloadControlAsync()
        {
            await SafeSendControlAsync(new IpcEnvelope(
                IpcTypes.UnloadControl,
                Guid.NewGuid().ToString("N"),
                "{}"));
            _hasLoadedControl = false;
        }

        public object? CaptureChildScreenshot() => null;

        public bool CanStayInUseWhileGenerating => true;

        public void SendToggleFullScreenAsync()
        {
            SafeSendControlAsync(new IpcEnvelope(
                IpcTypes.ToggleChildFullScreen,
                Guid.NewGuid().ToString("N"),
                "{}")).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
        }

        public void HideChild()
        {
            _allowChildVisible = false;
            SendVisibilityAsync(false).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
        }

        public void ShowChild()
        {
            _allowChildVisible = true;
            SendVisibilityAsync(true).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
            UpdatePosition();
        }

        private async Task SendVisibilityAsync(bool visible)
        {
            if (_messenger == null || _pipeStream == null || !_pipeStream.IsConnected) return;
            try
            {
                var msg = new ParentWindowBoundsMessage(0, 0, 0, 0, visible, Dock: false);
                await SafeSendControlAsync(new IpcEnvelope(
                    IpcTypes.ParentWindowBounds, "", Json.ToJson(msg)));
            }
            catch { }
        }

        private async Task DisposeChildAsync()
        {
            Debug.WriteLine("[ChildProcess] DisposeChild: start");
            _isShuttingDown = true;

            Debug.WriteLine("[ChildProcess] DisposeChild: cancelling CTS");
            try { _pipeCts.Cancel(); } catch { }

            // Wait for listen loop to fully exit before disposing resources
            if (_listenLoopTask != null)
            {
                Debug.WriteLine("[ChildProcess] DisposeChild: waiting for listen loop (3s max)...");
                try { await _listenLoopTask.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch { Debug.WriteLine("[ChildProcess] DisposeChild: listen loop wait timed out or failed"); }
                _listenLoopTask = null;
            }

            if (_pipeStream != null)
            {
                Debug.WriteLine("[ChildProcess] DisposeChild: disposing pipe");
                try
                {
                    if (_pipeStream.IsConnected)
                        _pipeStream.Disconnect();
                }
                catch { }
                try { _pipeStream.Dispose(); } catch { }
                _pipeStream = null;
                _messenger = null;
            }

            if (_childProcess != null && !_childProcess.HasExited)
            {
                Debug.WriteLine("[ChildProcess] DisposeChild: killing child process");
                try
                {
                    try { _childProcess.CloseMainWindow(); } catch { }

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await _childProcess.WaitForExitAsync(cts.Token); }
                    catch (OperationCanceledException) { }

                    if (!_childProcess.HasExited)
                        _childProcess.Kill(entireProcessTree: true);
                }
                catch { }
                _childProcess.Dispose();
                _childProcess = null;
            }

            _hasLoadedControl = false;
            Debug.WriteLine("[ChildProcess] DisposeChild: done");
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            await DisposeChildAsync();
        }
    }
}
