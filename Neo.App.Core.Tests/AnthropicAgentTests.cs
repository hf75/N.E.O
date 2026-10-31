using FluentAssertions;
using Neo.Agents;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Neo.App.Core.Tests;

/// <summary>
/// What the Claude agent sends and how it reads answers, without the network. The answers follow
/// what the real API returned on 2026-10-31. KnownModels lives as long as the process, so every test
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

    private sealed class FakeApi : HttpMessageHandler
    {
        public string Model = ModelInfo(true);
        public HttpStatusCode ModelStatus = HttpStatusCode.OK;
        public bool ModelLookupFails;
        public string Message = TextAnswer(Json);
        public HttpStatusCode MessageStatus = HttpStatusCode.OK;

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
            return new HttpResponseMessage(MessageStatus) { Content = new StringContent(Message) };
        }

        public IEnumerable<HttpRequestMessage> Lookups => Calls.Where(c => c.Request.Method == HttpMethod.Get).Select(c => c.Request);
        public HttpRequestMessage LastMessage => Calls.Last(c => c.Request.Method == HttpMethod.Post).Request;
        public JsonElement LastBody => JsonDocument.Parse(Calls.Last(c => c.Request.Method == HttpMethod.Post).Body).RootElement;
    }

    private static async Task<string> Run(FakeApi api, string model, string schema)
    {
        var agent = new AnthropicTextChatAgent { Handler = api };
        agent.SetOption("ApiKey", "test-key");
        agent.SetOption("Model", model);
        agent.SetOption("Temperature", 0.1f);
        agent.SetInput("SystemMessage", "system");
        agent.SetInput("Prompt", "prompt");
        agent.SetInput("History", "");
        agent.SetInput("JsonSchema", schema);

        await agent.ExecuteAsync();

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
        var api = new FakeApi { Message = TextAnswer("OK") };

        var result = await Run(api, NewModel(), "");

        result.Should().Be("OK");
        var body = api.LastBody;
        body.TryGetProperty("output_config", out _).Should().BeFalse();
        body.TryGetProperty("tools", out _).Should().BeFalse();
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
    public async Task Fallbacks_SwitchMarkerInTheAnswerIsIgnored()
    {
        var api = new FakeApi
        {
            Model = ModelInfo(true, "claude-opus-4-8"),
            Message = JsonSerializer.Serialize(new
            {
                content = new object[]
                {
                    new Dictionary<string, object> { ["type"] = "fallback", ["from"] = new { model = "a" }, ["to"] = new { model = "b" } },
                    new { type = "text", text = Json },
                },
                stop_reason = "end_turn",
            }),
        };

        (await Run(api, NewModel(), Schema)).Should().Be(Json);
    }

    // ── Answers that are not an answer ────────────────────────────────────

    [Theory]
    [InlineData(Schema)]
    [InlineData("")]
    public async Task Refusal_IsAClearErrorWithTheCategory(string schema)
    {
        // HTTP 200, as the API sends it, before any output.
        var api = new FakeApi { Message = """{"content":[],"stop_reason":"refusal","stop_details":{"type":"refusal","category":"cyber"}}""" };

        var act = () => Run(api, NewModel(), schema);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("declined").And.Contain("cyber");
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
        var api = new FakeApi
        {
            Model = ModelInfo(false),
            Message = """{"content":[{"type":"tool_use","name":"record_summary","input":{"Chat":"unfinis"}}],"stop_reason":"max_tokens"}""",
        };

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
