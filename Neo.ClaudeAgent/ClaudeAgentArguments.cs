using System.Globalization;
using System.Text.Json.Nodes;

namespace Neo.ClaudeAgent;

/// <summary>Claude Code's command line for a session, as the official Agent SDKs build it: stream-json both ways.</summary>
internal static class ClaudeAgentArguments
{
    public static List<string> For(ClaudeAgentOptions options)
    {
        var arguments = new List<string> { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose" };
        if (options.PartialMessages)
            arguments.Add("--include-partial-messages");
        if (!string.IsNullOrWhiteSpace(options.Model))
            arguments.AddRange(["--model", options.Model]);
        if (!string.IsNullOrWhiteSpace(options.Effort))
            arguments.AddRange(["--effort", options.Effort]);
        if (options.SystemPrompt != null)
            arguments.AddRange(["--system-prompt", options.SystemPrompt]);
        if (!string.IsNullOrWhiteSpace(options.AppendSystemPrompt))
            arguments.AddRange(["--append-system-prompt", options.AppendSystemPrompt]);

        if (McpConfig(options) is { } config)
            arguments.AddRange(["--mcp-config", config]);
        if (options.StrictMcpConfig)
            arguments.Add("--strict-mcp-config");

        // Permission prompts come to this process as control requests (can_use_tool).
        if (options.PermissionHandler != null)
            arguments.AddRange(["--permission-prompt-tool", "stdio"]);

        if (options.BuiltInTools != null)
            arguments.AddRange(["--tools", string.Join(",", options.BuiltInTools)]);
        var allowed = options.AllowedTools.ToList();
        if (options.Tools.Count > 0)
            allowed.Add("mcp__" + options.ToolServerName); // our own tools: the handler decides inside
        if (allowed.Count > 0)
            arguments.AddRange(["--allowedTools", string.Join(",", allowed)]);
        if (options.DisallowedTools.Count > 0)
            arguments.AddRange(["--disallowedTools", string.Join(",", options.DisallowedTools)]);

        if (options.Restricted)
            arguments.Add("--restricted");
        if (options.MaxBudgetUsd is { } budget)
            arguments.AddRange(["--max-budget-usd", budget.ToString(CultureInfo.InvariantCulture)]);
        if (!options.PersistSession)
            arguments.Add("--no-session-persistence");
        if (!string.IsNullOrWhiteSpace(options.ResumeSessionId))
            arguments.AddRange(["--resume", options.ResumeSessionId]);

        arguments.AddRange(options.ExtraArguments);
        return arguments;
    }

    /// <summary>The --mcp-config JSON; null when there is no server. Our own tools are a server of type "sdk".</summary>
    public static string? McpConfig(ClaudeAgentOptions options)
    {
        var servers = new JsonObject();
        foreach (var (name, server) in options.McpServers)
        {
            servers[name] = server switch
            {
                ClaudeAgentStdioServer stdio => Stdio(stdio),
                ClaudeAgentHttpServer http => Http(http),
                _ => throw new ArgumentException($"MCP server {name}: {server.GetType().Name} is not supported."),
            };
        }
        if (options.Tools.Count > 0)
            servers[options.ToolServerName] = new JsonObject { ["type"] = "sdk", ["name"] = options.ToolServerName };

        return servers.Count == 0 ? null : new JsonObject { ["mcpServers"] = servers }.ToJsonString();
    }

    private static JsonObject Stdio(ClaudeAgentStdioServer server)
    {
        var json = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = server.Command,
            ["args"] = new JsonArray((server.Arguments ?? []).Select(argument => (JsonNode?)JsonValue.Create(argument)).ToArray()),
        };
        if (server.Environment is { Count: > 0 } environment)
            json["env"] = Dictionary(environment);
        return json;
    }

    private static JsonObject Http(ClaudeAgentHttpServer server)
    {
        var json = new JsonObject { ["type"] = "http", ["url"] = server.Url };
        if (server.Headers is { Count: > 0 } headers)
            json["headers"] = Dictionary(headers);
        return json;
    }

    private static JsonObject Dictionary(IReadOnlyDictionary<string, string> values)
    {
        var json = new JsonObject();
        foreach (var (key, value) in values)
            json[key] = value;
        return json;
    }
}
