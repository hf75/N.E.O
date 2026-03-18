using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
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
        private Process? _childProcess;
        private CancellationTokenSource? _listenCts;
        private bool _hasLoadedControl;
        private bool _isDisposed;
        private SandboxSettings? _sandboxSettings;
        private CrossplatformSettings? _crossplatformSettings;

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
            await DisposeChildAsync();

            var pipeName = $"neo_avalonia_{Guid.NewGuid():N}";

            _pipeStream = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            _messenger = new FramedPipeMessenger(_pipeStream);

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var childExe = Path.Combine(baseDir, "Neo.PluginWindowAvalonia");

            if (OperatingSystem.IsWindows())
                childExe += ".exe";

            if (!File.Exists(childExe))
            {
                var altPath = Path.Combine(baseDir, "..", "Neo.PluginWindowAvalonia",
                    "bin", "Debug", "net9.0", "Neo.PluginWindowAvalonia");
                if (OperatingSystem.IsWindows()) altPath += ".exe";
                if (File.Exists(altPath))
                    childExe = altPath;
            }

            var psi = new ProcessStartInfo
            {
                FileName = childExe,
                Arguments = $"--pipe {pipeName}",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            try
            {
                _childProcess = Process.Start(psi);
                if (_childProcess == null)
                {
                    Debug.WriteLine("[AvaloniaChildProcessService] Failed to start child process.");
                    return;
                }

                await _pipeStream.WaitForConnectionAsync();

                _listenCts = new CancellationTokenSource();
                _ = Task.Run(() => ListenLoopAsync(_listenCts.Token));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AvaloniaChildProcessService] RestartAsync failed: {ex.Message}");
            }
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
                                break;

                            default:
                                Debug.WriteLine($"[AvaloniaChildProcessService] Unknown IPC type: {env.Type}");
                                break;
                        }
                    },
                    onBlobStart: null,
                    onBlobChunk: null,
                    onBlobEnd: null,
                    ct: ct);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AvaloniaChildProcessService] ListenLoop error: {ex.Message}");
            }
        }

        private async Task SafeSendControlAsync(IpcEnvelope env)
        {
            if (_messenger == null) return;
            await _sendLock.WaitAsync();
            try
            {
                await _messenger.SendControlAsync(env);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AvaloniaChildProcessService] SendAsync failed: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task SendBlobFromBytesAsync(string name, byte[] data)
        {
            if (_messenger == null) return;

            var corrId = Guid.NewGuid();
            var meta = new BlobStartMeta(name, data.Length);

            int offset = 0;
            await _sendLock.WaitAsync();
            try
            {
                await _messenger.SendBlobAsync(
                    corrId,
                    meta,
                    (buffer, ct) =>
                    {
                        int remaining = data.Length - offset;
                        int toCopy = Math.Min(remaining, buffer.Length);
                        if (toCopy <= 0)
                            return new ValueTask<int>(0);
                        data.AsSpan(offset, toCopy).CopyTo(buffer.Span);
                        offset += toCopy;
                        return new ValueTask<int>(toCopy);
                    });
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void UpdatePosition(bool useTopMostTrick = false)
        {
            // No HWND embedding in Avalonia version
        }

        public async Task<bool> DisplayControlAsync(string mainDllPath, IEnumerable<string> nugetDlls, IEnumerable<string> additionalDlls)
        {
            if (_messenger == null || _pipeStream == null || !_pipeStream.IsConnected) return false;

            try
            {
                var mainBytes = await File.ReadAllBytesAsync(mainDllPath);
                var mainName = Path.GetFileName(mainDllPath);
                await SendBlobFromBytesAsync(mainName, mainBytes);

                var nugetList = new List<string>();
                foreach (var nugetDll in nugetDlls)
                {
                    if (File.Exists(nugetDll))
                    {
                        var bytes = await File.ReadAllBytesAsync(nugetDll);
                        await SendBlobFromBytesAsync(Path.GetFileName(nugetDll), bytes);
                        nugetList.Add(Path.GetFileName(nugetDll));
                    }
                }

                foreach (var additionalDll in additionalDlls)
                {
                    if (File.Exists(additionalDll))
                    {
                        var bytes = await File.ReadAllBytesAsync(additionalDll);
                        await SendBlobFromBytesAsync(Path.GetFileName(additionalDll), bytes);
                    }
                }

                var req = new LoadControlRequest(mainDllPath, "", nugetList);
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
        }

        public void ConfigureCrossplatformSettings(CrossplatformSettings settings)
        {
            _crossplatformSettings = settings;
        }

        public void NotifyParentWindowStateChanged(HostWindowState newState) { }

        public async Task SetCursorVisibilityAsync(bool isVisible)
        {
            var msg = new IsCursorVisible(isVisible ? 1 : 0);
            await SafeSendControlAsync(new IpcEnvelope(
                IpcTypes.CursorVisible,
                Guid.NewGuid().ToString("N"),
                Json.ToJson(msg)));
        }

        public async Task SetChildModalityAsync(bool isModal)
        {
            var msg = new IsChildModal(isModal ? 1 : 0);
            await SafeSendControlAsync(new IpcEnvelope(
                IpcTypes.SetChildModal,
                Guid.NewGuid().ToString("N"),
                Json.ToJson(msg)));
        }

        public async Task SetDesignerModeAsync(bool enabled)
        {
            var msg = new SetDesignerModeMessage(enabled);
            await SafeSendControlAsync(new IpcEnvelope(
                IpcTypes.SetDesignerMode,
                Guid.NewGuid().ToString("N"),
                Json.ToJson(msg)));
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

        public void HideChild() { }

        public void ShowChild() { }

        private async Task DisposeChildAsync()
        {
            try { _listenCts?.Cancel(); } catch { }

            if (_pipeStream != null)
            {
                try { _pipeStream.Dispose(); } catch { }
                _pipeStream = null;
                _messenger = null;
            }

            if (_childProcess != null && !_childProcess.HasExited)
            {
                try
                {
                    _childProcess.Kill(entireProcessTree: true);
                    await _childProcess.WaitForExitAsync();
                }
                catch { }
                _childProcess.Dispose();
                _childProcess = null;
            }

            _hasLoadedControl = false;
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            await DisposeChildAsync();
        }
    }
}
