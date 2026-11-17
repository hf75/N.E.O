using System.Text.Json;

namespace Neo.ClaudeAgent;

/// <summary>How a <see cref="ClaudeAgentSession"/> starts Claude Code and what it gives it.</summary>
public sealed class ClaudeAgentOptions
{
    /// <summary>The Claude Code to run; null finds the newest one (<see cref="ClaudeCli.Find(string?)"/>).</summary>
    public string? CliPath { get; init; }

    /// <summary>A model alias or full name (claude-haiku-5-5); null leaves Claude Code's default.</summary>
    public string? Model { get; init; }

    /// <summary>low, medium, high, xhigh or max; null leaves the default.</summary>
    public string? Effort { get; init; }

    /// <summary>Replaces Claude Code's own system prompt. Lean and fast; Claude Code's guidance for its own tools is gone then.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Appended to Claude Code's own system prompt.</summary>
    public string? AppendSystemPrompt { get; init; }

    public string WorkingDirectory { get; init; } = System.Environment.CurrentDirectory;

    /// <summary>MCP servers Claude Code starts or connects to itself, by name.</summary>
    public IReadOnlyDictionary<string, ClaudeAgentMcpServer> McpServers { get; init; } = new Dictionary<string, ClaudeAgentMcpServer>();

    /// <summary>
    /// Tools that run in this process. They are offered as the MCP server <see cref="ToolServerName"/>, whose
    /// messages travel over the session's own pipe as the official SDKs do it; Claude sees them as
    /// mcp__&lt;server&gt;__&lt;name&gt; and may use them without asking.
    /// </summary>
    public IReadOnlyList<ClaudeAgentTool> Tools { get; init; } = [];

    public string ToolServerName { get; init; } = "neo";

    /// <summary>Claude Code's built-in tools to offer (--tools); null offers its default set, empty offers none.</summary>
    public IReadOnlyList<string>? BuiltInTools { get; init; }

    /// <summary>Tools Claude may use without asking, in Claude Code's rule syntax ("Read", "Bash(git *)", "mcp__server").</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    public IReadOnlyList<string> DisallowedTools { get; init; } = [];

    /// <summary>
    /// Claude Code's restricted mode: no tools that run commands or code and no WebFetch unless
    /// <see cref="BuiltInTools"/> names them, no user, project or local settings files, file tools only in the working directory.
    /// </summary>
    public bool Restricted { get; init; }

    /// <summary>Only the MCP servers named here, none from the user's own Claude Code configuration.</summary>
    public bool StrictMcpConfig { get; init; } = true;

    /// <summary>
    /// False puts every tool's description into the prompt at once (ENABLE_TOOL_SEARCH=false). Claude Code otherwise
    /// loads MCP tools through a search first, which costs one more round the first time each tool is used.
    /// </summary>
    public bool? ToolSearch { get; init; }

    /// <summary>Words as they are written (stream events); without them text comes per message.</summary>
    public bool PartialMessages { get; init; } = true;

    public decimal? MaxBudgetUsd { get; init; }

    /// <summary>Keep the conversation on disk so that it can be resumed; off by default.</summary>
    public bool PersistSession { get; init; }

    public string? ResumeSessionId { get; init; }

    /// <summary>
    /// Answers Claude Code's permission prompts. Without one, anything that would ask is denied
    /// (a headless Claude Code does that by itself).
    /// </summary>
    public Func<ClaudeAgentPermissionRequest, CancellationToken, Task<ClaudeAgentPermission>>? PermissionHandler { get; init; }

    /// <summary>Set for Claude Code on top of this program's environment.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>
    /// Claude Code runs on an Anthropic API key (ANTHROPIC_API_KEY). Products other than Anthropic's may not use
    /// a claude.ai login without Anthropic's approval, so this is on unless the host has that approval.
    /// </summary>
    public bool RequireApiKey { get; init; } = true;

    /// <summary>How long Claude Code may take to answer the initialize request.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>An MCP server Claude Code runs itself.</summary>
public abstract record ClaudeAgentMcpServer;

/// <summary>A program Claude Code starts and talks to over its standard input and output.</summary>
public sealed record ClaudeAgentStdioServer(string Command, IReadOnlyList<string>? Arguments = null, IReadOnlyDictionary<string, string>? Environment = null)
    : ClaudeAgentMcpServer;

/// <summary>A server Claude Code reaches over HTTP.</summary>
public sealed record ClaudeAgentHttpServer(string Url, IReadOnlyDictionary<string, string>? Headers = null) : ClaudeAgentMcpServer;

/// <summary>A tool that runs in this process. <see cref="InputSchema"/> is the JSON schema of its input.</summary>
public sealed record ClaudeAgentTool(
    string Name,
    string Description,
    JsonElement InputSchema,
    Func<JsonElement, CancellationToken, Task<ClaudeAgentToolResult>> Run);

/// <summary>What a tool hands back: text, and a picture if it has one.</summary>
public sealed record ClaudeAgentToolResult(string Text, bool IsError = false, ClaudeAgentImage? Image = null);

/// <summary>A picture for Claude, as bytes.</summary>
public sealed record ClaudeAgentImage(byte[] Data, string MediaType = "image/png");

/// <summary>Claude Code asks whether it may use a tool.</summary>
public sealed record ClaudeAgentPermissionRequest(
    string ToolName,
    JsonElement Input,
    string? ToolUseId,
    string? Reason,
    string? BlockedPath,
    string? Title,
    string? Description);

/// <summary>The answer to a permission prompt.</summary>
public sealed record ClaudeAgentPermission(bool Allowed, string? Message = null, JsonElement? UpdatedInput = null)
{
    public static ClaudeAgentPermission Allow(JsonElement? updatedInput = null) => new(true, null, updatedInput);

    public static ClaudeAgentPermission Deny(string message) => new(false, message);
}
