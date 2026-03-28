using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using Neo.IPC;

namespace Neo.McpServer.Services;

/// <summary>
/// Manages a single Neo.PluginWindowAvalonia child process and its Named Pipe connection.
/// This is the headless equivalent of AvaloniaChildProcessService — no UI host needed.
/// </summary>
public sealed class PreviewSessionManager : IAsyncDisposable
{
    private NamedPipeServerStream? _pipeStream;
    private FramedPipeMessenger? _messenger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Process? _childProcess;
    private Task? _listenLoopTask;
    private CancellationTokenSource _pipeCts = new();
    private int _sessionCounter;
    private bool _isShuttingDown;
    private volatile bool _helloReceived;
    private readonly List<string> _childLogs = new();
    private readonly List<string> _runtimeErrors = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcEnvelope>> _pendingRequests = new();

    public bool IsRunning => _childProcess is { HasExited: false } && _pipeStream is { IsConnected: true };
    public IReadOnlyList<string> ChildLogs => _childLogs;

    /// <summary>
    /// Runtime errors received from the child process since the last DLL load.
    /// These are exceptions thrown by the user's generated code.
    /// </summary>
    public IReadOnlyList<string> RuntimeErrors => _runtimeErrors;

    /// <summary>
    /// Starts Neo.PluginWindowAvalonia in standalone mode and connects via Named Pipe.
    /// </summary>
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return true;

        await DisposeChildAsync();

        _pipeCts = new CancellationTokenSource();
        _sessionCounter++;
        _isShuttingDown = false;
        _helloReceived = false;
        _childLogs.Clear();

        var pipeName = $"neo_mcp_{Environment.ProcessId}_{_sessionCounter}";

        _pipeStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        _messenger = new FramedPipeMessenger(_pipeStream);

        var childPath = FindChildExecutable();
        if (childPath == null)
        {
            _childLogs.Add("ERROR: Neo.PluginWindowAvalonia executable not found.");
            return false;
        }

        var childArgs = $"--pipe {pipeName} --standalone";

        var psi = new ProcessStartInfo
        {
            FileName = childPath.Value.useDotnet ? "dotnet" : childPath.Value.exePath,
            Arguments = childPath.Value.useDotnet
                ? $"\"{childPath.Value.exePath}\" {childArgs}"
                : childArgs,
            UseShellExecute = false,
            CreateNoWindow = false,
        };

        try
        {
            System.Diagnostics.Debug.WriteLine($"[PreviewSession] Starting: {psi.FileName} {psi.Arguments}");
            _childProcess = Process.Start(psi);
            if (_childProcess == null)
            {
                _childLogs.Add("ERROR: Failed to start child process.");
                return false;
            }
            System.Diagnostics.Debug.WriteLine($"[PreviewSession] Child started PID={_childProcess.Id}, waiting for pipe...");

            // Wait for pipe connection with timeout
            var connectTask = _pipeStream.WaitForConnectionAsync(_pipeCts.Token);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), _pipeCts.Token);
            if (await Task.WhenAny(connectTask, timeoutTask) != connectTask)
            {
                _childLogs.Add("ERROR: Child process connection timed out (10s).");
                System.Diagnostics.Debug.WriteLine("[PreviewSession] TIMEOUT waiting for pipe connection!");
                try { _pipeStream.Dispose(); } catch { }
                _pipeStream = null;
                _messenger = null;
                return false;
            }
            await connectTask; // propagate any exception
            System.Diagnostics.Debug.WriteLine("[PreviewSession] Pipe connected!");

            // Start listen loop
            _listenLoopTask = Task.Run(() => ListenLoopAsync(_pipeCts.Token));

            // Wait for child to send Hello (it starts the listen loop after Opened event)
            // Without this, we'd send blobs before the child is reading, causing pipe buffer to fill and block.
            System.Diagnostics.Debug.WriteLine("[PreviewSession] Waiting for child Hello...");
            var helloDeadline = DateTime.UtcNow.AddSeconds(10);
            while (!_helloReceived && DateTime.UtcNow < helloDeadline)
                await Task.Delay(100, _pipeCts.Token);

            if (!_helloReceived)
            {
                System.Diagnostics.Debug.WriteLine("[PreviewSession] WARNING: No Hello received, proceeding anyway...");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[PreviewSession] Hello received from child!");
            }

            _childLogs.Add($"Preview window started (PID: {_childProcess.Id}).");
            System.Diagnostics.Debug.WriteLine($"[PreviewSession] Ready. Sending DLL next...");
            return true;
        }
        catch (Exception ex)
        {
            _childLogs.Add($"ERROR: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sends DLL bytes + dependencies to the running PluginWindow and triggers LoadControl.
    /// </summary>
    public async Task<bool> SendDllAsync(
        byte[] mainDllBytes,
        string mainDllName,
        Dictionary<string, byte[]>? dependencyDlls = null,
        CancellationToken ct = default)
    {
        if (!IsRunning || _messenger == null)
            return false;

        try
        {
            // Clear runtime errors from previous load
            _runtimeErrors.Clear();

            // Send all dependency DLLs first
            if (dependencyDlls != null)
            {
                foreach (var (name, bytes) in dependencyDlls)
                {
                    System.Diagnostics.Debug.WriteLine($"[PreviewSession] Sending dep: {name} ({bytes.Length} bytes)");
                    await SendBlobAsync(name, bytes, ct);
                }
            }

            // Send main DLL
            System.Diagnostics.Debug.WriteLine("[PreviewSession] Sending main DLL...");
            await SendBlobAsync(mainDllName, mainDllBytes, ct);
            System.Diagnostics.Debug.WriteLine($"[PreviewSession] Sending LoadControl... pipe connected={_pipeStream?.IsConnected}");

            // Send LoadControl command
            var req = new LoadControlRequest(
                mainDllName,
                "DynamicUserControl",
                dependencyDlls?.Keys.ToList() ?? new List<string>());

            await SafeSendControlAsync(new IpcEnvelope(
                IpcTypes.LoadControl,
                Guid.NewGuid().ToString("N"),
                Json.ToJson(req)));

            _childLogs.Add($"DLL loaded: {mainDllName} ({mainDllBytes.Length:N0} bytes).");
            return true;
        }
        catch (Exception ex)
        {
            _childLogs.Add($"ERROR sending DLL: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Unloads the current control, sends a new DLL, and reloads.
    /// </summary>
    public async Task<bool> UpdateAsync(
        byte[] mainDllBytes,
        string mainDllName,
        Dictionary<string, byte[]>? dependencyDlls = null,
        CancellationToken ct = default)
    {
        if (!IsRunning || _messenger == null)
            return false;

        // Unload current control
        await SafeSendControlAsync(new IpcEnvelope(
            IpcTypes.UnloadControl,
            Guid.NewGuid().ToString("N"),
            "{}"));

        // Small delay to let the child process clean up
        await Task.Delay(100, ct);

        return await SendDllAsync(mainDllBytes, mainDllName, dependencyDlls, ct);
    }

    /// <summary>
    /// Captures a screenshot of the running preview window as Base64 PNG.
    /// </summary>
    public async Task<ScreenshotResultMessage?> CaptureScreenshotAsync(CancellationToken ct = default)
    {
        if (!IsRunning || _messenger == null)
            return null;

        var corrId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<IpcEnvelope>();
        _pendingRequests[corrId] = tcs;

        try
        {
            await SafeSendControlAsync(new IpcEnvelope(
                IpcTypes.CaptureScreenshot, corrId, "{}"));

            // Wait for ScreenshotResult with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            cts.Token.Register(() => tcs.TrySetCanceled());

            var response = await tcs.Task;

            if (response.Type == IpcTypes.ScreenshotResult)
                return Json.FromJson<ScreenshotResultMessage>(response.PayloadJson);

            return null;
        }
        catch (OperationCanceledException)
        {
            _childLogs.Add("Screenshot timed out.");
            return null;
        }
        catch (Exception ex)
        {
            _childLogs.Add($"Screenshot failed: {ex.Message}");
            return null;
        }
        finally
        {
            _pendingRequests.TryRemove(corrId, out _);
        }
    }

    /// <summary>
    /// Sets a property on a control in the running app without recompilation.
    /// </summary>
    public async Task<SetPropertyResultMessage?> SetPropertyAsync(
        SetPropertyRequest request, CancellationToken ct = default)
    {
        if (!IsRunning || _messenger == null)
            return new SetPropertyResultMessage(false, "No preview is running.");

        var corrId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<IpcEnvelope>();
        _pendingRequests[corrId] = tcs;

        try
        {
            await SafeSendControlAsync(new IpcEnvelope(
                IpcTypes.SetProperty, corrId, Json.ToJson(request)));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            cts.Token.Register(() => tcs.TrySetCanceled());

            var response = await tcs.Task;

            if (response.Type == IpcTypes.SetPropertyResult)
                return Json.FromJson<SetPropertyResultMessage>(response.PayloadJson);

            return new SetPropertyResultMessage(false, "Unexpected response.");
        }
        catch (OperationCanceledException)
        {
            return new SetPropertyResultMessage(false, "SetProperty timed out.");
        }
        catch (Exception ex)
        {
            return new SetPropertyResultMessage(false, $"SetProperty failed: {ex.Message}");
        }
        finally
        {
            _pendingRequests.TryRemove(corrId, out _);
        }
    }

    /// <summary>
    /// Closes the preview window and cleans up.
    /// </summary>
    public async Task StopAsync()
    {
        await DisposeChildAsync();
        _childLogs.Add("Preview window closed.");
    }

    // ========================================
    // Blob Streaming
    // ========================================
    private async Task SendBlobAsync(string name, byte[] data, CancellationToken ct)
    {
        if (_messenger == null) return;

        // Write to temp file first, then stream from file (matches AvaloniaChildProcessService pattern)
        var tempPath = Path.Combine(Path.GetTempPath(), "neo_mcp_blob", name);
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        await File.WriteAllBytesAsync(tempPath, data, ct);

        var corr = Guid.NewGuid();
        var logicalName = Path.GetFileName(tempPath);

        await using var fs = File.Open(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var meta = new BlobStartMeta(Name: name, Length: fs.Length);

        await _sendLock.WaitAsync(ct);
        try
        {
            await _messenger.SendBlobAsync(
                correlationId: corr,
                meta: meta,
                read: async (buffer, token) => await fs.ReadAsync(buffer, token),
                chunkSize: 512 * 1024,
                ct: ct);
            System.Diagnostics.Debug.WriteLine($"[PreviewSession] Blob sent: {name} ({data.Length} bytes)");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SafeSendControlAsync(IpcEnvelope env)
    {
        if (_messenger == null || _pipeStream == null || !_pipeStream.IsConnected) return;
        await _sendLock.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _messenger.SendControlAsync(env, cts.Token);
            System.Diagnostics.Debug.WriteLine($"[PreviewSession] SafeSendControl: {env.Type} sent OK");
        }
        catch (Exception ex)
        {
            _childLogs.Add($"Send failed: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[PreviewSession] SafeSendControl FAILED: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ========================================
    // Listen Loop (receives Hello, Ack, Error, Log from child)
    // ========================================
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
                            _helloReceived = true;
                            System.Diagnostics.Debug.WriteLine("[PreviewSession] ListenLoop: Hello received!");
                            await SafeSendControlAsync(new IpcEnvelope(
                                IpcTypes.Ack, env.CorrelationId,
                                Json.ToJson(new AckMessage("Hello acknowledged by MCP server"))));
                            break;

                        case IpcTypes.Log:
                            var logMsg = Json.FromJson<LogMessage>(env.PayloadJson);
                            if (logMsg != null)
                                _childLogs.Add($"[{logMsg.Level}] {logMsg.Message}");
                            break;

                        case IpcTypes.Error:
                            var errMsg = Json.FromJson<ErrorMessage>(env.PayloadJson);
                            if (errMsg != null)
                            {
                                var errorText = $"{errMsg.ExceptionType}: {errMsg.Message}";
                                _childLogs.Add($"[CHILD ERROR] {errorText}\n{errMsg.StackTrace}");
                                _runtimeErrors.Add(errorText);

                                // Complete pending request if this was a response
                                if (!string.IsNullOrEmpty(env.CorrelationId) &&
                                    _pendingRequests.TryRemove(env.CorrelationId, out var errTcs))
                                    errTcs.TrySetResult(env);
                            }
                            break;

                        case IpcTypes.ScreenshotResult:
                        case IpcTypes.SetPropertyResult:
                            // Complete pending request
                            if (!string.IsNullOrEmpty(env.CorrelationId) &&
                                _pendingRequests.TryRemove(env.CorrelationId, out var resultTcs))
                                resultTcs.TrySetResult(env);
                            break;

                        case IpcTypes.Heartbeat:
                        case IpcTypes.Ack:
                        case IpcTypes.NotifyFirstChildVisibility:
                        case IpcTypes.ChildActivated:
                            break;

                        default:
                            _childLogs.Add($"[IPC] Unknown: {env.Type}");
                            break;
                    }
                },
                onBlobStart: (_, _) => Task.CompletedTask,
                onBlobChunk: (_, _) => Task.CompletedTask,
                onBlobEnd: (_) => Task.CompletedTask,
                ct: ct);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) when (_isShuttingDown) { }
        catch (Exception ex)
        {
            _childLogs.Add($"[ListenLoop] Disconnected: {ex.Message}");
        }
    }

    // ========================================
    // Child process discovery + cleanup
    // ========================================
    private (string exePath, bool useDotnet)? FindChildExecutable()
    {
        // 1. Check NEO_PLUGIN_PATH environment variable
        var envPath = Environment.GetEnvironmentVariable("NEO_PLUGIN_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var candidate = FindInDirectory(envPath);
            if (candidate != null) return candidate;
        }

        // 2. Check relative to this executable
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var fromBase = FindInDirectory(baseDir);
        if (fromBase != null) return fromBase;

        // 3. Dev-time: sibling project output
        foreach (var config in new[] { "Debug", "Release" })
        {
            var devDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                "Neo.PluginWindowAvalonia", "bin", config, "net9.0"));
            var devCandidate = FindInDirectory(devDir);
            if (devCandidate != null) return devCandidate;
        }

        return null;
    }

    private static (string exePath, bool useDotnet)? FindInDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        var nativeName = OperatingSystem.IsWindows()
            ? "Neo.PluginWindowAvalonia.exe"
            : "Neo.PluginWindowAvalonia";

        var nativePath = Path.Combine(dir, nativeName);
        if (File.Exists(nativePath)) return (nativePath, false);

        var dllPath = Path.Combine(dir, "Neo.PluginWindowAvalonia.dll");
        if (File.Exists(dllPath)) return (dllPath, true);

        return null;
    }

    private async Task DisposeChildAsync()
    {
        _isShuttingDown = true;

        try { _pipeCts.Cancel(); } catch { }

        if (_listenLoopTask != null)
        {
            try { await _listenLoopTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { }
            _listenLoopTask = null;
        }

        if (_pipeStream != null)
        {
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
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeChildAsync();
    }
}
