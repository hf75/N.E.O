using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
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
#pragma warning disable CS0414 // Field is assigned but never read — used by HideChild/ShowChild for future IPC visibility control
        private bool _allowChildVisible = true;
#pragma warning restore CS0414
        private SandboxSettings _sandboxSettings = SandboxSettings.MaximumSecurity;
        private CrossplatformSettings _crossplatformSettings = new();
        private int _sessionCounter;

        public bool HasLoadedControl => _hasLoadedControl;

        public event Func<CrashReason, ErrorMessage, Task>? ChildProcessCrashed;
        public event Action<LogMessage>? ChildLogReceived;
        public event Action<DesignerSelectionMessage>? DesignerSelectionReceived;

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

            var pipeName = $"neo_avalonia_{Environment.ProcessId}_{_sessionCounter}";

            _pipeStream = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            _messenger = new FramedPipeMessenger(_pipeStream);

            // Find child executable — always use Avalonia child from this host
            var childExe = FindChildExecutable();
            if (childExe == null)
            {
                Debug.WriteLine("[AvaloniaChildProcessService] Child executable not found!");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = childExe,
                Arguments = $"--pipe {pipeName} --parentPid {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = false,
            };

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

                Debug.WriteLine("[ChildProcess] StartCore: child connected, waiting for Hello...");

                // ReceiveControlAsync with real timeout
                var helloTask = _messenger.ReceiveControlAsync(_pipeCts.Token);
                var helloTimeout = Task.Delay(TimeSpan.FromSeconds(5), _pipeCts.Token);
                IpcEnvelope? helloEnv = null;
                if (await Task.WhenAny(helloTask, helloTimeout) == helloTask)
                    helloEnv = await helloTask;
                else
                    Debug.WriteLine("[ChildProcess] StartCore: Hello timed out (5s)");
                if (helloEnv?.Type == IpcTypes.Hello)
                {
                    // Reply with Hello ACK
                    await _messenger.SendControlAsync(new IpcEnvelope(
                        IpcTypes.Ack, helloEnv.CorrelationId,
                        Json.ToJson(new AckMessage("Hello received by Parent"))));

                    // Send our Hello
                    var parentHello = new HelloMessage("Parent", Environment.ProcessId);
                    await _messenger.SendControlAsync(new IpcEnvelope(
                        IpcTypes.Hello,
                        Guid.NewGuid().ToString("N"),
                        Json.ToJson(parentHello)));
                }

                // Start listen loop (save task for clean shutdown)
                _listenLoopTask = Task.Run(() => ListenLoopAsync(_pipeCts.Token));

                Debug.WriteLine($"[AvaloniaChildProcessService] Child process started (PID: {_childProcess.Id})");
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

        private string? FindChildExecutable()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var exeName = OperatingSystem.IsWindows()
                ? "Neo.PluginWindowAvalonia.exe"
                : "Neo.PluginWindowAvalonia";

            // 1. Same directory (published/deployed)
            var candidate = Path.Combine(baseDir, exeName);
            if (File.Exists(candidate)) return candidate;

            // 2. Dev-time: sibling project output
            var devPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                "Neo.PluginWindowAvalonia", "bin", "Debug", "net9.0", exeName));
            if (File.Exists(devPath)) return devPath;

            // 3. Try Release too
            var relPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                "Neo.PluginWindowAvalonia", "bin", "Release", "net9.0", exeName));
            if (File.Exists(relPath)) return relPath;

            Debug.WriteLine($"[AvaloniaChildProcessService] Tried: {candidate}");
            Debug.WriteLine($"[AvaloniaChildProcessService] Tried: {devPath}");
            return null;
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
                            case IpcTypes.Hello:
                                // Late Hello (re-handshake) — just ACK it
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
                                if (errMsg != null && ChildProcessCrashed != null)
                                    await ChildProcessCrashed.Invoke(CrashReason.UnhandledException, errMsg);
                                break;

                            case IpcTypes.DesignerSelection:
                                var selMsg = Json.FromJson<DesignerSelectionMessage>(env.PayloadJson);
                                if (selMsg != null)
                                    Dispatcher.UIThread.Post(() => DesignerSelectionReceived?.Invoke(selMsg));
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
            // No HWND embedding in Avalonia version — child is a separate window
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
                    await SendFileAsBlobAsync(dll, ct);

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
            // Could send IPC message to child to show/hide, but no HWND control
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

        public async Task SetDesignerModeAsync(bool enabled)
        {
            await SafeSendControlAsync(new IpcEnvelope(
                IpcTypes.SetDesignerMode,
                Guid.NewGuid().ToString("N"),
                Json.ToJson(new SetDesignerModeMessage(enabled))));
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

        public void HideChild()
        {
            _allowChildVisible = false;
        }

        public void ShowChild()
        {
            _allowChildVisible = true;
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
