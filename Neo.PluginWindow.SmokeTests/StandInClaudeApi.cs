using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// Answers on localhost the way the Anthropic API does, so a test of Smart Edit costs nothing and sees
/// what the window sent. The process under test finds it through NEO_ANTHROPIC_BASE_URL.
/// </summary>
internal sealed class StandInClaudeApi : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<string> _answers = new();
    private readonly ConcurrentQueue<string> _prompts = new();

    public StandInClaudeApi()
    {
        BaseUrl = $"http://localhost:{FreePort()}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = ServeAsync();
    }

    public string BaseUrl { get; }

    /// <summary>The user messages received so far: what the window asked Claude, with the code it sent.</summary>
    public IReadOnlyList<string> Prompts => _prompts.ToArray();

    /// <summary>The next answer: replace the app with this code.</summary>
    public void AnswerWithCode(string code)
        => _answers.Enqueue(JsonSerializer.Serialize(new { Code = code, Explanation = "From the stand-in." }));

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) when (!_listener.IsListening) { return; }

            await AnswerAsync(context);
        }
    }

    private async Task AnswerAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            if (context.Request.HttpMethod == "GET")
            {
                // GET /v1/models/{id}
                await WriteAsync(response, "application/json", """{"capabilities":{"structured_outputs":{"supported":true}},"allowed_fallback_models":[]}""");
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var request = JsonNode.Parse(await reader.ReadToEndAsync())!;
            _prompts.Enqueue(request["messages"]!.AsArray().Last()!["content"]!.GetValue<string>());

            if (!_answers.TryDequeue(out var answer))
            {
                response.StatusCode = 500;
                await WriteAsync(response, "application/json", """{"type":"error","error":{"type":"test","message":"The stand-in has no answer prepared."}}""");
                return;
            }

            await WriteAsync(response, "text/event-stream", StreamOf(answer));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[StandInClaudeApi] " + ex.Message);
            try { response.Abort(); } catch { }
        }
    }

    // A whole answer in the pieces the API streams it in.
    private static string StreamOf(string text)
    {
        var events = new object[]
        {
            new { type = "message_start" },
            new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } },
            new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } },
            new { type = "content_block_stop", index = 0 },
            new { type = "message_delta", delta = new { stop_reason = "end_turn" } },
            new { type = "message_stop" },
        };
        return string.Concat(events.Select(e => "data: " + JsonSerializer.Serialize(e) + "\n\n"));
    }

    private static async Task WriteAsync(HttpListenerResponse response, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
