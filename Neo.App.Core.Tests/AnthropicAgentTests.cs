using FluentAssertions;
using Neo.Agents;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Neo.App.Core.Tests;

/// <summary>
/// What the Claude agent sends and how it reads answers, without the network. The agent always
/// streams; a test states the message the API would finish with and <see cref="ToStream"/> sends it
/// the way the API does, in small pieces. KnownModels lives as long as the process, so every test
/// uses a model name of its own.
/// </summary>
public class AnthropicAgentTests
{
    private const string Schema = """{"type":"object","properties":{"Chat":{"type":"string"}},"required":["Chat"],"additionalProperties":false}""";
    private const string Json = """{"Chat":"hi"}""";

    private static string NewModel() => "test-model-" + Guid.NewGuid().ToString("N");

    private static string ModelInfo(bool structuredOutput, params string[] fallbacks) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["capabilities"] = new Dictionary<string, object> { ["structured_outputs"] = new { supported = structuredOutput } },
        ["allowed_fallback_models"] = fallbacks,
    });

    private static string TextAnswer(string text, string stopReason = "end_turn") => JsonSerializer.Serialize(new
    {
        // Newer models always think: the answer is not the first block.
        content = new object[] { new { type = "thinking", thinking = "" }, new { type = "text", text } },
        stop_reason = stopReason,
    });

    private static readonly string ToolAnswer = JsonSerializer.Serialize(new
    {
        content = new object[] { new { type = "tool_use", name = "record_summary", input = new { Chat = "hi" } } },
        stop_reason = "tool_use",
    });

    private static string Event(JsonObject data) => $"event: {data["type"]}\ndata: {data.ToJsonString()}\n\n";

    private static IEnumerable<string> Pieces(string text)
    {
        for (int i = 0; i < text.Length; i += 3)
            yield return text.Substring(i, Math.Min(3, text.Length - i));
    }

    private static string ToStream(string message, bool stopDetailsOnEvent = false, bool complete = true)
    {
        var answer = JsonNode.Parse(message)!;
        var sse = new StringBuilder();
        sse.Append(Event(new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["content"] = new JsonArray() } }));
        sse.Append(Event(new JsonObject { ["type"] = "ping" }));

        int index = 0;
        foreach (var block in answer["content"]!.AsArray())
        {
            var type = (string)block!["type"]!;
            var start = new JsonObject { ["type"] = type };
            if (type == "web_search_tool_result")
                start = block.DeepClone().AsObject(); // it arrives whole
            else if (type == "server_tool_use")
            {
                start["id"] = (string?)block["id"];
                start["name"] = (string?)block["name"];
            }
            sse.Append(Event(new JsonObject { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = start }));

            void Delta(JsonObject delta) => sse.Append(Event(new JsonObject { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = delta }));
            if (type == "text")
            {
                foreach (var piece in Pieces((string)block["text"]!))
                    Delta(new JsonObject { ["type"] = "text_delta", ["text"] = piece });
                foreach (var citation in block["citations"]?.AsArray() ?? [])
                    Delta(new JsonObject { ["type"] = "citations_delta", ["citation"] = citation!.DeepClone() });
            }
            else if (type is "tool_use" or "server_tool_use")
                foreach (var piece in Pieces(block["input"]!.ToJsonString()))
                    Delta(new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = piece });
            else if (type == "thinking")
            {
                Delta(new JsonObject { ["type"] = "thinking_delta", ["thinking"] = "not an answer" });
                Delta(new JsonObject { ["type"] = "signature_delta", ["signature"] = "abc" });
            }

            sse.Append(Event(new JsonObject { ["type"] = "content_block_stop", ["index"] = index }));
            index++;
        }

        var end = new JsonObject { ["stop_reason"] = (string?)answer["stop_reason"] };
        var details = answer["stop_details"]?.DeepClone();
        var messageDelta = new JsonObject { ["type"] = "message_delta", ["delta"] = end };
        if (details != null)
        {
            if (stopDetailsOnEvent) messageDelta["stop_details"] = details;
            else end["stop_details"] = details;
        }
        sse.Append(Event(messageDelta));
        if (complete) sse.Append(Event(new JsonObject { ["type"] = "message_stop" }));
        return sse.ToString();
    }

    private sealed class FakeApi : HttpMessageHandler
    {
        public string Model = ModelInfo(true);
        public HttpStatusCode ModelStatus = HttpStatusCode.OK;
        public bool ModelLookupFails;
        public string Message = TextAnswer(Json);
        // Answers for one request each, before Message takes over.
        public readonly Queue<string> NextMessages = new();
        public HttpStatusCode MessageStatus = HttpStatusCode.OK;
        public string? RawStream;
        public Stream? SlowStream;

        public readonly List<(HttpRequestMessage Request, string Body)> Calls = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));

            if (request.Method == HttpMethod.Get)
            {
                if (ModelLookupFails) throw new HttpRequestException("no network");
                return new HttpResponseMessage(ModelStatus) { Content = new StringContent(Model) };
            }

            if (MessageStatus != HttpStatusCode.OK)
                return new HttpResponseMessage(MessageStatus) { Content = new StringContent(Message) };
            if (SlowStream != null)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(SlowStream) };
            var message = NextMessages.TryDequeue(out var next) ? next : Message;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RawStream ?? ToStream(message), Encoding.UTF8, "text/event-stream") };
        }

        public IEnumerable<HttpRequestMessage> Lookups => Calls.Where(c => c.Request.Method == HttpMethod.Get).Select(c => c.Request);
        public HttpRequestMessage LastMessage => Calls.Last(c => c.Request.Method == HttpMethod.Post).Request;
        public JsonElement LastBody => JsonDocument.Parse(Calls.Last(c => c.Request.Method == HttpMethod.Post).Body).RootElement;
        public int MessageCalls => Calls.Count(c => c.Request.Method == HttpMethod.Post);
    }

    /// <summary>Hands out lines with a pause before each; a pause of -1 never ends.</summary>
    private sealed class SlowStream((int PauseMs, string Line)[] script) : Stream
    {
        private readonly Queue<(int PauseMs, byte[] Bytes)> _left = new(script.Select(s => (s.PauseMs, Encoding.UTF8.GetBytes(s.Line))));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_left.TryDequeue(out var next)) return 0;
            await Task.Delay(next.PauseMs, cancellationToken);
            next.Bytes.CopyTo(buffer);
            return next.Bytes.Length;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<string> Run(FakeApi api, string model, string schema, int? timeoutSeconds = null, CancellationToken? cancellation = null, string? effort = null)
    {
        var agent = new AnthropicTextChatAgent { Handler = api };
        agent.SetOption("ApiKey", "test-key");
        agent.SetOption("Model", model);
        agent.SetOption("Temperature", 0.1f);
        if (timeoutSeconds != null) agent.SetOption("TimeoutSeconds", timeoutSeconds.Value);
        if (effort != null) agent.SetOption("Effort", effort);
        agent.SetInput("SystemMessage", "system");
        agent.SetInput("Prompt", "prompt");
        agent.SetInput("History", "");
        agent.SetInput("JsonSchema", schema);

        await agent.ExecuteAsync(cancellation);

        return agent.GetOutput<string>("Result");
    }

    // ── Which way a schema goes out ───────────────────────────────────────

    [Fact]
    public async Task Schema_ModelWithStructuredOutput_UsesItAndNoTool()
    {
        var api = new FakeApi();

        var result = await Run(api, NewModel(), Schema);

        result.Should().Be(Json);
        var body = api.LastBody;
        body.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString().Should().Be("json_schema");
        body.GetProperty("output_config").GetProperty("format").GetProperty("schema").GetProperty("required")[0].GetString().Should().Be("Chat");
        body.TryGetProperty("tools", out _).Should().BeFalse();
        body.TryGetProperty("tool_choice", out _).Should().BeFalse("Opus 5.5, Sonnet 5.5 and Fable 5.1 answer a forced tool with a 400");
    }

    [Fact]
    public async Task Schema_ModelWithoutStructuredOutput_UsesForcedTool()
    {
        var api = new FakeApi { Model = ModelInfo(false), Message = ToolAnswer };

        var result = await Run(api, NewModel(), Schema);

        JsonDocument.Parse(result).RootElement.GetProperty("Chat").GetString().Should().Be("hi");
        var body = api.LastBody;
        body.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("tool");
        body.GetProperty("tools")[0].GetProperty("input_schema").GetProperty("required")[0].GetString().Should().Be("Chat");
        body.TryGetProperty("output_config", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Schema_ModelLookupWithoutNetwork_FallsBackToForcedTool()
    {
        var api = new FakeApi { ModelLookupFails = true, Message = ToolAnswer };

        var result = await Run(api, NewModel(), Schema);

        result.Should().Contain("hi");
        api.LastBody.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("tool");
    }

    [Fact]
    public async Task UnknownModel_ReportsTheApiErrorOfTheRequest()
    {
        var api = new FakeApi
        {
            ModelStatus = HttpStatusCode.NotFound,
            MessageStatus = HttpStatusCode.NotFound,
            Message = """{"type":"error","error":{"type":"not_found_error","message":"model: nope"}}""",
        };

        var act = () => Run(api, NewModel(), Schema);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain("not_found_error").And.Contain("model: nope");
    }

    [Fact]
    public async Task PlainText_SendsNoSchemaAndReturnsTheText()
    {
        var api = new FakeApi { Message = TextAnswer("Grüße, das kostet 5 €") };

        var result = await Run(api, NewModel(), "");

        result.Should().Be("Grüße, das kostet 5 €");
        var body = api.LastBody;
        body.TryGetProperty("output_config", out _).Should().BeFalse();
        body.TryGetProperty("tools", out _).Should().BeFalse();
    }

    // ── Effort ────────────────────────────────────────────────────────────

    private static string ModelWithEffort(bool structuredOutput, params string[] levels)
    {
        var effort = new Dictionary<string, object> { ["supported"] = levels.Length > 0, ["max"] = new { supported = false } };
        foreach (var level in levels)
            effort[level] = new { supported = true };

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["capabilities"] = new Dictionary<string, object> { ["structured_outputs"] = new { supported = structuredOutput }, ["effort"] = effort },
        });
    }

    [Fact]
    public async Task Effort_ModelThatKnowsTheLevel_GoesOutAlongWithTheSchema()
    {
        var api = new FakeApi { Model = ModelWithEffort(true, "low", "high") };

        await Run(api, NewModel(), Schema, effort: "low");

        api.LastBody.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("low");
        api.LastBody.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString().Should().Be("json_schema");
    }

    [Fact]
    public async Task Effort_WithoutSchema_GoesOutAlone()
    {
        var api = new FakeApi { Model = ModelWithEffort(true, "low") };

        await Run(api, NewModel(), "", effort: "low");

        api.LastBody.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("low");
        api.LastBody.GetProperty("output_config").TryGetProperty("format", out _).Should().BeFalse("a null there is a 400");
    }

    [Fact]
    public async Task Effort_WithForcedTool_GoesOutAlone()
    {
        var api = new FakeApi { Model = ModelWithEffort(false, "low"), Message = ToolAnswer };

        await Run(api, NewModel(), Schema, effort: "low");

        api.LastBody.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("tool");
        api.LastBody.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("low");
    }

    [Theory]
    [InlineData("max")]    // listed, but not supported
    [InlineData("xhigh")]  // not listed at all
    public async Task Effort_LevelTheModelDoesNotHave_IsLeftOut(string level)
    {
        var api = new FakeApi { Model = ModelWithEffort(true, "low", "high") };

        await Run(api, NewModel(), Schema, effort: level);

        api.LastBody.GetProperty("output_config").TryGetProperty("effort", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Effort_ModelThatSaysNothingAboutIt_IsLeftOut()
    {
        var api = new FakeApi();

        await Run(api, NewModel(), "", effort: "low");

        api.LastBody.TryGetProperty("output_config", out _).Should().BeFalse("older models answer the field with a 400");
    }

    [Fact]
    public async Task Effort_ModelLookupWithoutNetwork_IsLeftOut()
    {
        var api = new FakeApi { ModelLookupFails = true };

        await Run(api, NewModel(), "", effort: "low");

        api.LastBody.TryGetProperty("output_config", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Effort_NotSet_LeavesItToTheModel()
    {
        var api = new FakeApi { Model = ModelWithEffort(true, "low") };

        await Run(api, NewModel(), "");

        api.LastBody.TryGetProperty("output_config", out _).Should().BeFalse();
    }

    // ── Sampling parameters ───────────────────────────────────────────────

    [Theory]
    [InlineData(Schema)]
    [InlineData("")]
    public async Task Temperature_IsAcceptedButNeverSent(string schema)
    {
        var api = new FakeApi();

        await Run(api, NewModel(), schema);

        api.LastBody.TryGetProperty("temperature", out _).Should().BeFalse("Claude 4.7 and later answer it with a 400");
        api.LastBody.TryGetProperty("top_p", out _).Should().BeFalse();
    }

    // ── Streaming and the time limit ──────────────────────────────────────

    [Theory]
    [InlineData(Schema)]
    [InlineData("")]
    public async Task Requests_AlwaysAskForAStream(string schema)
    {
        var api = new FakeApi();

        await Run(api, NewModel(), schema);

        api.LastBody.GetProperty("stream").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task TimeLimit_AnAnswerMayTakeLongerThanTheLimit_AsLongAsDataKeepsComing()
    {
        // Three times the limit in total, never more than a third of it without a sign of life.
        var api = new FakeApi { RawStream = null };
        var lines = ToStream(TextAnswer("OK")).Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(e => e + "\n\n").ToList();
        var script = new List<(int, string)>();
        foreach (var line in lines) script.Add((0, line));
        script.InsertRange(2, Enumerable.Range(0, 9).Select(_ => (330, Event(new JsonObject { ["type"] = "ping" }))));
        api.SlowStream = new SlowStream(script.ToArray());

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await Run(api, NewModel(), "", timeoutSeconds: 1);

        result.Should().Be("OK");
        watch.Elapsed.TotalSeconds.Should().BeGreaterThan(2);
    }

    [Fact]
    public async Task TimeLimit_SilenceEndsTheWaitWithAClearError()
    {
        var api = new FakeApi { SlowStream = new SlowStream([(0, Event(new JsonObject { ["type"] = "ping" })), (-1, "")]) };

        var act = () => Run(api, NewModel(), "", timeoutSeconds: 1);

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Message.Should().Contain("1 seconds");
    }

    [Fact]
    public async Task Cancel_IsACancellationNotATimeout()
    {
        var api = new FakeApi { SlowStream = new SlowStream([(-1, "")]) };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = () => Run(api, NewModel(), "", timeoutSeconds: 30, cancellation: cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Stream_ErrorInTheMiddleIsReported()
    {
        var api = new FakeApi
        {
            RawStream = Event(new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject() })
                + Event(new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = "overloaded_error", ["message"] = "Overloaded" } }),
        };

        var act = () => Run(api, NewModel(), Schema);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain("overloaded_error").And.Contain("Overloaded");
    }

    [Fact]
    public async Task Stream_ConnectionLostBeforeTheEndIsAnError()
    {
        var api = new FakeApi { RawStream = ToStream(TextAnswer(Json), complete: false) };

        var act = () => Run(api, NewModel(), Schema);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("ended before");
    }

    // ── Asking the API about a model ──────────────────────────────────────

    [Fact]
    public async Task Capabilities_AreAskedOncePerModel()
    {
        var api = new FakeApi();
        var model = NewModel();

        await Run(api, model, Schema);
        await Run(api, model, Schema);

        api.Lookups.Should().ContainSingle();
        api.Lookups.Single().RequestUri!.ToString().Should().EndWith("/v1/models/" + model);
        api.Lookups.Single().Headers.GetValues("anthropic-beta").Should().Contain("server-side-fallback-2026-06-01");
    }

    [Fact]
    public async Task Capabilities_FailedLookupIsAskedAgain()
    {
        var api = new FakeApi { ModelStatus = HttpStatusCode.InternalServerError, Message = ToolAnswer };
        var model = NewModel();

        await Run(api, model, Schema);
        api.ModelStatus = HttpStatusCode.OK;
        api.Message = TextAnswer(Json);
        await Run(api, model, Schema);

        api.Lookups.Should().HaveCount(2);
        api.LastBody.TryGetProperty("output_config", out _).Should().BeTrue();
    }

    // ── Fallbacks after a declined request ────────────────────────────────

    [Theory]
    [InlineData(Schema)]
    [InlineData("")]
    public async Task Fallbacks_ModelThatAllowsThem_AreRequested(string schema)
    {
        var api = new FakeApi { Model = ModelInfo(true, "claude-opus-4-8") };

        await Run(api, NewModel(), schema);

        api.LastBody.GetProperty("fallbacks").GetString().Should().Be("default");
        api.LastMessage.Headers.GetValues("anthropic-beta").Should().Contain("server-side-fallback-2026-07-01");
    }

    [Fact]
    public async Task Fallbacks_ModelThatDoesNotAllowThem_AreNotSent()
    {
        var api = new FakeApi { Model = ModelInfo(true) };

        await Run(api, NewModel(), Schema);

        api.LastBody.TryGetProperty("fallbacks", out _).Should().BeFalse();
        api.LastMessage.Headers.Contains("anthropic-beta").Should().BeFalse();
    }

    [Fact]
    public async Task Fallbacks_TakingOverBeforeTheAnswer_IsSeamless()
    {
        var api = new FakeApi
        {
            Model = ModelInfo(true, "claude-opus-4-8"),
            Message = JsonSerializer.Serialize(new
            {
                content = new object[] { new { type = "fallback" }, new { type = "text", text = Json } },
                stop_reason = "end_turn",
            }),
        };

        (await Run(api, NewModel(), Schema)).Should().Be(Json);
    }

    [Fact]
    public async Task Fallbacks_TakingOverMidAnswer_ContinuesTheText()
    {
        // On a stream the partial answer stays and the fallback model carries on from it.
        var api = new FakeApi
        {
            Model = ModelInfo(true, "claude-opus-4-8"),
            Message = JsonSerializer.Serialize(new
            {
                content = new object[]
                {
                    new { type = "text", text = """{"Chat":""" },
                    new { type = "fallback" },
                    new { type = "text", text = "\"hi\"}" },
                },
                stop_reason = "end_turn",
            }),
        };

        (await Run(api, NewModel(), Schema)).Should().Be(Json);
    }

    // ── Answers that are not an answer ────────────────────────────────────

    [Theory]
    [InlineData(Schema, false)]
    [InlineData("", false)]
    [InlineData(Schema, true)]
    public async Task Refusal_IsAClearErrorWithTheCategory(string schema, bool detailsOnTheEvent)
    {
        var api = new FakeApi { RawStream = ToStream("""{"content":[],"stop_reason":"refusal","stop_details":{"type":"refusal","category":"cyber"}}""", detailsOnTheEvent) };

        var act = () => Run(api, NewModel(), schema);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("declined").And.Contain("cyber");
    }

    [Fact]
    public async Task Refusal_MidAnswer_DiscardsThePartialText()
    {
        var api = new FakeApi { Message = TextAnswer("""{"Chat":"unfinis""", "refusal") };

        var act = () => Run(api, NewModel(), Schema);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("declined");
    }

    [Fact]
    public async Task Refusal_WithoutDetails_IsStillClear()
    {
        var api = new FakeApi { Message = """{"content":[],"stop_reason":"refusal","stop_details":null}""" };

        var act = () => Run(api, NewModel(), Schema);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("declined");
    }

    [Fact]
    public async Task CutOff_SchemaAnswerIsAnError()
    {
        var api = new FakeApi { Message = TextAnswer("""{"Chat":"unfinis""", "max_tokens") };

        var act = () => Run(api, NewModel(), Schema);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("cut off");
    }

    [Fact]
    public async Task CutOff_ForcedToolAnswerIsAnError()
    {
        // The stream ends in the middle of the tool's JSON, which does not parse.
        var cutOff = Event(new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject() })
            + Event(new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "tool_use" } })
            + Event(new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = """{"Chat":"unfin""" } })
            + Event(new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 })
            + Event(new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = "max_tokens" } })
            + Event(new JsonObject { ["type"] = "message_stop" });
        var api = new FakeApi { Model = ModelInfo(false), RawStream = cutOff };

        var act = () => Run(api, NewModel(), Schema);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("cut off");
    }

    [Fact]
    public async Task CutOff_PlainTextIsReturned()
    {
        var api = new FakeApi { Message = TextAnswer("a long text that ends", "max_tokens") };

        (await Run(api, NewModel(), "")).Should().Be("a long text that ends");
    }

    // ── Web search ────────────────────────────────────────────────────────

    private static readonly (string Title, string Url) PageA = ("Page A", "https://a.example/one");
    private static readonly (string Title, string Url) PageB = ("Page B", "https://b.example/two");

    private static string SearchingModel(bool structuredOutput = true, bool search = true) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["capabilities"] = new Dictionary<string, object>
        {
            ["structured_outputs"] = new { supported = structuredOutput },
            ["server_tools"] = new { web_search = new { supported = search } },
        },
    });

    private static string Answer(string stopReason, params object[] blocks) => JsonSerializer.Serialize(new { content = blocks, stop_reason = stopReason });

    private static object Plain(string text) => new { type = "text", text };

    private static object Searched(string query, string id = "s1") => new { type = "server_tool_use", id, name = "web_search", input = new { query } };

    private static object Found(string id, params (string Title, string Url)[] pages) => new
    {
        type = "web_search_tool_result",
        tool_use_id = id,
        content = pages.Select(p => new { type = "web_search_result", title = p.Title, url = p.Url, encrypted_content = "secret " + p.Url, page_age = "today" }).ToArray(),
    };

    private static object FoundNothingBecause(string error, string id = "s1") => new
    {
        type = "web_search_tool_result",
        tool_use_id = id,
        content = new { type = "web_search_tool_result_error", error_code = error },
    };

    private static object Cited(string text, params (string Title, string Url)[] pages) => new
    {
        type = "text",
        text,
        citations = pages.Select(p => new { type = "web_search_result_location", url = p.Url, title = p.Title, encrypted_index = "index " + p.Url, cited_text = "quoted" }).ToArray(),
    };

    private static AnthropicTextChatAgent SearchingAgent(FakeApi api, string schema = "", int searches = 2, string? guidance = null)
    {
        var agent = new AnthropicTextChatAgent { Handler = api };
        agent.SetOption("ApiKey", "test-key");
        agent.SetOption("Model", NewModel());
        agent.SetOption("WebSearchMaxUses", searches);
        agent.SetInput("SystemMessage", "system");
        agent.SetInput("Prompt", "prompt");
        agent.SetInput("History", "");
        agent.SetInput("JsonSchema", schema);
        if (guidance != null) agent.SetInput("WebSearchGuidance", guidance);
        return agent;
    }

    [Theory]
    [InlineData(Schema)]
    [InlineData("")]
    public async Task WebSearch_Allowed_GoesOutAsOneServerToolNextToTheSchema(string schema)
    {
        var api = new FakeApi { Model = SearchingModel(), Message = TextAnswer(Json) };

        await SearchingAgent(api, schema, searches: 3).ExecuteAsync();

        var tool = api.LastBody.GetProperty("tools").EnumerateArray().Should().ContainSingle().Which;
        tool.GetProperty("type").GetString().Should().Be("web_search_20250305", "the newer versions take twice as long and answer without a list of citations");
        tool.GetProperty("name").GetString().Should().Be("web_search");
        tool.GetProperty("max_uses").GetInt32().Should().Be(3);
        tool.TryGetProperty("input_schema", out _).Should().BeFalse("a server tool has none, and a null there is a 400");
        tool.TryGetProperty("description", out _).Should().BeFalse();
        api.LastBody.TryGetProperty("tool_choice", out _).Should().BeFalse("a forced tool would keep the model from searching");
        api.LastBody.TryGetProperty("output_config", out var config).Should().Be(schema.Length > 0);
        if (schema.Length > 0) config.GetProperty("format").GetProperty("type").GetString().Should().Be("json_schema");
    }

    [Fact]
    public async Task WebSearch_NotAllowed_SendsNoToolAndNoGuidance()
    {
        var api = new FakeApi { Model = SearchingModel() };

        await SearchingAgent(api, searches: 0, guidance: " Search wisely.").ExecuteAsync();

        api.LastBody.TryGetProperty("tools", out _).Should().BeFalse();
        api.LastBody.GetProperty("system").GetString().Should().Be("system");
    }

    [Fact]
    public async Task WebSearch_Guidance_GoesWithTheTool()
    {
        var api = new FakeApi { Model = SearchingModel() };

        await SearchingAgent(api, guidance: " Search wisely.").ExecuteAsync();

        api.LastBody.GetProperty("system").GetString().Should().Be("system Search wisely.");
    }

    [Fact]
    public async Task WebSearch_ModelThatCannotSearch_SaysSoBeforeAnythingGoesOut()
    {
        var api = new FakeApi { Model = SearchingModel(search: false) };

        var act = () => SearchingAgent(api).ExecuteAsync();

        (await act.Should().ThrowAsync<WebSearchUnavailableException>()).Which.Message.Should().Contain("cannot search");
        api.MessageCalls.Should().Be(0);
    }

    [Fact]
    public async Task WebSearch_ModelThatSaysNothingAboutIt_CountsAsNotSearching()
    {
        var api = new FakeApi { Model = ModelInfo(true) };

        var act = () => SearchingAgent(api).ExecuteAsync();

        await act.Should().ThrowAsync<WebSearchUnavailableException>();
    }

    [Fact]
    public async Task WebSearch_SchemaOnAModelThatNeedsAForcedTool_CannotSearch()
    {
        var api = new FakeApi { Model = SearchingModel(structuredOutput: false) };

        var act = () => SearchingAgent(api, Schema).ExecuteAsync();

        await act.Should().ThrowAsync<WebSearchUnavailableException>();
        api.MessageCalls.Should().Be(0);
    }

    [Fact]
    public async Task WebSearch_ModelLookupFailed_LetsTheRequestTellWhatIsWrong()
    {
        var api = new FakeApi { ModelLookupFails = true };

        await SearchingAgent(api).ExecuteAsync();

        api.LastBody.GetProperty("tools")[0].GetProperty("name").GetString().Should().Be("web_search");
    }

    [Theory]
    [InlineData("web search is not enabled for this organization", true)]
    [InlineData("Web Search is not enabled", true)]
    [InlineData("The web_search tool is not enabled", true)]
    [InlineData("max_tokens: too large", false)]
    public async Task WebSearch_SwitchedOffForTheOrganization_IsAClearError(string apiMessage, bool isAboutSearch)
    {
        var api = new FakeApi
        {
            Model = SearchingModel(),
            MessageStatus = HttpStatusCode.BadRequest,
            Message = JsonSerializer.Serialize(new { type = "error", error = new { type = "invalid_request_error", message = apiMessage } }),
        };

        var act = () => SearchingAgent(api).ExecuteAsync();

        if (isAboutSearch)
            (await act.Should().ThrowAsync<WebSearchUnavailableException>()).Which.Message.Should().Contain("switched off");
        else
            (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain(apiMessage);
    }

    [Fact]
    public async Task WebSearch_WithoutTheTool_ANotEnabledMessageIsJustAnError()
    {
        var api = new FakeApi
        {
            MessageStatus = HttpStatusCode.BadRequest,
            Message = """{"type":"error","error":{"type":"invalid_request_error","message":"web search is not enabled"}}""",
        };

        var act = () => Run(api, NewModel(), "");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task WebSearch_Answer_IsTheTextAfterTheLastSearch()
    {
        var api = new FakeApi
        {
            Model = SearchingModel(),
            Message = Answer("end_turn",
                new { type = "thinking", thinking = "" },
                Plain("I'll search for that."),
                Searched("first", "s1"),
                Found("s1", PageA),
                Plain("Now the second part."),
                Searched("second", "s2"),
                Found("s2", PageB),
                Plain("Based on the results, "),
                Cited("it is 42.", PageB)),
        };
        var agent = SearchingAgent(api);

        await agent.ExecuteAsync();

        agent.GetOutput<string>("Result").Should().Be("Based on the results, it is 42.");
        agent.GetOutput<List<string>>("WebSearches").Should().Equal("first", "second");
    }

    [Fact]
    public async Task WebSearch_SchemaAnswer_IsCleanJsonEvenAfterAPreamble()
    {
        var api = new FakeApi
        {
            Model = SearchingModel(),
            Message = Answer("end_turn", Plain("Let me look."), Searched("q"), Found("s1", PageA), Plain(Json)),
        };
        var agent = SearchingAgent(api, Schema);

        await agent.ExecuteAsync();

        agent.GetOutput<string>("Result").Should().Be(Json);
    }

    [Fact]
    public async Task WebSearch_FallbackModelTakingOverAfterTheSearch_StillContinuesTheText()
    {
        var api = new FakeApi
        {
            Model = SearchingModel(),
            Message = Answer("end_turn", Searched("q"), Found("s1", PageA), Plain("""{"Chat":"""), new { type = "fallback" }, Plain("\"hi\"}")),
        };
        var agent = SearchingAgent(api, Schema);

        await agent.ExecuteAsync();

        agent.GetOutput<string>("Result").Should().Be(Json);
    }

    [Fact]
    public async Task WebSearch_Sources_AreTheCitedPagesOncePerPage()
    {
        var api = new FakeApi
        {
            Model = SearchingModel(),
            Message = Answer("end_turn",
                Searched("q"),
                Found("s1", PageA, PageB, ("Never cited", "https://c.example")),
                Cited("One. ", PageB),
                Cited("Two. ", PageA, PageB)),
        };
        var agent = SearchingAgent(api);

        await agent.ExecuteAsync();

        agent.GetOutput<List<WebSource>>("WebSources").Should().Equal(
            new WebSource("Page B", "https://b.example/two"),
            new WebSource("Page A", "https://a.example/one"));
    }

    [Fact]
    public async Task WebSearch_Sources_WithoutCitations_AreThePagesFound()
    {
        // A schema answer carries no citations.
        var api = new FakeApi
        {
            Model = SearchingModel(),
            Message = Answer("end_turn", Searched("q", "s1"), Found("s1", PageA), Searched("q2", "s2"), Found("s2", PageA, PageB), Plain(Json)),
        };
        var agent = SearchingAgent(api, Schema);

        await agent.ExecuteAsync();

        agent.GetOutput<List<WebSource>>("WebSources").Select(s => s.Url).Should().Equal(PageA.Url, PageB.Url);
    }

    [Theory]
    [InlineData("max_uses_exceeded")]
    [InlineData("too_many_requests")]
    [InlineData("unavailable")]
    public async Task WebSearch_ASearchThatFailed_LeavesNoSourcesAndStillAnAnswer(string error)
    {
        var api = new FakeApi
        {
            Model = SearchingModel(),
            Message = Answer("end_turn", Searched("q"), FoundNothingBecause(error), Plain("I could not search.")),
        };
        var agent = SearchingAgent(api);

        await agent.ExecuteAsync();

        agent.GetOutput<string>("Result").Should().Be("I could not search.");
        agent.GetOutput<List<WebSource>>("WebSources").Should().BeEmpty();
        agent.GetOutput<List<string>>("WebSearches").Should().BeEmpty("it did not run: past max_uses the model even sends the query 'placeholder'");
    }

    [Fact]
    public async Task WebSearch_Searches_NameOnlyTheOnesThatRan()
    {
        var api = new FakeApi
        {
            Model = SearchingModel(),
            Message = Answer("end_turn", Searched("real", "s1"), Found("s1", PageA), Searched("placeholder", "s2"), FoundNothingBecause("max_uses_exceeded", "s2"), Cited("Done.", PageA)),
        };
        var agent = SearchingAgent(api);

        await agent.ExecuteAsync();

        agent.GetOutput<List<string>>("WebSearches").Should().Equal("real");
    }

    [Fact]
    public async Task WebSearch_NoSearchMade_LeavesEmptyLists()
    {
        var api = new FakeApi { Model = SearchingModel(), Message = TextAnswer("I know this.") };
        var agent = SearchingAgent(api);

        await agent.ExecuteAsync();

        agent.GetOutput<string>("Result").Should().Be("I know this.");
        agent.GetOutput<List<WebSource>>("WebSources").Should().BeEmpty();
        agent.GetOutput<List<string>>("WebSearches").Should().BeEmpty();
    }

    [Fact]
    public async Task WebSearch_Results_DoNotCarryOverToTheNextRequest()
    {
        var api = new FakeApi { Model = SearchingModel() };
        api.NextMessages.Enqueue(Answer("end_turn", Searched("q"), Found("s1", PageA), Cited("Yes.", PageA)));
        var agent = SearchingAgent(api);

        await agent.ExecuteAsync();
        agent.GetOutput<List<string>>("WebSearches").Should().NotBeEmpty();
        api.Message = TextAnswer("Plain.");
        await agent.ExecuteAsync();

        agent.GetOutput<List<string>>("WebSearches").Should().BeEmpty();
        agent.GetOutput<List<WebSource>>("WebSources").Should().BeEmpty();
    }

    [Fact]
    public async Task WebSearch_PausedTurn_SendsThePausedPartBackUnchangedAndCarriesOn()
    {
        var api = new FakeApi { Model = SearchingModel() };
        api.NextMessages.Enqueue(Answer("pause_turn",
            new { type = "thinking", thinking = "" },
            Plain("Looking."),
            Cited("Early finding. ", PageA),
            Searched("first"),
            Found("s1", PageA)));
        api.Message = Answer("end_turn", Searched("second", "s2"), Found("s2", PageB), Cited("Done.", PageB));
        var agent = SearchingAgent(api);

        await agent.ExecuteAsync();

        api.MessageCalls.Should().Be(2);
        agent.GetOutput<string>("Result").Should().Be("Done.");
        agent.GetOutput<List<string>>("WebSearches").Should().Equal("first", "second");
        agent.GetOutput<List<WebSource>>("WebSources").Select(s => s.Url).Should().Equal(PageA.Url, PageB.Url);

        var messages = api.LastBody.GetProperty("messages");
        messages.GetArrayLength().Should().Be(2, "no extra user message: the API resumes by itself");
        messages[0].GetProperty("role").GetString().Should().Be("user");
        messages[1].GetProperty("role").GetString().Should().Be("assistant");

        var sent = messages[1].GetProperty("content");
        sent.GetArrayLength().Should().Be(5);
        sent[0].GetProperty("thinking").GetString().Should().Be("not an answer");
        sent[0].GetProperty("signature").GetString().Should().Be("abc");
        sent[1].GetProperty("text").GetString().Should().Be("Looking.");
        sent[2].GetProperty("citations")[0].GetProperty("encrypted_index").GetString().Should().Be("index " + PageA.Url);
        sent[3].GetProperty("id").GetString().Should().Be("s1");
        sent[3].GetProperty("input").GetProperty("query").GetString().Should().Be("first");
        sent[4].GetProperty("tool_use_id").GetString().Should().Be("s1");
        sent[4].GetProperty("content")[0].GetProperty("encrypted_content").GetString().Should().Be("secret " + PageA.Url);
        api.LastBody.GetProperty("tools")[0].GetProperty("name").GetString().Should().Be("web_search");
    }

    [Fact]
    public async Task WebSearch_PausedTwice_KeepsEveryPausedPart()
    {
        var api = new FakeApi { Model = SearchingModel() };
        api.NextMessages.Enqueue(Answer("pause_turn", Searched("first", "s1"), Found("s1", PageA)));
        api.NextMessages.Enqueue(Answer("pause_turn", Searched("second", "s2"), Found("s2", PageB)));
        api.Message = Answer("end_turn", Plain("Done."));
        var agent = SearchingAgent(api);

        await agent.ExecuteAsync();

        agent.GetOutput<string>("Result").Should().Be("Done.");
        var messages = api.LastBody.GetProperty("messages");
        messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()).Should().Equal("user", "assistant", "assistant");
        messages[1].GetProperty("content")[0].GetProperty("id").GetString().Should().Be("s1");
        messages[2].GetProperty("content")[0].GetProperty("id").GetString().Should().Be("s2");
    }

    [Fact]
    public async Task WebSearch_PausedTurn_KeepsTheHistoryAtTheFront()
    {
        var api = new FakeApi { Model = SearchingModel() };
        api.NextMessages.Enqueue(Answer("pause_turn", Searched("q"), Found("s1", PageA)));
        api.Message = Answer("end_turn", Plain("Done."));
        var agent = SearchingAgent(api);
        agent.SetInput("History", "before");

        await agent.ExecuteAsync();

        var messages = api.LastBody.GetProperty("messages");
        messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()).Should().Equal("assistant", "user", "assistant");
        messages[0].GetProperty("content").GetString().Should().Be("before");
        messages[1].GetProperty("content").GetString().Should().Be("prompt");
    }

    [Fact]
    public async Task WebSearch_NeverEndingPause_SchemaAnswerIsAnError()
    {
        var api = new FakeApi { Model = SearchingModel(), Message = Answer("pause_turn", Plain("""{"Chat":"unfin"""), Searched("q"), Found("s1", PageA)) };

        var act = () => SearchingAgent(api, Schema).ExecuteAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("did not finish");
        api.MessageCalls.Should().BeLessThanOrEqualTo(6, "the requests must not go on for ever");
    }

    [Fact]
    public async Task WebSearch_NeverEndingPause_WithoutAnyTextIsAnError()
    {
        var api = new FakeApi { Model = SearchingModel(), Message = Answer("pause_turn", Searched("q"), Found("s1", PageA)) };

        var act = () => SearchingAgent(api).ExecuteAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task WebSearch_RefusalAfterASearch_IsStillAClearError()
    {
        var api = new FakeApi { Model = SearchingModel(), Message = Answer("refusal", Searched("q"), Found("s1", PageA)) };

        var act = () => SearchingAgent(api).ExecuteAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("declined");
    }

    [Fact]
    public async Task WebSearch_Progress_TellsWhatIsBeingSearchedAndWhenThatIsDone()
    {
        var api = new FakeApi
        {
            Model = SearchingModel(),
            Message = Answer("end_turn", Searched("first", "s1"), Found("s1", PageA), Searched("second", "s2"), FoundNothingBecause("unavailable", "s2"), Plain("Done.")),
        };
        var agent = SearchingAgent(api);
        var seen = new List<string?>();
        agent.WebSearchProgress = seen.Add;

        await agent.ExecuteAsync();

        seen.Should().Equal("first", null, "second", null);
    }

    [Fact]
    public async Task WebSearch_RealStreamShape_InputArrivingEmptyAtTheStart_IsFilledByTheDeltas()
    {
        // The API announces a server tool with an empty input, then streams it.
        var stream = Event(new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject() })
            + Event(new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "server_tool_use", ["id"] = "s1", ["name"] = "web_search", ["input"] = new JsonObject() } })
            + Event(new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = """{"query":"neo""" } })
            + Event(new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = """ news"}""" } })
            + Event(new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 })
            + Event(new JsonObject { ["type"] = "content_block_start", ["index"] = 1, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "", ["citations"] = null } })
            + Event(new JsonObject { ["type"] = "content_block_delta", ["index"] = 1, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = "Answer." } })
            + Event(new JsonObject { ["type"] = "content_block_stop", ["index"] = 1 })
            + Event(new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = "end_turn" } })
            + Event(new JsonObject { ["type"] = "message_stop" });
        var api = new FakeApi { Model = SearchingModel(), RawStream = stream };
        var agent = SearchingAgent(api);

        await agent.ExecuteAsync();

        agent.GetOutput<string>("Result").Should().Be("Answer.");
        agent.GetOutput<List<string>>("WebSearches").Should().Equal("neo news");
    }

    [Fact]
    public async Task WebSearch_PausedPart_LeavesOutNullFieldsTheApiWouldNotTakeBack()
    {
        // A text block starts with "citations":null; sent back like that it is a 400.
        var paused = Event(new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject() })
            + Event(new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "", ["citations"] = null } })
            + Event(new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = "Looking." } })
            + Event(new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 })
            + Event(new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = "pause_turn" } })
            + Event(new JsonObject { ["type"] = "message_stop" });
        var api = new PausedOnceApi(paused, ToStream(Answer("end_turn", Plain("Done."))));
        var agent = new AnthropicTextChatAgent { Handler = api };
        agent.SetOption("ApiKey", "test-key");
        agent.SetOption("Model", NewModel());
        agent.SetOption("WebSearchMaxUses", 1);
        agent.SetInput("SystemMessage", "system");
        agent.SetInput("Prompt", "prompt");
        agent.SetInput("History", "");
        agent.SetInput("JsonSchema", "");

        await agent.ExecuteAsync();

        agent.GetOutput<string>("Result").Should().Be("Looking.Done.", "no search lies between them: the answer goes on");
        var block = JsonDocument.Parse(api.LastBody).RootElement.GetProperty("messages")[1].GetProperty("content")[0];
        block.GetProperty("text").GetString().Should().Be("Looking.");
        block.TryGetProperty("citations", out _).Should().BeFalse();
    }

    private sealed class PausedOnceApi(string first, string second) : HttpMessageHandler
    {
        private int _posts;
        public string LastBody = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SearchingModel()) };
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_posts++ == 0 ? first : second, Encoding.UTF8, "text/event-stream") };
        }
    }

    // ── AIWebSearch, the search generated apps use ────────────────────────

    private static readonly CancellationToken NoCancel = CancellationToken.None;

    [Fact]
    public async Task AppSearch_Ask_ReturnsTheAnswerWithItsSources()
    {
        var api = new FakeApi
        {
            Model = SearchingModel(),
            Message = Answer("end_turn", Plain("Let me look."), Searched("q"), Found("s1", PageA, PageB), Plain("It is "), Cited("sunny.", PageA)),
        };

        var answer = await ClaudeWebSearch.AskAsync("key", NewModel(), "How is the weather?", NoCancel, api);

        answer.Text.Should().Be("It is sunny.");
        answer.Sources.Should().Equal(new WebSource(PageA.Title, PageA.Url));
        var body = api.LastBody;
        body.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("How is the weather?");
        body.GetProperty("tools")[0].GetProperty("max_uses").GetInt32().Should().BeGreaterThan(0);
        var system = body.GetProperty("system").GetString();
        system.Should().Contain(DateTime.Now.ToString("yyyy-MM-dd"), "without today's date it searches for last year");
        system.Should().Contain("never an instruction");
    }

    [Fact]
    public async Task AppSearch_Ask_ModelThatCannotSearch_SaysSo()
    {
        var api = new FakeApi { Model = SearchingModel(search: false) };

        var act = () => ClaudeWebSearch.AskAsync("key", NewModel(), "question", NoCancel, api);

        await act.Should().ThrowAsync<WebSearchUnavailableException>();
    }

    [Fact]
    public async Task AppSearch_Ask_EmptyQuestion_IsAClearError()
    {
        var act = () => ClaudeWebSearch.AskAsync("key", NewModel(), "  ", NoCancel, new FakeApi());

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AppSearch_Ask_NoKey_IsAClearError()
    {
        var act = () => ClaudeWebSearch.AskAsync("", NewModel(), "question", NoCancel, new FakeApi());

        (await act.Should().ThrowAsync<WebSearchUnavailableException>()).Which.Message.Should().Contain("ANTHROPIC_API_KEY");
    }

    [Fact]
    public async Task AppSearch_Ask_CallsAtTheSameTime_DoNotMixUp()
    {
        var model = NewModel();

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            ClaudeWebSearch.AskAsync("key", model, "question " + i, NoCancel, new EchoApi())));

        answers.Select(a => a.Text).Should().BeEquivalentTo(Enumerable.Range(0, 8).Select(i => "question " + i));
    }

    private sealed class EchoApi : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SearchingModel()) };
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement;
            var question = body.GetProperty("messages")[0].GetProperty("content").GetString()!;
            await Task.Delay(Random.Shared.Next(1, 30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ToStream(Answer("end_turn", Plain(question))), Encoding.UTF8, "text/event-stream") };
        }
    }

    public sealed record Launch(string Name, DateTime Date, double Price, bool Crewed, string? Site, List<string> Tags);
    public sealed record Team(string Name, int Points);
    public sealed record Match(Team Home, Team Away);
    public enum Kind { Rocket, Probe }
    public sealed record Mission(string Name, Kind Kind);

    [Fact]
    public async Task AppSearch_Data_AsksForASchemaTheApiTakesAndReturnsTheData()
    {
        var api = new FakeApi
        {
            Model = SearchingModel(),
            Message = Answer("end_turn", Searched("q"), Found("s1", PageA, PageB),
                Plain($$"""{"result":[{"name":"Ariane","date":"2026-11-03T10:00:00Z","price":1.5,"crewed":false,"site":null,"tags":["esa"]}],"usedPages":["{{PageB.Url}}","https://made-up.example"]}""")),
        };

        var answer = await ClaudeWebSearch.AskAsync<List<Launch>>("key", NewModel(), "Next launches?", NoCancel, api);

        answer.Data.Should().ContainSingle().Which.Name.Should().Be("Ariane");
        answer.Data[0].Site.Should().BeNull();
        answer.Data[0].Tags.Should().Equal("esa");
        answer.Sources.Should().Equal([new WebSource(PageB.Title, PageB.Url)], "only pages it used, and only pages the search really found");

        var schema = api.LastBody.GetProperty("output_config").GetProperty("format").GetProperty("schema");
        schema.GetProperty("type").GetString().Should().Be("object", "the API wants an object at the top, also for a list");
        schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).Should().Equal("result", "usedPages");

        var row = schema.GetProperty("properties").GetProperty("result").GetProperty("items");
        row.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        row.GetProperty("required").EnumerateArray().Select(r => r.GetString()).Should().BeEquivalentTo("name", "date", "price", "crewed", "site", "tags");
        row.GetProperty("properties").GetProperty("date").GetProperty("format").GetString().Should().Be("date-time");
    }

    [Fact]
    public async Task AppSearch_Data_NoPageNamedThatTheSearchFound_ShowsThePagesFound()
    {
        var api = new FakeApi
        {
            Model = SearchingModel(),
            Message = Answer("end_turn", Searched("q"), Found("s1", PageA, PageB), Plain("""{"result":{"name":"V","kind":"Rocket"},"usedPages":["https://made-up.example"]}""")),
        };

        var answer = await ClaudeWebSearch.AskAsync<Mission>("key", NewModel(), "Which?", NoCancel, api);

        answer.Sources.Select(s => s.Url).Should().Equal(PageA.Url, PageB.Url);
    }

    [Fact]
    public async Task AppSearch_Data_ATypeUsedTwice_KeepsItsReferenceValidInsideTheWrapper()
    {
        var api = new FakeApi { Model = SearchingModel(), Message = Answer("end_turn", Plain("""{"result":{"home":{"name":"A","points":3},"away":{"name":"B","points":0}}}""")) };

        var answer = await ClaudeWebSearch.AskAsync<Match>("key", NewModel(), "Score?", NoCancel, api);

        answer.Data.Should().Be(new Match(new Team("A", 3), new Team("B", 0)));

        var schema = api.LastBody.GetProperty("output_config").GetProperty("format").GetProperty("schema");
        var match = schema.GetProperty("properties").GetProperty("result");
        foreach (var side in new[] { "home", "away" })
        {
            var property = match.GetProperty("properties").GetProperty(side);
            if (property.TryGetProperty("$ref", out var reference))
                reference.GetString().Should().StartWith("#/properties/result/");
            else
                property.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        }
    }

    [Fact]
    public async Task AppSearch_Data_EnumsTravelAsTheirNames()
    {
        var api = new FakeApi { Model = SearchingModel(), Message = Answer("end_turn", Plain("""{"result":{"name":"Voyager","kind":"Probe"}}""")) };

        var answer = await ClaudeWebSearch.AskAsync<Mission>("key", NewModel(), "Which?", NoCancel, api);

        answer.Data.Should().Be(new Mission("Voyager", Kind.Probe));
        var kind = api.LastBody.GetProperty("output_config").GetProperty("format").GetProperty("schema")
            .GetProperty("properties").GetProperty("result").GetProperty("properties").GetProperty("kind");
        kind.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().Contain("Probe");
    }

    [Fact]
    public async Task AppSearch_Data_APropertyNamedLikeASchemaKeyword_StaysAProperty()
    {
        var schema = JsonNode.Parse(ClaudeWebSearch.DataSchema(typeof(WithKeywordNames), ClaudeWebSearch.DataOptions()))!;

        var result = schema["properties"]!["result"]!;
        result["properties"]!["properties"]!["type"]!.GetValue<string>().Should().Be("string");
        result["properties"]!.AsObject().ContainsKey("additionalProperties").Should().BeFalse();
        result["additionalProperties"]!.GetValue<bool>().Should().BeFalse();
    }

    public sealed record WithKeywordNames(string Properties, string Items);

#nullable disable
    // What generated code looks like: a class with setters, without nullable annotations. The
    // exporter requires nothing here and would let every string be null.
    public sealed class PlainRow
    {
        public string Name { get; set; }
        public int Value { get; set; }
        public int? Maybe { get; set; }
        public List<string> Tags { get; set; }
        public PlainInner Inner { get; set; }
    }

    public sealed record PlainInner(string Text);
#nullable restore

    [Fact]
    public async Task AppSearch_Data_AClassWithSetters_HasEveryPropertyRequiredAndNotNull()
    {
        var api = new FakeApi
        {
            Model = SearchingModel(),
            Message = Answer("end_turn", Plain("""{"result":[{"name":"A","value":1,"maybe":null,"tags":[],"inner":{"text":"x"}}],"usedPages":[]}""")),
        };

        var answer = await ClaudeWebSearch.AskAsync<List<PlainRow>>("key", NewModel(), "Which?", NoCancel, api);

        answer.Data.Should().ContainSingle().Which.Inner.Text.Should().Be("x");
        var result = api.LastBody.GetProperty("output_config").GetProperty("format").GetProperty("schema").GetProperty("properties").GetProperty("result");
        result.GetProperty("type").GetString().Should().Be("array", "the list itself is never null either");
        var row = result.GetProperty("items");
        row.GetProperty("type").GetString().Should().Be("object");
        row.GetProperty("required").EnumerateArray().Select(r => r.GetString()).Should().BeEquivalentTo("name", "value", "maybe", "tags", "inner");

        var properties = row.GetProperty("properties");
        properties.GetProperty("name").GetProperty("type").GetString().Should().Be("string");
        properties.GetProperty("tags").GetProperty("type").GetString().Should().Be("array");
        properties.GetProperty("inner").GetProperty("type").GetString().Should().Be("object");
        properties.GetProperty("inner").GetProperty("properties").GetProperty("text").GetProperty("type").GetString().Should().Be("string");
        properties.GetProperty("maybe").GetProperty("type").EnumerateArray().Select(t => t.GetString()).Should().BeEquivalentTo("integer", "null");
    }

    // ── What N.E.O. knows about the search ────────────────────────────────

    [Fact]
    public void Plugin_DescribesItselfAndCarriesItsHelper()
    {
        var plugin = new ClaudeWebSearchPlugin();

        plugin.RequiredEnvVar.Should().Be("ANTHROPIC_API_KEY");
        plugin.AgentDllName.Should().Be("Neo.Agents.Claude.dll");
        plugin.HelperTemplateCode.Should().Contain("namespace Neo.App").And.Contain("class AIWebSearch");
        plugin.TemplatePlaceholders.Keys.Should().ContainSingle().Which.Should().Match(p => plugin.HelperTemplateCode!.Contains(p));
        plugin.SystemMessageDocs.Should().Contain("AIWebSearch.AskAsync").And.Contain("Sources");
    }
}
