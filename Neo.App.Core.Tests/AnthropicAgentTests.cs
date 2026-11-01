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
            sse.Append(Event(new JsonObject { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = new JsonObject { ["type"] = type } }));

            void Delta(JsonObject delta) => sse.Append(Event(new JsonObject { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = delta }));
            if (type == "text")
                foreach (var piece in Pieces((string)block["text"]!))
                    Delta(new JsonObject { ["type"] = "text_delta", ["text"] = piece });
            else if (type == "tool_use")
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
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RawStream ?? ToStream(Message), Encoding.UTF8, "text/event-stream") };
        }

        public IEnumerable<HttpRequestMessage> Lookups => Calls.Where(c => c.Request.Method == HttpMethod.Get).Select(c => c.Request);
        public HttpRequestMessage LastMessage => Calls.Last(c => c.Request.Method == HttpMethod.Post).Request;
        public JsonElement LastBody => JsonDocument.Parse(Calls.Last(c => c.Request.Method == HttpMethod.Post).Body).RootElement;
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
}
