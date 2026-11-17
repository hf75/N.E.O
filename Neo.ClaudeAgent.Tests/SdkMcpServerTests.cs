using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace Neo.ClaudeAgent.Tests;

public class SdkMcpServerTests
{
    private static readonly SdkMcpServer s_server = new("stage",
    [
        new ClaudeAgentTool("echo", "Gives the text back.",
            JsonDocument.Parse("""{ "type": "object", "properties": { "text": { "type": "string" } }, "required": ["text"] }""").RootElement,
            (input, _) => Task.FromResult(new ClaudeAgentToolResult("echo:" + input.GetProperty("text").GetString()))),
        new ClaudeAgentTool("look", "Gives a picture.", JsonDocument.Parse("""{ "type": "object" }""").RootElement,
            (_, _) => Task.FromResult(new ClaudeAgentToolResult("a picture", Image: new ClaudeAgentImage([1, 2, 3])))),
        new ClaudeAgentTool("fail", "Throws.", JsonDocument.Parse("""{ "type": "object" }""").RootElement,
            (_, _) => throw new InvalidOperationException("no window there")),
    ]);

    private static Task<JsonObject?> Ask(string json) => s_server.HandleAsync(JsonNode.Parse(json)!.AsObject(), CancellationToken.None);

    [Fact]
    public async Task Initialize_names_the_server_and_takes_the_protocol_version()
    {
        var answer = await Ask("""{ "jsonrpc": "2.0", "id": 7, "method": "initialize", "params": { "protocolVersion": "2025-11-25" } }""");

        answer!["id"]!.GetValue<int>().Should().Be(7);
        answer["result"]!["protocolVersion"]!.GetValue<string>().Should().Be("2025-11-25");
        answer["result"]!["serverInfo"]!["name"]!.GetValue<string>().Should().Be("stage");
        answer["result"]!["capabilities"]!["tools"].Should().NotBeNull();
    }

    [Fact]
    public async Task The_tools_are_listed_with_their_schema()
    {
        var answer = await Ask("""{ "jsonrpc": "2.0", "id": "a", "method": "tools/list" }""");

        var tools = answer!["result"]!["tools"]!.AsArray();
        tools.Select(tool => tool!["name"]!.GetValue<string>()).Should().Equal("echo", "look", "fail");
        tools[0]!["inputSchema"]!["required"]![0]!.GetValue<string>().Should().Be("text");
        answer["id"]!.GetValue<string>().Should().Be("a");
    }

    [Fact]
    public async Task A_call_runs_the_tool_and_hands_back_text_and_picture()
    {
        var echo = await Ask("""{ "jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": { "name": "echo", "arguments": { "text": "hi" } } }""");
        var look = await Ask("""{ "jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": { "name": "look", "arguments": {} } }""");

        echo!["result"]!["content"]![0]!["text"]!.GetValue<string>().Should().Be("echo:hi");
        echo["result"]!["isError"]!.GetValue<bool>().Should().BeFalse();
        look!["result"]!["content"]![1]!["type"]!.GetValue<string>().Should().Be("image");
        look["result"]!["content"]![1]!["data"]!.GetValue<string>().Should().Be("AQID");
        look["result"]!["content"]![1]!["mimeType"]!.GetValue<string>().Should().Be("image/png");
    }

    [Fact]
    public async Task A_tool_that_throws_tells_Claude_what_went_wrong()
    {
        var answer = await Ask("""{ "jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": { "name": "fail" } }""");

        answer!["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        answer["result"]!["content"]![0]!["text"]!.GetValue<string>().Should().Contain("no window there");
    }

    [Fact]
    public async Task Unknown_methods_and_tools_are_errors_and_notifications_get_no_answer()
    {
        var method = await Ask("""{ "jsonrpc": "2.0", "id": 4, "method": "resources/list" }""");
        var tool = await Ask("""{ "jsonrpc": "2.0", "id": 5, "method": "tools/call", "params": { "name": "nothing" } }""");
        var notification = await Ask("""{ "jsonrpc": "2.0", "method": "notifications/initialized" }""");

        method!["error"]!["code"]!.GetValue<int>().Should().Be(-32601);
        tool!["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
        notification.Should().BeNull();
    }
}
