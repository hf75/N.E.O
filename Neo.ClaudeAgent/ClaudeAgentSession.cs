using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Neo.ClaudeAgent;

/// <summary>
/// One Claude Code that keeps running: messages in, events out, one turn after the other. It speaks the protocol
/// the official Agent SDKs (TypeScript, Python) speak with Claude Code: stream-json on standard input and output,
/// and control requests for initialize, interrupt, permission prompts (can_use_tool) and the tools that run in
/// this process (mcp_message). There is no official SDK for .NET; this is the part N.E.O. needs.
/// </summary>
/// <remarks>Events are raised on the session's reading thread; a handler must return quickly.</remarks>
public sealed class ClaudeAgentSession : IAsyncDisposable
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private const int ErrorOutputKept = 8 * 1024;

    private readonly ClaudeAgentOptions _options;
    private readonly Process _process;
    private readonly SdkMcpServer? _tools;
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _requests = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _prompts = new();
    private readonly ConcurrentDictionary<string, (string Name, long Started)> _runningTools = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly StringBuilder _errorOutput = new();
    private readonly object _gate = new();
    private readonly Task _reading;
    private readonly Task _readingErrors;
    private Turn? _turn;
    private int _requestCount;
    private int _disposed;
    private double _sessionCost;
    private double _sessionApiMilliseconds;
    private volatile bool _exited;

    /// <summary>Everything the session reports. Raised on the reading thread.</summary>
    public event Action<ClaudeAgentEvent>? EventReceived;

    /// <summary>The Claude Code this session runs.</summary>
    public ClaudeCliCommand Cli { get; }

    /// <summary>What Claude Code said when the last turn began: model, tools, MCP servers.</summary>
    public ClaudeAgentReady? Ready { get; private set; }

    public string? SessionId { get; private set; }

    /// <summary>What Claude Code answered to initialize: its commands, models and account.</summary>
    public JsonObject? Initialization { get; private set; }

    /// <summary>What the session has cost so far.</summary>
    public double CostUsd => Volatile.Read(ref _sessionCost);

    public bool HasExited => _exited;

    public bool IsBusy
    {
        get { lock (_gate) return _turn != null; }
    }

    private ClaudeAgentSession(ClaudeAgentOptions options, ClaudeCliCommand cli, Dictionary<string, string> environment)
    {
        _options = options;
        Cli = cli;
        if (options.Tools.Count > 0)
            _tools = new SdkMcpServer(options.ToolServerName, options.Tools);

        var start = new ProcessStartInfo(cli.FileName)
        {
            WorkingDirectory = options.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = s_utf8,
            StandardOutputEncoding = s_utf8,
            StandardErrorEncoding = s_utf8,
        };
        foreach (var argument in cli.Arguments.Concat(ClaudeAgentArguments.For(options)))
            start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var (name, value) in environment)
            start.Environment[name] = value;

        try
        {
            _process = Process.Start(start) ?? throw new ClaudeAgentException($"{cli.FileName} did not start.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new ClaudeAgentException($"{cli.FileName} could not be started: {exception.Message}", exception);
        }
        _process.StandardInput.NewLine = "\n";
        _readingErrors = Task.Run(ReadErrorsAsync);
        _reading = Task.Run(ReadAsync);
    }

    /// <summary>
    /// Starts Claude Code and waits until it has answered the initialize request. Throws
    /// <see cref="ClaudeCliNotFoundException"/> when there is no Claude Code, <see cref="ClaudeAgentException"/> when it
    /// cannot run (no API key, it ended, it did not answer).
    /// </summary>
    public static async Task<ClaudeAgentSession> StartAsync(ClaudeAgentOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != options.Tools.Count)
            throw new ArgumentException("Two tools have the same name.", nameof(options));

        var cli = ClaudeCli.Find(options.CliPath);
        var environment = ClaudeCli.ChildEnvironment();
        if (options.ToolSearch is { } search)
            environment["ENABLE_TOOL_SEARCH"] = search ? "true" : "false";
        foreach (var (name, value) in options.Environment)
            environment[name] = value;
        if (options.RequireApiKey && (!environment.TryGetValue("ANTHROPIC_API_KEY", out var key) || string.IsNullOrWhiteSpace(key)))
        {
            throw new ClaudeAgentException(
                "Claude Code needs an Anthropic API key here: set ANTHROPIC_API_KEY. Other products may not use a claude.ai login.");
        }

        var session = new ClaudeAgentSession(options, cli, environment);
        try
        {
            var request = new JsonObject { ["subtype"] = "initialize", ["hooks"] = null };
            if (session._tools != null)
                request["sdkMcpServers"] = new JsonArray(JsonValue.Create(session._tools.Name));
            session.Initialization = await session.RequestAsync(request, options.StartTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return session;
    }

    /// <summary>
    /// Sends a message and waits for the end of the turn it starts. Cancelling interrupts the turn in Claude Code,
    /// waits until it has stopped, and then throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public Task<ClaudeAgentTurnResult> SendAsync(string text, IReadOnlyList<ClaudeAgentImage>? images = null, CancellationToken cancellationToken = default)
    {
        Turn turn;
        lock (_gate)
        {
            if (_exited)
                throw Ended();
            if (_turn != null)
                throw new InvalidOperationException("A turn is still running. Wait for it, or interrupt it first.");
            _turn = turn = new Turn();
        }

        var content = new JsonArray();
        foreach (var image in images ?? [])
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.MediaType, ["data"] = Convert.ToBase64String(image.Data) },
            });
        }
        content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        var message = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
            ["parent_tool_use_id"] = null,
        };
        return SendCoreAsync(turn, message, cancellationToken);
    }

    private async Task<ClaudeAgentTurnResult> SendCoreAsync(Turn turn, JsonObject message, CancellationToken cancellationToken)
    {
        try
        {
            await WriteAsync(message).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                if (_turn == turn)
                    _turn = null;
            }
            throw;
        }

        try
        {
            return await turn.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { await InterruptAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (ClaudeAgentException) { }
            try { await turn.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is TimeoutException or ClaudeAgentException) { }
            throw;
        }
    }

    /// <summary>Stops the running turn. The turn then ends with a result that did not succeed.</summary>
    public async Task InterruptAsync(CancellationToken cancellationToken = default)
        => await RequestAsync(new JsonObject { ["subtype"] = "interrupt" }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

    // ── Requests to Claude Code ──

    private async Task<JsonObject> RequestAsync(JsonObject request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = $"req_{Interlocked.Increment(ref _requestCount)}_{Guid.NewGuid():N}";
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[id] = answer;
        try
        {
            if (_exited)
                throw Ended();
            await WriteAsync(new JsonObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request }).ConfigureAwait(false);
            return await answer.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ClaudeAgentException($"Claude Code did not answer the {(string?)request["subtype"]} request within {timeout.TotalSeconds:0} s.");
        }
        finally
        {
            _requests.TryRemove(id, out _);
        }
    }

    private void Answered(JsonObject response)
    {
        if ((string?)response["request_id"] is not { } id || !_requests.TryGetValue(id, out var answer))
            return;
        if ((string?)response["subtype"] == "error")
            answer.TrySetException(new ClaudeAgentException("Claude Code refused the request: " + (string?)response["error"]));
        else
            answer.TrySetResult(response["response"] as JsonObject ?? new JsonObject());
    }

    private async Task WriteAsync(JsonObject message)
    {
        var line = message.ToJsonString();
        await _writing.WaitAsync().ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw Ended(exception);
        }
        finally
        {
            _writing.Release();
        }
    }

    // ── What Claude Code writes ──

    private async Task ReadAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (line.Length == 0 || line[0] != '{')
                    continue;
                JsonObject? message;
                try { message = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { continue; }
                if (message != null)
                    Handle(message);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        finally
        {
            await ExitedAsync().ConfigureAwait(false);
        }
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                lock (_errorOutput)
                {
                    _errorOutput.AppendLine(line);
                    if (_errorOutput.Length > ErrorOutputKept)
                        _errorOutput.Remove(0, _errorOutput.Length - ErrorOutputKept);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    private void Handle(JsonObject message)
    {
        switch ((string?)message["type"])
        {
            case "control_response":
                if (message["response"] is JsonObject response)
                    Answered(response);
                break;
            case "control_request":
                _ = AnswerAsync(message);
                break;
            case "control_cancel_request":
                if ((string?)message["request_id"] is { } cancelled && _prompts.TryRemove(cancelled, out var prompt))
                    prompt.Cancel();
                break;
            case "system" when (string?)message["subtype"] == "init":
                Started(message);
                break;
            case "stream_event":
                if (message["parent_tool_use_id"] is null && message["event"] is JsonObject streamEvent
                    && (string?)streamEvent["type"] == "content_block_delta"
                    && (string?)streamEvent["delta"]?["type"] == "text_delta"
                    && (string?)streamEvent["delta"]?["text"] is { Length: > 0 } delta)
                    Raise(new ClaudeAgentTextDelta(delta));
                break;
            case "assistant":
                Assistant(message);
                break;
            case "user":
                ToolResults(message);
                break;
            case "result":
                TurnEnded(message);
                break;
        }
    }

    private void Started(JsonObject message)
    {
        SessionId = (string?)message["session_id"] ?? SessionId;
        var tools = message["tools"] is JsonArray toolNames ? toolNames.Select(name => (string?)name ?? "").ToList() : [];
        var servers = message["mcp_servers"] is JsonArray serverList
            ? serverList.OfType<JsonObject>().Select(server => new ClaudeAgentMcpServerStatus((string?)server["name"] ?? "", (string?)server["status"] ?? "")).ToList()
            : [];
        Ready = new ClaudeAgentReady((string?)message["model"] ?? "", tools, servers, (string?)message["permissionMode"],
            (string?)message["claude_code_version"], SessionId);
        Raise(Ready);
    }

    private void Assistant(JsonObject message)
    {
        var parent = (string?)message["parent_tool_use_id"];
        if (message["message"]?["content"] is not JsonArray content)
            return;
        foreach (var block in content.OfType<JsonObject>())
        {
            switch ((string?)block["type"])
            {
                case "text" when parent == null && (string?)block["text"] is { } text:
                    lock (_gate)
                        _turn?.Text.Append(text);
                    Raise(new ClaudeAgentText(text));
                    break;
                case "tool_use" when (string?)block["id"] is { } id:
                    var name = (string?)block["name"] ?? "";
                    _runningTools[id] = (name, Stopwatch.GetTimestamp());
                    Raise(new ClaudeAgentToolStarted(id, name, Element(block["input"]), parent));
                    break;
            }
        }
    }

    private void ToolResults(JsonObject message)
    {
        if (message["message"]?["content"] is not JsonArray content)
            return;
        foreach (var block in content.OfType<JsonObject>())
        {
            if ((string?)block["type"] != "tool_result" || (string?)block["tool_use_id"] is not { } id || !_runningTools.TryRemove(id, out var tool))
                continue;
            Raise(new ClaudeAgentToolFinished(id, tool.Name, Flag(block["is_error"]), OutputText(block["content"]), Stopwatch.GetElapsedTime(tool.Started)));
        }
    }

    private void TurnEnded(JsonObject message)
    {
        // Claude Code counts cost and API time for the whole session; a turn gets the difference.
        var sessionCost = Number(message["total_cost_usd"]);
        var costBefore = Interlocked.Exchange(ref _sessionCost, sessionCost);
        var sessionApi = Number(message["duration_api_ms"]);
        var apiBefore = Interlocked.Exchange(ref _sessionApiMilliseconds, sessionApi);
        SessionId = (string?)message["session_id"] ?? SessionId;

        Turn? turn;
        lock (_gate)
        {
            turn = _turn;
            _turn = null;
        }

        var usage = message["usage"] as JsonObject;
        var subtype = (string?)message["subtype"] ?? "";
        var result = new ClaudeAgentTurnResult(
            Succeeded: subtype == "success" && !Flag(message["is_error"]),
            Subtype: subtype,
            Text: (string?)message["result"] is { Length: > 0 } text ? text : turn?.Text.ToString() ?? "",
            Duration: TimeSpan.FromMilliseconds(Number(message["duration_ms"])),
            ApiDuration: TimeSpan.FromMilliseconds(Math.Max(0, sessionApi - apiBefore)),
            Rounds: (int)Number(message["num_turns"]),
            CostUsd: Math.Max(0, sessionCost - costBefore),
            SessionCostUsd: sessionCost,
            Usage: new ClaudeAgentUsage(
                (long)Number(usage?["input_tokens"]),
                (long)Number(usage?["cache_creation_input_tokens"]),
                (long)Number(usage?["cache_read_input_tokens"]),
                (long)Number(usage?["output_tokens"])),
            SessionId: SessionId,
            PermissionDenials: message["permission_denials"] is JsonArray denials
                ? denials.OfType<JsonObject>().Select(denial => (string?)denial["tool_name"] ?? "").ToList()
                : []);
        Raise(new ClaudeAgentTurnEnded(result));
        turn?.Completion.TrySetResult(result);
    }

    // ── What Claude Code asks of this process ──

    private async Task AnswerAsync(JsonObject message)
    {
        if ((string?)message["request_id"] is not { } id)
            return;
        var request = message["request"] as JsonObject;
        JsonObject response;
        try
        {
            var answer = (string?)request?["subtype"] switch
            {
                "can_use_tool" => await PermissionAsync(id, request!).ConfigureAwait(false),
                "mcp_message" => await McpAsync(request!).ConfigureAwait(false),
                var other => throw new NotSupportedException($"This host does not answer {other} requests."),
            };
            response = new JsonObject { ["subtype"] = "success", ["request_id"] = id, ["response"] = answer };
        }
        catch (Exception exception)
        {
            response = new JsonObject { ["subtype"] = "error", ["request_id"] = id, ["error"] = exception.Message };
        }

        try { await WriteAsync(new JsonObject { ["type"] = "control_response", ["response"] = response }).ConfigureAwait(false); }
        catch (ClaudeAgentException) { } // Claude Code has ended meanwhile
    }

    private async Task<JsonObject> PermissionAsync(string requestId, JsonObject request)
    {
        var input = request["input"];
        var asked = new ClaudeAgentPermissionRequest(
            (string?)request["tool_name"] ?? "",
            Element(input),
            (string?)request["tool_use_id"],
            (string?)request["decision_reason"],
            (string?)request["blocked_path"],
            (string?)request["title"],
            (string?)request["description"]);

        ClaudeAgentPermission decision;
        if (_options.PermissionHandler is { } handler)
        {
            using var prompt = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            _prompts[requestId] = prompt;
            try { decision = await handler(asked, prompt.Token).ConfigureAwait(false); }
            finally { _prompts.TryRemove(requestId, out _); }
        }
        else
        {
            decision = ClaudeAgentPermission.Deny("Nobody here answers permission prompts.");
        }
        Raise(new ClaudeAgentPermissionAsked(asked, decision));

        var answer = new JsonObject();
        if (decision.Allowed)
        {
            answer["behavior"] = "allow";
            answer["updatedInput"] = decision.UpdatedInput is { } updated ? JsonNode.Parse(updated.GetRawText()) : input?.DeepClone() ?? new JsonObject();
        }
        else
        {
            answer["behavior"] = "deny";
            answer["message"] = decision.Message ?? "Not allowed.";
        }
        if (asked.ToolUseId != null)
            answer["toolUseID"] = asked.ToolUseId;
        return answer;
    }

    private async Task<JsonObject> McpAsync(JsonObject request)
    {
        var server = (string?)request["server_name"];
        if (_tools == null || server != _tools.Name)
            throw new InvalidOperationException($"There is no MCP server {server} in this host.");
        var reply = request["message"] is JsonObject message ? await _tools.HandleAsync(message, _stopping.Token).ConfigureAwait(false) : null;
        // A notification gets an empty answer, as the official SDKs give it.
        return new JsonObject { ["mcp_response"] = reply ?? new JsonObject { ["jsonrpc"] = "2.0", ["result"] = new JsonObject(), ["id"] = 0 } };
    }

    // ── The end ──

    private async Task ExitedAsync()
    {
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException) { }
        try { await _readingErrors.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }

        _exited = true;
        var failure = Ended();
        foreach (var pending in _requests.Values)
            pending.TrySetException(failure);
        Turn? turn;
        lock (_gate)
        {
            turn = _turn;
            _turn = null;
        }
        turn?.Completion.TrySetException(failure);

        int? exitCode = null;
        try
        {
            if (_process.HasExited)
                exitCode = _process.ExitCode;
        }
        catch (InvalidOperationException) { }
        Raise(new ClaudeAgentExited(exitCode, ErrorOutput()));
    }

    private ClaudeAgentException Ended(Exception? inner = null)
    {
        var detail = ErrorOutput().Trim();
        return new ClaudeAgentException("Claude Code has ended" + (detail.Length > 0 ? ": " + detail : "."), inner);
    }

    private string ErrorOutput()
    {
        lock (_errorOutput)
            return _errorOutput.ToString();
    }

    /// <summary>Ends Claude Code: standard input closes, and what has not ended after five seconds is stopped with its child processes.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _stopping.Cancel();
        try { _process.StandardInput.Close(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException) { }

        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch (Exception killing) when (killing is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        try { await _reading.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        _process.Dispose();
        _stopping.Dispose();
    }

    private void Raise(ClaudeAgentEvent agentEvent)
    {
        try { EventReceived?.Invoke(agentEvent); }
        catch (Exception exception)
        {
            // A handler's mistake must not end the session.
            Debug.WriteLine($"[ClaudeAgentSession] event handler threw: {exception}");
        }
    }

    private static JsonElement Element(JsonNode? node)
    {
        using var document = JsonDocument.Parse(node?.ToJsonString() ?? "{}");
        return document.RootElement.Clone();
    }

    private static string OutputText(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray blocks => string.Join("\n", blocks.OfType<JsonObject>().Select(block => (string?)block["type"] switch
        {
            "text" => (string?)block["text"] ?? "",
            "image" => "[image]",
            var other => $"[{other}]",
        })),
        _ => "",
    };

    private static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static double Number(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;

    private sealed class Turn
    {
        public readonly TaskCompletionSource<ClaudeAgentTurnResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly StringBuilder Text = new();
    }
}
