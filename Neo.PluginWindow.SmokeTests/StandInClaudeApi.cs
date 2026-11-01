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
    private readonly ConcurrentQueue<(string Query, string Title, string Url, string Answer, bool Cited)> _searchedAnswers = new();
    private readonly ConcurrentQueue<string> _prompts = new();
    private readonly ConcurrentQueue<string> _requests = new();

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

    /// <summary>The whole requests received so far, as JSON.</summary>
    public IReadOnlyList<string> Requests => _requests.ToArray();

    /// <summary>The next answer: replace the app with this code, which needs these packages.</summary>
    public void AnswerWithCode(string code, params string[] nuGetPackages)
        => _answers.Enqueue(JsonSerializer.Serialize(new { Code = code, NuGetPackages = nuGetPackages, Explanation = "From the stand-in." }));

    /// <summary>The next answer: a text. An app that asks gets this; so does a schema, then it is its JSON.</summary>
    public void AnswerWithText(string text) => _answers.Enqueue(text);

    /// <summary>The next answer comes after a web search, which found this page and cites it.</summary>
    public void AnswerAfterSearching(string query, string pageTitle, string pageUrl, string answer, bool cited = true)
        => _searchedAnswers.Enqueue((query, pageTitle, pageUrl, answer, cited));

    /// <summary>The next answer after a web search: the code of a tool, written after looking this up.</summary>
    public void AnswerWithCodeAfterSearching(string query, string pageTitle, string pageUrl, string code, params string[] nuGetPackages)
        => AnswerAfterSearching(query, pageTitle, pageUrl,
            JsonSerializer.Serialize(new { Code = code, NuGetPackages = nuGetPackages, Explanation = "From the stand-in, after a search." }), cited: false);

    /// <summary>Requests with the search tool are refused the way the API does for an organization that switched it off.</summary>
    public bool SearchSwitchedOff { get; set; }

    /// <summary>The models say they cannot search.</summary>
    public bool ModelsCannotSearch { get; set; }

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
                await WriteAsync(response, "application/json", """{"capabilities":{"structured_outputs":{"supported":true},"server_tools":{"web_search":{"supported":CAN_SEARCH}},"effort":{"supported":true,"low":{"supported":true},"medium":{"supported":true},"high":{"supported":true}}},"allowed_fallback_models":[]}""".Replace("CAN_SEARCH", ModelsCannotSearch ? "false" : "true"));
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            _requests.Enqueue(body);
            var request = JsonNode.Parse(body)!;
            _prompts.Enqueue(request["messages"]!.AsArray().Last()!["content"]!.GetValue<string>());

            var mayAskForSearch = request["tools"] != null;
            if (mayAskForSearch && SearchSwitchedOff)
            {
                response.StatusCode = 400;
                await WriteAsync(response, "application/json", """{"type":"error","error":{"type":"invalid_request_error","message":"web search is not enabled for this organization"}}""");
                return;
            }

            if (mayAskForSearch && _searchedAnswers.TryDequeue(out var searched))
            {
                await WriteAsync(response, "text/event-stream", StreamOfSearch(searched.Query, searched.Title, searched.Url, searched.Answer, searched.Cited));
                return;
            }

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

    // Field for field what the API sent in a real search on 2026-11-01.
    private static string StreamOfSearch(string query, string title, string url, string answer, bool cited)
    {
        const string id = "srvtoolu_standin";
        var events = new List<object>
        {
            new { type = "message_start" },
            new { type = "content_block_start", index = 0, content_block = new { type = "server_tool_use", id, name = "web_search", input = new { } } },
            new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = JsonSerializer.Serialize(new { query }) } },
            new { type = "content_block_stop", index = 0 },
            new
            {
                type = "content_block_start",
                index = 1,
                content_block = new
                {
                    type = "web_search_tool_result",
                    tool_use_id = id,
                    content = new[] { new { type = "web_search_result", title, url, encrypted_content = "stand-in", page_age = "today" } },
                },
            },
            new { type = "content_block_stop", index = 1 },
        };

        if (cited)
        {
            events.Add(new { type = "content_block_start", index = 2, content_block = new { citations = Array.Empty<object>(), type = "text", text = "" } });
            events.Add(new { type = "content_block_delta", index = 2, delta = new { type = "citations_delta", citation = new { type = "web_search_result_location", cited_text = "quoted", url, title, encrypted_index = "stand-in" } } });
        }
        else
        {
            events.Add(new { type = "content_block_start", index = 2, content_block = new { type = "text", text = "" } });
        }

        events.Add(new { type = "content_block_delta", index = 2, delta = new { type = "text_delta", text = answer } });
        events.Add(new { type = "content_block_stop", index = 2 });
        events.Add(new { type = "message_delta", delta = new { stop_reason = "end_turn" } });
        events.Add(new { type = "message_stop" });

        return string.Concat(events.Select(e => "data: " + JsonSerializer.Serialize(e, e.GetType()) + "\n\n"));
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
