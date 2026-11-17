using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace Neo.ClaudeAgent.Tests;

public class ClaudeAgentArgumentsTests
{
    private static readonly JsonElement s_emptySchema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

    [Fact]
    public void A_session_speaks_stream_json_both_ways_and_keeps_nothing_on_disk()
    {
        var arguments = ClaudeAgentArguments.For(new ClaudeAgentOptions());

        arguments.Should().ContainInConsecutiveOrder("-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose");
        arguments.Should().Contain(["--include-partial-messages", "--strict-mcp-config", "--no-session-persistence"]);
        arguments.Should().NotContain(["--permission-prompt-tool", "--mcp-config", "--model", "--restricted"]);
    }

    [Fact]
    public void Every_option_reaches_the_command_line()
    {
        var options = new ClaudeAgentOptions
        {
            Model = "claude-haiku-5-5",
            Effort = "low",
            SystemPrompt = "Du bist die Bühne.",
            BuiltInTools = ["Read", "Write"],
            AllowedTools = ["mcp__neo-preview"],
            DisallowedTools = ["WebFetch"],
            Restricted = true,
            MaxBudgetUsd = 2.5m,
            PersistSession = true,
            ResumeSessionId = "abc",
            PartialMessages = false,
            PermissionHandler = (_, _) => Task.FromResult(ClaudeAgentPermission.Allow()),
            Tools = [new ClaudeAgentTool("say", "Says something.", s_emptySchema, (_, _) => Task.FromResult(new ClaudeAgentToolResult("ok")))],
            ExtraArguments = ["--safe-mode"],
        };

        var arguments = ClaudeAgentArguments.For(options);

        arguments.Should().ContainInConsecutiveOrder("--model", "claude-haiku-5-5");
        arguments.Should().ContainInConsecutiveOrder("--effort", "low");
        arguments.Should().ContainInConsecutiveOrder("--system-prompt", "Du bist die Bühne.");
        arguments.Should().ContainInConsecutiveOrder("--permission-prompt-tool", "stdio");
        arguments.Should().ContainInConsecutiveOrder("--tools", "Read,Write");
        arguments.Should().ContainInConsecutiveOrder("--allowedTools", "mcp__neo-preview,mcp__neo");
        arguments.Should().ContainInConsecutiveOrder("--disallowedTools", "WebFetch");
        arguments.Should().ContainInConsecutiveOrder("--max-budget-usd", "2.5");
        arguments.Should().ContainInConsecutiveOrder("--resume", "abc");
        arguments.Should().Contain(["--restricted", "--safe-mode"]);
        arguments.Should().NotContain(["--no-session-persistence", "--include-partial-messages", "--append-system-prompt"]);
    }

    [Fact]
    public void The_mcp_config_names_every_server_and_ours_as_an_sdk_server()
    {
        var options = new ClaudeAgentOptions
        {
            McpServers = new Dictionary<string, ClaudeAgentMcpServer>
            {
                ["neo-preview"] = new ClaudeAgentStdioServer(@"C:\neo\Neo.McpServer.exe", ["--x"], new Dictionary<string, string> { ["A"] = "1" }),
                ["web"] = new ClaudeAgentHttpServer("http://127.0.0.1:5000/mcp", new Dictionary<string, string> { ["Authorization"] = "Bearer t" }),
            },
            ToolServerName = "stage",
            Tools = [new ClaudeAgentTool("say", "Says something.", s_emptySchema, (_, _) => Task.FromResult(new ClaudeAgentToolResult("ok")))],
        };

        var servers = JsonNode.Parse(ClaudeAgentArguments.McpConfig(options)!)!["mcpServers"]!;

        servers["neo-preview"]!["type"]!.GetValue<string>().Should().Be("stdio");
        servers["neo-preview"]!["command"]!.GetValue<string>().Should().Be(@"C:\neo\Neo.McpServer.exe");
        servers["neo-preview"]!["args"]![0]!.GetValue<string>().Should().Be("--x");
        servers["neo-preview"]!["env"]!["A"]!.GetValue<string>().Should().Be("1");
        servers["web"]!["type"]!.GetValue<string>().Should().Be("http");
        servers["web"]!["headers"]!["Authorization"]!.GetValue<string>().Should().Be("Bearer t");
        servers["stage"]!["type"]!.GetValue<string>().Should().Be("sdk");
        servers["stage"]!["name"]!.GetValue<string>().Should().Be("stage");
        ClaudeAgentArguments.McpConfig(new ClaudeAgentOptions()).Should().BeNull();
    }
}
