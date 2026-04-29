using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using Neo.IPC;
using McpServerInstance = ModelContextProtocol.Server.McpServer;

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

    // ── Live-MCP loop protection (Phase 1) ──
    private readonly LoopProtection _loopProtection;

    // ── Live-MCP dynamic tool registry (Phase 2) ──
    private readonly LiveMcpToolRegistry _liveMcpToolRegistry;

    // ── Live-MCP resource registry for watch_observable (Phase 2B) ──
    private readonly LiveMcpResourceRegistry _liveMcpResourceRegistry;

    public PreviewSessionManager(
        LoopProtection loopProtection,
        LiveMcpToolRegistry liveMcpToolRegistry,
        LiveMcpResourceRegistry liveMcpResourceRegistry)
    {
        _loopProtection = loopProtection;
        _liveMcpToolRegistry = liveMcpToolRegistry;
        _liveMcpResourceRegistry = liveMcpResourceRegistry;
    }

    // ── Channel support: push events to Claude Code ──
    private McpServerInstance? _channelServer;

    /// <summary>Wire up the MCP server instance for pushing channel notifications.</summary>
    public void SetChannelServer(McpServerInstance server) => _channelServer = server;

    /// <summary>Push an app event to Claude via MCP channel notification.</summary>
    private async Task PushChannelEventAsync(string windowId, AppEventMessage evt)
    {
        if (_channelServer == null) return;
        try
        {
            // Build meta object: each key becomes an attribute on the <channel> tag
            var meta = new JsonObject
            {
                ["event_type"] = evt.EventType,
                ["target"] = evt.Target,
                ["window_id"] = windowId
            };
            if (!string.IsNullOrEmpty(evt.Value))
                meta["value"] = evt.Value;

            // Channel notification params per MCP channel spec
            var parameters = new JsonObject
            {
                ["content"] = FormatChannelContent(evt),
                ["meta"] = meta
            };

            await _channelServer.SendNotificationAsync(
                "notifications/claude/channel",
                parameters,
                serializerOptions: null,
                cancellationToken: default);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Channel] Failed to push event: {ex.Message}");
        }
    }

    private static string FormatChannelContent(AppEventMessage evt)
    {
        return evt.EventType switch
        {
            // User-defined trigger from generated code — prompt is passed through verbatim
            "user_trigger" => evt.Value ?? "(empty trigger)",

            // System events — always active
            "runtime_error" => $"Runtime error in {evt.Target}: {evt.Value}" +
                               (evt.Details != null ? $"\n{evt.Details}" : ""),

            // Fallback (rarely used now that auto-hooks are gone)
            _ => $"{evt.EventType} on '{evt.Target}'" +
                 (evt.Value != null ? $": {evt.Value}" : "")
        };
    }

    // ========================================
    // WindowSession — per-window state
    // ========================================
    private sealed class WindowSession : IAsyncDisposable
    {
        public string WindowId { get; }
        public string Framework { get; }
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

        // Live-MCP (Phase 1): the most recent manifest the app emitted after LoadControl.
        // Cleared on UnloadControl / process exit.
        public AppManifestMessage? LiveMcpManifest { get; set; }

        public bool IsRunning => ChildProcess is { HasExited: false } && PipeStream is { IsConnected: true };

        public WindowSession(string windowId, string framework = "avalonia")
        {
            WindowId = windowId;
            Framework = framework;
        }

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

    /// <summary>Returns the UI framework for the given window session.</summary>
    public string? GetFramework(string? windowId = null) => GetSession(windowId)?.Framework;

    public async Task<bool> StartAsync(string? windowId = null, string framework = "avalonia", CancellationToken ct = default)
    {
        windowId ??= DefaultWindowId;
        framework = (framework ?? "avalonia").ToLowerInvariant();

        // Validate: WPF only on Windows
        if (framework == "wpf" && !OperatingSystem.IsWindows())
        {
            var errSession = new WindowSession(windowId, framework);
            errSession.AddLog("ERROR: WPF framework is only available on Windows.");
            _sessions[windowId] = errSession;
            return false;
        }

        // If already running, return true
        if (GetSession(windowId)?.IsRunning == true) return true;

        // Dispose old session if exists
        if (_sessions.TryRemove(windowId, out var oldSession))
            await oldSession.DisposeAsync();

        var session = new WindowSession(windowId, framework);
        _sessions[windowId] = session;

        session.PipeCts = new CancellationTokenSource();
        var counter = Interlocked.Increment(ref _sessionCounter);

        var pipeName = $"neo_mcp_{Environment.ProcessId}_{counter}";

        session.PipeStream = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        session.Messenger = new FramedPipeMessenger(session.PipeStream);

        var childPath = FindChildExecutable(framework);
        if (childPath == null)
        {
            session.AddLog($"ERROR: Neo.PluginWindow executable not found for framework '{framework}'.");
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
            _loopProtection.ResetApp(windowId);
            _liveMcpToolRegistry.UnregisterApp(windowId);
            _liveMcpResourceRegistry.UnregisterApp(windowId);
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
    // Live-MCP (Phase 1) — public API for the MCP tool layer
    // ========================================

    /// <summary>
    /// Resolved app id for the given (optional) windowId. Falls back to the default.
    /// Used as the keying scheme for LoopProtection and tool naming.
    /// </summary>
    public string ResolveAppId(string? windowId) => windowId ?? DefaultWindowId;

    /// <summary>List the appIds of all currently-running sessions.</summary>
    public IReadOnlyList<string> GetRunningAppIds()
        => _sessions.Where(kv => kv.Value.IsRunning).Select(kv => kv.Key).ToList();

    /// <summary>Returns the cached Live-MCP manifest for an app, or null if no app is loaded.</summary>
    public AppManifestMessage? GetManifest(string? windowId)
    {
        var s = GetRunningSession(windowId);
        return s?.LiveMcpManifest;
    }

    /// <summary>
    /// Invoke an [McpCallable] method on the running app. Loop-protection is enforced
    /// here — throws <see cref="LoopLimitExceededException"/> if the call would exceed
    /// the configured depth.
    /// </summary>
    public async Task<MethodResultMessage> InvokeAppMethodAsync(
        string? windowId, string method, string argsJson, int? overrideTimeoutSeconds = null,
        CancellationToken ct = default)
    {
        var appId = ResolveAppId(windowId);
        int hops;
        try { hops = _loopProtection.OnInvokeMethod(appId); }
        catch (LoopLimitExceededException ex)
        {
            Console.Error.WriteLine($"[live-mcp] loop_limit_exceeded app={ex.AppId} hops={ex.Hops} max={ex.MaxDepth} method={method}");
            return new MethodResultMessage(false, null, ex.Message, "loop_limit_exceeded");
        }

        // Pick timeout: prefer explicit override, else manifest entry, else default.
        var manifest = GetManifest(windowId);
        var entry = manifest?.Callables.FirstOrDefault(c => c.Name == method);
        var timeoutSeconds = overrideTimeoutSeconds
            ?? entry?.TimeoutSeconds
            ?? 30;

        var frame = new InvokeMethodMessage(method, argsJson ?? "[]", hops);
        var env = await SendRequestAsync(windowId, IpcTypes.InvokeMethod, Json.ToJson(frame), timeoutSeconds + 2, ct);
        if (env == null)
            return new MethodResultMessage(false, null, "No response (timeout or app disconnected).", "timeout");

        if (env.Type == IpcTypes.Error)
        {
            var errMsg = Json.FromJson<ErrorMessage>(env.PayloadJson);
            return new MethodResultMessage(false, null, errMsg?.Message ?? "App returned error.", "invocation_failed");
        }

        var result = Json.FromJson<MethodResultMessage>(env.PayloadJson);
        return result ?? new MethodResultMessage(false, null, "Malformed MethodResult payload.", "invocation_failed");
    }

    /// <summary>
    /// Phase 2B: tell the app to start emitting <see cref="ObservableValueMessage"/> frames
    /// for the named property. Idempotent app-side. Called from the resources/subscribe handler
    /// in Program.cs and from the manifest re-registration path on hot-reload.
    /// </summary>
    public async Task SendSubscribeObservableAsync(string windowId, string observableName, CancellationToken ct)
    {
        var s = GetRunningSession(windowId);
        if (s == null) return;
        var frame = new SubscribeObservableMessage(observableName);
        await SafeSendControlAsync(s, new IpcEnvelope(
            IpcTypes.SubscribeObservable, Guid.NewGuid().ToString("N"), Json.ToJson(frame)));
    }

    /// <summary>Phase 2B: counterpart to <see cref="SendSubscribeObservableAsync"/>.</summary>
    public async Task SendUnsubscribeObservableAsync(string windowId, string observableName, CancellationToken ct)
    {
        var s = GetRunningSession(windowId);
        if (s == null) return;
        var frame = new UnsubscribeObservableMessage(observableName);
        await SafeSendControlAsync(s, new IpcEnvelope(
            IpcTypes.UnsubscribeObservable, Guid.NewGuid().ToString("N"), Json.ToJson(frame)));
    }

    /// <summary>
    /// Read the current value of an [McpObservable] property on the running app.
    /// </summary>
    public async Task<ReadObservableResultMessage> ReadAppObservableAsync(
        string? windowId, string observableName, CancellationToken ct = default)
    {
        var frame = new ReadObservableMessage(observableName);
        var env = await SendRequestAsync(windowId, IpcTypes.ReadObservable, Json.ToJson(frame), 15, ct);
        if (env == null)
            return new ReadObservableResultMessage(false, null, "No response (timeout or app disconnected).");

        if (env.Type == IpcTypes.Error)
        {
            var errMsg = Json.FromJson<ErrorMessage>(env.PayloadJson);
            return new ReadObservableResultMessage(false, null, errMsg?.Message ?? "App returned error.");
        }

        var result = Json.FromJson<ReadObservableResultMessage>(env.PayloadJson);
        return result ?? new ReadObservableResultMessage(false, null, "Malformed ReadObservableResult payload.");
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

    private async Task ListenLoopAsync(WindowSession s, CancellationToken ct)
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

                                // Also push runtime errors to Claude via channel
                                _ = PushChannelEventAsync(s.WindowId, new AppEventMessage(
                                    EventType: "runtime_error",
                                    Target: errMsg.Context ?? "app",
                                    Value: errMsg.Message,
                                    Details: errMsg.StackTrace));
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

                        case IpcTypes.AppEvent:
                            var appEvt = Json.FromJson<AppEventMessage>(env.PayloadJson);
                            if (appEvt != null)
                            {
                                s.AddLog($"[APP EVENT] {appEvt.EventType}: {appEvt.Target}" +
                                         (appEvt.Value != null ? $" = {appEvt.Value}" : "") +
                                         (appEvt.Hops > 0 ? $" hops={appEvt.Hops}" : ""));
                                // Seed loop-protection: if the app's call context carried a hop count
                                // (Ai.Trigger fired from inside an InvokeMethod), Math.Max into the
                                // chain so Claude's next invoke_method increments from that floor.
                                _loopProtection.OnAppEvent(s.WindowId, appEvt.Hops);
                                // Push to Claude via channel notification
                                _ = PushChannelEventAsync(s.WindowId, appEvt);
                            }
                            break;

                        // ── Live-MCP (Phase 1+2) ───────────────────────────────
                        case IpcTypes.AppManifest:
                            {
                                var manifest = Json.FromJson<AppManifestMessage>(env.PayloadJson);
                                if (manifest != null)
                                {
                                    // The app emits AppId="" — server stamps its windowId here.
                                    var stamped = manifest with { AppId = s.WindowId };
                                    s.LiveMcpManifest = stamped;
                                    s.AddLog($"[LIVE-MCP] manifest stored for app '{s.WindowId}': " +
                                             $"{manifest.Callables.Count} callables, " +
                                             $"{manifest.Observables.Count} observables, " +
                                             $"{manifest.Triggerables.Count} triggerables.");

                                    // Phase 2: register one MCP tool per [McpCallable] method.
                                    // Hot-reload semantics live in the registry (signature-hash dedup),
                                    // so this is safe to call on every AppManifest frame including
                                    // updates from update_preview / patch_preview.
                                    try
                                    {
                                        _liveMcpToolRegistry.RegisterApp(s.WindowId, stamped, this);
                                        var registered = _liveMcpToolRegistry.GetRegisteredToolNamesForApp(s.WindowId);
                                        s.AddLog($"[LIVE-MCP] dynamic tools registered for app '{s.WindowId}' " +
                                                 $"({registered.Count}): {string.Join(", ", registered)}");
                                    }
                                    catch (Exception ex)
                                    {
                                        s.AddLog($"[LIVE-MCP] tool registration failed for app '{s.WindowId}': {ex.Message}");
                                    }

                                    // Phase 2B: register one MCP resource per Watchable observable.
                                    // Returns names that need a re-issued Subscribe IPC because Claude
                                    // had subscribed before the hot-reload and the new control instance
                                    // has fresh INPC state.
                                    try
                                    {
                                        var resub = _liveMcpResourceRegistry.RegisterApp(s.WindowId, stamped);
                                        var uris = _liveMcpResourceRegistry.GetRegisteredUrisForApp(s.WindowId);
                                        s.AddLog($"[LIVE-MCP] watchable resources registered for app '{s.WindowId}' " +
                                                 $"({uris.Count}): {string.Join(", ", uris)}");
                                        foreach (var name in resub)
                                        {
                                            await SendSubscribeObservableAsync(s.WindowId, name, default);
                                            s.AddLog($"[LIVE-MCP] re-subscribed observable '{name}' after hot-reload.");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        s.AddLog($"[LIVE-MCP] resource registration failed for app '{s.WindowId}': {ex.Message}");
                                    }
                                }
                                break;
                            }

                        case IpcTypes.ObservableValue:
                            {
                                var msg = Json.FromJson<ObservableValueMessage>(env.PayloadJson);
                                if (msg != null)
                                {
                                    _liveMcpResourceRegistry.OnObservableValue(s.WindowId, msg.Name, msg.ValueJson);
                                    // Hops on observable frames are telemetry only — observables don't
                                    // drive Claude turns, so they do NOT seed loop protection.
                                }
                                break;
                            }

                        case IpcTypes.MethodResult:
                        case IpcTypes.ReadObservableResult:
                            if (!string.IsNullOrEmpty(env.CorrelationId) &&
                                s.PendingRequests.TryRemove(env.CorrelationId, out var liveMcpTcs))
                                liveMcpTcs.TrySetResult(env);
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

    private static (string exePath, bool useDotnet)? FindChildExecutable(string framework = "avalonia")
    {
        // Determine env var and project names based on framework
        var envVar = framework == "wpf" ? "NEO_PLUGIN_PATH_WPF" : "NEO_PLUGIN_PATH";
        var projectNames = framework == "wpf"
            ? new[] { "Neo.PluginWindowWPF.MCP", "Neo.PluginWindowWPF" }
            : new[] { "Neo.PluginWindowAvalonia.MCP", "Neo.PluginWindowAvalonia" };
        // WPF uses net9.0-windows TFM
        var tfm = framework == "wpf" ? "net9.0-windows" : "net9.0";

        var envPath = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var candidate = FindInDirectory(envPath, null, projectNames);
            if (candidate != null) return candidate;
        }

        // Also check the generic NEO_PLUGIN_PATH for WPF (in case both are deployed together)
        if (framework == "wpf")
        {
            var genericPath = Environment.GetEnvironmentVariable("NEO_PLUGIN_PATH");
            if (!string.IsNullOrWhiteSpace(genericPath))
            {
                var candidate = FindInDirectory(genericPath, null, projectNames);
                if (candidate != null) return candidate;
            }
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var fromBase = FindInDirectory(baseDir, null, projectNames);
        if (fromBase != null) return fromBase;

        foreach (var projectName in projectNames)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                var devDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                    projectName, "bin", config, tfm));
                var devCandidate = FindInDirectory(devDir, projectName, projectNames);
                if (devCandidate != null) return devCandidate;
            }
        }

        return null;
    }

    private static (string exePath, bool useDotnet)? FindInDirectory(string dir, string? preferredName, string[]? namesList = null)
    {
        if (!Directory.Exists(dir)) return null;

        var names = preferredName != null
            ? new[] { preferredName }.Concat(namesList ?? Array.Empty<string>()).Distinct().ToArray()
            : namesList ?? new[] { "Neo.PluginWindowAvalonia.MCP", "Neo.PluginWindowAvalonia" };

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
