using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using Neo.IPC;

namespace Neo.McpServer.Services;

/// <summary>
/// Manages one or more Neo.PluginWindowAvalonia child processes via Named Pipes.
/// Each window has a unique windowId. Default windowId is "default" for backward compatibility.
/// </summary>
public sealed class PreviewSessionManager : IAsyncDisposable
{
    private const string DefaultWindowId = "default";
    private readonly ConcurrentDictionary<string, WindowSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private int _sessionCounter;

    // ========================================
    // WindowSession — per-window state
    // ========================================
    private sealed class WindowSession : IAsyncDisposable
    {
        public string WindowId { get; }
        public NamedPipeServerStream? PipeStream { get; set; }
        public FramedPipeMessenger? Messenger { get; set; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public Process? ChildProcess { get; set; }
        public Task? ListenLoopTask { get; set; }
        public CancellationTokenSource PipeCts { get; set; } = new();
        public bool IsShuttingDown { get; set; }
        public volatile bool HelloReceived;
        public readonly object LogLock = new();
        public readonly List<string> ChildLogs = new();
        public readonly List<string> RuntimeErrors = new();
        public readonly ConcurrentDictionary<string, TaskCompletionSource<IpcEnvelope>> PendingRequests = new();
        public string? LastWebBridgeHtml { get; set; }

        public bool IsRunning => ChildProcess is { HasExited: false } && PipeStream is { IsConnected: true };

        public WindowSession(string windowId) => WindowId = windowId;

        public void AddLog(string message) { lock (LogLock) ChildLogs.Add(message); }
        public void AddRuntimeError(string error) { lock (LogLock) RuntimeErrors.Add(error); }

        public async ValueTask DisposeAsync()
        {
            IsShuttingDown = true;
            try { PipeCts.Cancel(); } catch { }

            if (ListenLoopTask != null)
            {
                try { await ListenLoopTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
                ListenLoopTask = null;
            }

            if (PipeStream != null)
            {
                try { if (PipeStream.IsConnected) PipeStream.Disconnect(); } catch { }
                try { PipeStream.Dispose(); } catch { }
                PipeStream = null;
                Messenger = null;
            }

            if (ChildProcess != null && !ChildProcess.HasExited)
            {
                try
                {
                    try { ChildProcess.CloseMainWindow(); } catch { }
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await ChildProcess.WaitForExitAsync(cts.Token); }
                    catch (OperationCanceledException) { }
                    if (!ChildProcess.HasExited)
                        ChildProcess.Kill(entireProcessTree: true);
                }
                catch { }
                ChildProcess.Dispose();
                ChildProcess = null;
            }
        }
    }

    // ========================================
    // Public API — all methods accept optional windowId
    // ========================================

    public bool IsRunning(string? windowId = null) => GetSession(windowId)?.IsRunning ?? false;

    public IReadOnlyList<string> GetChildLogs(string? windowId = null)
    {
        var s = GetSession(windowId);
        if (s == null) return Array.Empty<string>();
        lock (s.LogLock) return s.ChildLogs.ToList();
    }

    public IReadOnlyList<string> GetRuntimeErrors(string? windowId = null)
    {
        var s = GetSession(windowId);
        if (s == null) return Array.Empty<string>();
        lock (s.LogLock) return s.RuntimeErrors.ToList();
    }

    public string? GetLastWebBridgeHtml(string? windowId = null) => GetSession(windowId)?.LastWebBridgeHtml;

    /// <summary>Returns all active window IDs.</summary>
    public IReadOnlyList<string> GetRunningWindowIds() =>
        _sessions.Where(kv => kv.Value.IsRunning).Select(kv => kv.Key).ToList();

    /// <summary>Returns all window IDs (including stopped).</summary>
    public IReadOnlyList<string> GetAllWindowIds() => _sessions.Keys.ToList();

    // ========================================
    // Backward-compatible properties (delegate to default session)
    // ========================================
    [Obsolete("Use IsRunning(windowId) instead")]
    public bool IsRunningDefault => IsRunning();
    public IReadOnlyList<string> ChildLogs => GetChildLogs();
    public IReadOnlyList<string> RuntimeErrors => GetRuntimeErrors();
    public string? LastWebBridgeHtml => GetLastWebBridgeHtml();

    // ========================================
    // Start / Stop
    // ========================================

    public async Task<bool> StartAsync(string? windowId = null, CancellationToken ct = default)
    {
        windowId ??= DefaultWindowId;

        // If already running, return true
        if (GetSession(windowId)?.IsRunning == true) return true;

        // Dispose old session if exists
        if (_sessions.TryRemove(windowId, out var oldSession))
            await oldSession.DisposeAsync();

        var session = new WindowSession(windowId);
        _sessions[windowId] = session;

        session.PipeCts = new CancellationTokenSource();
        var counter = Interlocked.Increment(ref _sessionCounter);

        var pipeName = $"neo_mcp_{Environment.ProcessId}_{counter}";

        session.PipeStream = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        session.Messenger = new FramedPipeMessenger(session.PipeStream);

        var childPath = FindChildExecutable();
        if (childPath == null)
        {
            session.AddLog("ERROR: Neo.PluginWindowAvalonia executable not found.");
            return false;
        }

        var childArgs = $"--pipe {pipeName} --standalone";
        var psi = new ProcessStartInfo
        {
            FileName = childPath.Value.useDotnet ? "dotnet" : childPath.Value.exePath,
            Arguments = childPath.Value.useDotnet
                ? $"\"{childPath.Value.exePath}\" {childArgs}" : childArgs,
            UseShellExecute = false,
            CreateNoWindow = false,
        };

        try
        {
            session.ChildProcess = Process.Start(psi);
            if (session.ChildProcess == null)
            {
                session.AddLog("ERROR: Failed to start child process.");
                return false;
            }

            var connectTask = session.PipeStream.WaitForConnectionAsync(session.PipeCts.Token);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), session.PipeCts.Token);
            if (await Task.WhenAny(connectTask, timeoutTask) != connectTask)
            {
                session.AddLog("ERROR: Child process connection timed out (10s).");
                try { session.PipeStream.Dispose(); } catch { }
                session.PipeStream = null;
                session.Messenger = null;
                return false;
            }
            await connectTask;

            session.ListenLoopTask = Task.Run(() => ListenLoopAsync(session, session.PipeCts.Token));

            var helloDeadline = DateTime.UtcNow.AddSeconds(10);
            while (!session.HelloReceived && DateTime.UtcNow < helloDeadline)
                await Task.Delay(100, session.PipeCts.Token);

            session.AddLog($"Preview window '{windowId}' started (PID: {session.ChildProcess.Id}).");
            return true;
        }
        catch (Exception ex)
        {
            session.AddLog($"ERROR: {ex.Message}");
            return false;
        }
    }

    public async Task StopAsync(string? windowId = null)
    {
        windowId ??= DefaultWindowId;
        if (_sessions.TryRemove(windowId, out var session))
        {
            await session.DisposeAsync();
            session.AddLog($"Preview window '{windowId}' closed.");
        }
    }

    public async Task StopAllAsync()
    {
        var ids = _sessions.Keys.ToList();
        foreach (var id in ids)
            await StopAsync(id);
    }

    // ========================================
    // DLL Send / Update
    // ========================================

    public async Task<bool> SendDllAsync(
        byte[] mainDllBytes, string mainDllName,
        Dictionary<string, byte[]>? dependencyDlls = null,
        CancellationToken ct = default, string? windowId = null)
    {
        var s = GetRunningSession(windowId);
        if (s == null) return false;

        try
        {
            lock (s.LogLock) s.RuntimeErrors.Clear();

            if (dependencyDlls != null)
                foreach (var (name, bytes) in dependencyDlls)
                    await SendBlobAsync(s, name, bytes, ct);

            await SendBlobAsync(s, mainDllName, mainDllBytes, ct);

            var req = new LoadControlRequest(
                mainDllName, "DynamicUserControl",
                dependencyDlls?.Keys.ToList() ?? new List<string>());

            await SafeSendControlAsync(s, new IpcEnvelope(
                IpcTypes.LoadControl, Guid.NewGuid().ToString("N"), Json.ToJson(req)));

            s.AddLog($"DLL loaded: {mainDllName} ({mainDllBytes.Length:N0} bytes).");
            return true;
        }
        catch (Exception ex)
        {
            s.AddLog($"ERROR sending DLL: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> UpdateAsync(
        byte[] mainDllBytes, string mainDllName,
        Dictionary<string, byte[]>? dependencyDlls = null,
        CancellationToken ct = default, string? windowId = null)
    {
        var s = GetRunningSession(windowId);
        if (s == null) return false;

        await SafeSendControlAsync(s, new IpcEnvelope(
            IpcTypes.UnloadControl, Guid.NewGuid().ToString("N"), "{}"));
        await Task.Delay(100, ct);

        return await SendDllAsync(mainDllBytes, mainDllName, dependencyDlls, ct, windowId);
    }

    // ========================================
    // IPC Request-Response methods
    // ========================================

    public async Task<ScreenshotResultMessage?> CaptureScreenshotAsync(
        CancellationToken ct = default, string? windowId = null)
    {
        var r = await SendRequestAsync(windowId, IpcTypes.CaptureScreenshot, "{}", 5, ct);
        return r?.Type == IpcTypes.ScreenshotResult
            ? Json.FromJson<ScreenshotResultMessage>(r.PayloadJson) : null;
    }

    public async Task<string?> InspectVisualTreeAsync(
        CancellationToken ct = default, string? windowId = null)
    {
        var r = await SendRequestAsync(windowId, IpcTypes.InspectVisualTree, "{}", 5, ct);
        return r?.Type == IpcTypes.InspectVisualTreeResult ? r.PayloadJson : null;
    }

    public async Task<string?> ExtractCodeAsync(
        CancellationToken ct = default, string? windowId = null)
    {
        var r = await SendRequestAsync(windowId, IpcTypes.ExtractCode, "{}", 10, ct);
        return r?.Type == IpcTypes.ExtractCodeResult ? r.PayloadJson : null;
    }

    public async Task<SetPropertyResultMessage?> SetPropertyAsync(
        SetPropertyRequest request, CancellationToken ct = default, string? windowId = null)
    {
        var r = await SendRequestAsync(windowId, IpcTypes.SetProperty, Json.ToJson(request), 5, ct);
        if (r == null) return new SetPropertyResultMessage(false, "No response.");
        return r.Type == IpcTypes.SetPropertyResult
            ? Json.FromJson<SetPropertyResultMessage>(r.PayloadJson)
            : new SetPropertyResultMessage(false, "Unexpected response.");
    }

    public async Task<InjectDataResult?> InjectDataAsync(
        InjectDataRequest request, CancellationToken ct = default, string? windowId = null)
    {
        var r = await SendRequestAsync(windowId, IpcTypes.InjectData, Json.ToJson(request), 15, ct);
        if (r == null) return new InjectDataResult(false, "No response.");
        return r.Type == IpcTypes.InjectDataResult
            ? Json.FromJson<InjectDataResult>(r.PayloadJson)
            : new InjectDataResult(false, "Unexpected response.");
    }

    public async Task<ReadDataResult?> ReadDataAsync(
        ReadDataRequest request, CancellationToken ct = default, string? windowId = null)
    {
        var r = await SendRequestAsync(windowId, IpcTypes.ReadData, Json.ToJson(request), 5, ct);
        if (r == null) return new ReadDataResult(false, "No response.");
        return r.Type == IpcTypes.ReadDataResult
            ? Json.FromJson<ReadDataResult>(r.PayloadJson)
            : new ReadDataResult(false, "Unexpected response.");
    }

    public async Task<StartWebBridgeResult?> StartWebBridgeAsync(
        StartWebBridgeRequest request, CancellationToken ct = default, string? windowId = null)
    {
        var s = GetRunningSession(windowId);
        if (s == null) return new StartWebBridgeResult(false, null, null, "No preview is running.");
        s.LastWebBridgeHtml = request.HtmlContent;

        var r = await SendRequestAsync(windowId, IpcTypes.StartWebBridge, Json.ToJson(request), 10, ct);
        if (r == null) return new StartWebBridgeResult(false, null, null, "No response.");
        return r.Type == IpcTypes.StartWebBridgeResult
            ? Json.FromJson<StartWebBridgeResult>(r.PayloadJson)
            : new StartWebBridgeResult(false, null, null, "Unexpected response.");
    }

    public async Task<bool> SendToWebBridgeAsync(
        string message, CancellationToken ct = default, string? windowId = null)
    {
        var s = GetRunningSession(windowId);
        if (s == null) return false;
        await SafeSendControlAsync(s, new IpcEnvelope(
            IpcTypes.SendToWebBridge, Guid.NewGuid().ToString("N"), message));
        return true;
    }

    public async Task StopWebBridgeAsync(CancellationToken ct = default, string? windowId = null)
    {
        var s = GetRunningSession(windowId);
        if (s == null) return;
        await SafeSendControlAsync(s, new IpcEnvelope(
            IpcTypes.StopWebBridge, Guid.NewGuid().ToString("N"), "{}"));
    }

    /// <summary>Positions a window at specific coordinates.</summary>
    public async Task PositionWindowAsync(string windowId, double x, double y, double width, double height)
    {
        var s = GetRunningSession(windowId);
        if (s == null) return;

        var bounds = new ParentWindowBoundsMessage(x, y, width, height, true);
        await SafeSendControlAsync(s, new IpcEnvelope(
            IpcTypes.PositionWindow, "", Json.ToJson(bounds)));
    }

    // ========================================
    // Generic request-response helper
    // ========================================

    private async Task<IpcEnvelope?> SendRequestAsync(
        string? windowId, string ipcType, string payloadJson, int timeoutSeconds,
        CancellationToken ct = default)
    {
        var s = GetRunningSession(windowId);
        if (s == null) return null;

        var corrId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<IpcEnvelope>();
        s.PendingRequests[corrId] = tcs;

        try
        {
            await SafeSendControlAsync(s, new IpcEnvelope(ipcType, corrId, payloadJson));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            cts.Token.Register(() => tcs.TrySetCanceled());

            return await tcs.Task;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            s.AddLog($"{ipcType} failed: {ex.Message}");
            return null;
        }
        finally
        {
            s.PendingRequests.TryRemove(corrId, out _);
        }
    }

    // ========================================
    // Blob Streaming
    // ========================================

    private static async Task SendBlobAsync(WindowSession s, string name, byte[] data, CancellationToken ct)
    {
        if (s.Messenger == null) return;

        var tempPath = Path.Combine(Path.GetTempPath(), "neo_mcp_blob", name);
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        await File.WriteAllBytesAsync(tempPath, data, ct);

        var corr = Guid.NewGuid();
        await using var fs = File.Open(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var meta = new BlobStartMeta(Name: name, Length: fs.Length);

        await s.SendLock.WaitAsync(ct);
        try
        {
            await s.Messenger.SendBlobAsync(
                correlationId: corr, meta: meta,
                read: async (buffer, token) => await fs.ReadAsync(buffer, token),
                chunkSize: 512 * 1024, ct: ct);
        }
        finally { s.SendLock.Release(); }

        try { File.Delete(tempPath); } catch { }
    }

    private static async Task SafeSendControlAsync(WindowSession s, IpcEnvelope env)
    {
        if (s.Messenger == null || s.PipeStream == null || !s.PipeStream.IsConnected) return;
        await s.SendLock.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await s.Messenger.SendControlAsync(env, cts.Token);
        }
        catch (Exception ex)
        {
            s.AddLog($"Send failed: {ex.Message}");
        }
        finally { s.SendLock.Release(); }
    }

    // ========================================
    // Listen Loop
    // ========================================

    private static async Task ListenLoopAsync(WindowSession s, CancellationToken ct)
    {
        if (s.Messenger == null) return;

        try
        {
            await s.Messenger.ReceiveLoopAsync(
                onControl: async env =>
                {
                    switch (env.Type)
                    {
                        case IpcTypes.Hello:
                            s.HelloReceived = true;
                            await SafeSendControlAsync(s, new IpcEnvelope(
                                IpcTypes.Ack, env.CorrelationId,
                                Json.ToJson(new AckMessage("Hello acknowledged"))));
                            break;

                        case IpcTypes.Log:
                            var logMsg = Json.FromJson<LogMessage>(env.PayloadJson);
                            if (logMsg != null) s.AddLog($"[{logMsg.Level}] {logMsg.Message}");
                            break;

                        case IpcTypes.Error:
                            var errMsg = Json.FromJson<ErrorMessage>(env.PayloadJson);
                            if (errMsg != null)
                            {
                                var errorText = $"{errMsg.ExceptionType}: {errMsg.Message}";
                                s.AddLog($"[CHILD ERROR] {errorText}\n{errMsg.StackTrace}");
                                s.AddRuntimeError(errorText);
                                if (!string.IsNullOrEmpty(env.CorrelationId) &&
                                    s.PendingRequests.TryRemove(env.CorrelationId, out var errTcs))
                                    errTcs.TrySetResult(env);
                            }
                            break;

                        case IpcTypes.ScreenshotResult:
                        case IpcTypes.SetPropertyResult:
                        case IpcTypes.InspectVisualTreeResult:
                        case IpcTypes.InjectDataResult:
                        case IpcTypes.ReadDataResult:
                        case IpcTypes.ExtractCodeResult:
                        case IpcTypes.StartWebBridgeResult:
                        case IpcTypes.Ack:
                            if (!string.IsNullOrEmpty(env.CorrelationId) &&
                                s.PendingRequests.TryRemove(env.CorrelationId, out var resultTcs))
                                resultTcs.TrySetResult(env);
                            break;

                        case IpcTypes.Heartbeat:
                        case IpcTypes.NotifyFirstChildVisibility:
                        case IpcTypes.ChildActivated:
                            break;

                        default:
                            s.AddLog($"[IPC] Unknown: {env.Type}");
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
        catch (IOException) when (s.IsShuttingDown) { }
        catch (Exception ex)
        {
            s.AddLog($"[ListenLoop] Disconnected: {ex.Message}");
        }
    }

    // ========================================
    // Session lookup helpers
    // ========================================

    private WindowSession? GetSession(string? windowId) =>
        _sessions.TryGetValue(windowId ?? DefaultWindowId, out var s) ? s : null;

    private WindowSession? GetRunningSession(string? windowId)
    {
        var s = GetSession(windowId);
        return s?.IsRunning == true ? s : null;
    }

    // ========================================
    // Child process discovery
    // ========================================

    private static (string exePath, bool useDotnet)? FindChildExecutable()
    {
        var envPath = Environment.GetEnvironmentVariable("NEO_PLUGIN_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var candidate = FindInDirectory(envPath);
            if (candidate != null) return candidate;
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var fromBase = FindInDirectory(baseDir);
        if (fromBase != null) return fromBase;

        foreach (var projectName in new[] { "Neo.PluginWindowAvalonia.MCP", "Neo.PluginWindowAvalonia" })
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                var devDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                    projectName, "bin", config, "net9.0"));
                var devCandidate = FindInDirectory(devDir, projectName);
                if (devCandidate != null) return devCandidate;
            }
        }

        return null;
    }

    private static (string exePath, bool useDotnet)? FindInDirectory(string dir, string? preferredName = null)
    {
        if (!Directory.Exists(dir)) return null;

        var names = preferredName != null
            ? new[] { preferredName, "Neo.PluginWindowAvalonia" }
            : new[] { "Neo.PluginWindowAvalonia.MCP", "Neo.PluginWindowAvalonia" };

        foreach (var name in names)
        {
            var nativeName = OperatingSystem.IsWindows() ? $"{name}.exe" : name;
            var nativePath = Path.Combine(dir, nativeName);
            if (File.Exists(nativePath)) return (nativePath, false);

            var dllPath = Path.Combine(dir, $"{name}.dll");
            if (File.Exists(dllPath)) return (dllPath, true);
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync();
    }
}
