using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using Neo.IPC;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// Plays the host side of the pipe protocol against one real plugin window process —
/// the same sequence ChildProcessService (desktop hosts) and PreviewSessionManager
/// (MCP server) run: pipe server, start child, Hello/Ack, blob stream, LoadControl.
/// </summary>
internal sealed class ChildSession : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly FramedPipeMessenger _messenger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromMinutes(2));
    private readonly TaskCompletionSource<bool> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly ConcurrentQueue<string> _errors = new();
    private readonly ConcurrentDictionary<string, int> _frameCounts = new(StringComparer.Ordinal);
    private Process? _child;
    private Task? _receiveLoop;
    private int _loadExecuted;

    private ChildSession(NamedPipeServerStream pipe)
    {
        _pipe = pipe;
        _messenger = new FramedPipeMessenger(pipe);
    }

    public int LoadExecutedCount => Volatile.Read(ref _loadExecuted);
    public IReadOnlyCollection<string> Errors => _errors.ToArray();
    public IReadOnlyCollection<string> Logs => _logs.ToArray();
    public bool ChildIsAlive => _child is { HasExited: false };

    /// <summary>How many control frames of the given <see cref="IpcTypes"/> type the child sent.</summary>
    public int FrameCount(string ipcType) => _frameCounts.TryGetValue(ipcType, out var n) ? n : 0;

    public static async Task<ChildSession> StartAsync(string exePath, string extraArgs)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"Plugin window executable not built: {exePath}");

        var pipeName = $"neo_smoke_{Environment.ProcessId}_{Guid.NewGuid():N}"[..40];
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var session = new ChildSession(pipe);

        session._child = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--pipe {pipeName} --parentPid {Environment.ProcessId} {extraArgs}".Trim(),
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Child process did not start.");

        var connect = pipe.WaitForConnectionAsync(session._cts.Token);
        if (await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(30))) != connect)
        {
            var alive = session.ChildIsAlive;
            await session.DisposeAsync();
            throw new TimeoutException(
                $"Child never connected to the pipe (process alive: {alive}). A child that stays alive " +
                "without connecting usually threw inside its window-opened handler — e.g. a type whose " +
                "assembly is missing next to the exe.");
        }
        await connect;

        session._receiveLoop = Task.Run(session.ReceiveAsync);

        // Protocol precondition, same as in the real hosts: nothing is sent before the child's Hello.
        if (await Task.WhenAny(session._hello.Task, Task.Delay(TimeSpan.FromSeconds(20))) != session._hello.Task)
        {
            await session.DisposeAsync();
            throw new TimeoutException("Child connected to the pipe but never said Hello.");
        }
        return session;
    }

    /// <summary>Streams the plugin DLL as a blob and asks the child to load it.</summary>
    public async Task LoadAsync(string pluginDllPath)
    {
        var bytes = await File.ReadAllBytesAsync(pluginDllPath);
        var name = Path.GetFileName(pluginDllPath);

        using var ms = new MemoryStream(bytes, writable: false);
        await _sendLock.WaitAsync();
        try
        {
            await _messenger.SendBlobAsync(Guid.NewGuid(), new BlobStartMeta(Name: name, Length: bytes.Length),
                read: async (buffer, token) => await ms.ReadAsync(buffer, token), ct: _cts.Token);
        }
        finally { _sendLock.Release(); }

        await SendAsync(new IpcEnvelope(IpcTypes.LoadControl, Guid.NewGuid().ToString("N"),
            Json.ToJson(new LoadControlRequest(name, "DynamicUserControl", new List<string>()))));
    }

    public Task UnloadAsync()
        => SendAsync(new IpcEnvelope(IpcTypes.UnloadControl, Guid.NewGuid().ToString("N"), "{}"));

    /// <summary>Waits until the child reported <paramref name="count"/> executed loads, or an error arrived.</summary>
    public async Task<bool> WaitForLoadsAsync(int count)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (LoadExecutedCount >= count) return true;
            if (!_errors.IsEmpty || !ChildIsAlive) return false;
            await Task.Delay(100);
        }
        return LoadExecutedCount >= count;
    }

    private async Task SendAsync(IpcEnvelope env)
    {
        await _sendLock.WaitAsync();
        try { await _messenger.SendControlAsync(env, _cts.Token); }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            await _messenger.ReceiveLoopAsync(
                onControl: async env =>
                {
                    _frameCounts.AddOrUpdate(env.Type, 1, (_, n) => n + 1);
                    switch (env.Type)
                    {
                        case IpcTypes.Hello:
                            await SendAsync(new IpcEnvelope(IpcTypes.Ack, env.CorrelationId,
                                Json.ToJson(new AckMessage("Hello acknowledged"))));
                            _hello.TrySetResult(true);
                            break;

                        case IpcTypes.Log:
                            var log = Json.FromJson<LogMessage>(env.PayloadJson);
                            if (log == null) break;
                            _logs.Enqueue($"[{log.Level}] {log.Message}");
                            if (log.Message.Contains("LoadControl executed", StringComparison.Ordinal))
                                Interlocked.Increment(ref _loadExecuted);
                            break;

                        case IpcTypes.Error:
                            var err = Json.FromJson<ErrorMessage>(env.PayloadJson);
                            _errors.Enqueue($"{err?.ExceptionType}: {err?.Message}");
                            break;
                    }
                },
                onBlobStart: (_, _) => Task.CompletedTask,
                onBlobChunk: (_, _) => Task.CompletedTask,
                onBlobEnd: _ => Task.CompletedTask,
                ct: _cts.Token);
        }
        catch (Exception) when (_cts.IsCancellationRequested || !ChildIsAlive)
        {
            // Pipe torn down on purpose.
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_child is { HasExited: false }) _child.Kill(entireProcessTree: true); } catch { }
        try { _cts.Cancel(); } catch { }
        if (_receiveLoop != null)
        {
            try { await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        try { _pipe.Dispose(); } catch { }
        try { _child?.WaitForExit(5000); } catch { }
        _child?.Dispose();
        _cts.Dispose();
    }
}
