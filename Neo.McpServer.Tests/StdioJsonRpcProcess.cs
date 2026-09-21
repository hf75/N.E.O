using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Neo.McpServer.Tests;

/// <summary>
/// A child process that speaks newline-delimited JSON-RPC over stdio — which is what an MCP
/// client sees of Neo.McpServer and of an app exported in Frozen Mode.
/// </summary>
internal sealed class StdioJsonRpcProcess : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();
    private int _nextId;

    private StdioJsonRpcProcess(Process process)
    {
        _process = process;
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_stderr) _stderr.AppendLine(e.Data); };
        _process.BeginErrorReadLine();
    }

    public static StdioJsonRpcProcess Start(string fileName, string arguments)
        => new(Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        }) ?? throw new InvalidOperationException($"Could not start {fileName}."));

    public string StandardError { get { lock (_stderr) return _stderr.ToString(); } }

    /// <summary>The MCP handshake: <c>initialize</c>, then the <c>initialized</c> notification.</summary>
    public async Task<JsonElement> InitializeAsync(string clientName)
    {
        var result = await RequestAsync("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = clientName, version = "1.0" },
        });
        await NotifyAsync("notifications/initialized");
        return result;
    }

    public async Task<IReadOnlyList<string>> ListToolNamesAsync()
        => (await RequestAsync("tools/list")).GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();

    /// <summary>Calls a tool and returns the text of its first content block.</summary>
    public async Task<string> CallToolAsync(string name, object arguments, TimeSpan? timeout = null)
    {
        var result = await RequestAsync("tools/call", new { name, arguments }, timeout);
        return result.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }

    /// <summary>Sends a request and returns its <c>result</c>; a JSON-RPC error fails the test.</summary>
    public async Task<JsonElement> RequestAsync(string method, object? @params = null, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        object request = @params == null
            ? new { jsonrpc = "2.0", id, method }
            : new { jsonrpc = "2.0", id, method, @params };
        await WriteAsync(request);

        var response = await ReadResponseAsync(id, timeout ?? TimeSpan.FromSeconds(45));
        if (response.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"{method} failed: {error}. Process stderr:\n{StandardError}");
        return response.GetProperty("result");
    }

    public Task NotifyAsync(string method) => WriteAsync(new { jsonrpc = "2.0", method });

    private async Task WriteAsync(object message)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await _process.StandardInput.FlushAsync();
    }

    /// <summary>Reads messages until the response with the given id arrives (notifications are skipped).</summary>
    private async Task<JsonElement> ReadResponseAsync(int id, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            string? line;
            try { line = await _process.StandardOutput.ReadLineAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"No JSON-RPC response with id {id}. Process stderr:\n{StandardError}");
            }

            if (line == null)
                throw new InvalidOperationException($"Process closed stdout before answering id {id}. Process stderr:\n{StandardError}");
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("id", out var idProp) &&
                idProp.ValueKind == JsonValueKind.Number && idProp.GetInt32() == id)
                return doc.RootElement.Clone();
        }
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        try { _process.WaitForExit(5000); } catch { }
        _process.Dispose();
    }
}
