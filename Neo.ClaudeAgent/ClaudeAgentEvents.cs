using System.Text.Json;

namespace Neo.ClaudeAgent;

/// <summary>Something a running Claude Code session reports. Raised on the session's reading thread.</summary>
public abstract record ClaudeAgentEvent;

/// <summary>Claude Code is ready for a turn: its model, its tools and how its MCP servers are doing.</summary>
public sealed record ClaudeAgentReady(
    string Model,
    IReadOnlyList<string> Tools,
    IReadOnlyList<ClaudeAgentMcpServerStatus> McpServers,
    string? PermissionMode,
    string? ClaudeCodeVersion,
    string? SessionId) : ClaudeAgentEvent;

public sealed record ClaudeAgentMcpServerStatus(string Name, string Status);

/// <summary>Words Claude is writing, as they come.</summary>
public sealed record ClaudeAgentTextDelta(string Text) : ClaudeAgentEvent;

/// <summary>A finished block of Claude's text.</summary>
public sealed record ClaudeAgentText(string Text) : ClaudeAgentEvent;

/// <summary>Claude starts a tool. A tool of a sub-agent carries the id of the tool use that started that sub-agent.</summary>
public sealed record ClaudeAgentToolStarted(string Id, string Name, JsonElement Input, string? ParentToolUseId) : ClaudeAgentEvent;

/// <summary>A tool is done.</summary>
public sealed record ClaudeAgentToolFinished(string Id, string Name, bool IsError, string Output, TimeSpan Duration) : ClaudeAgentEvent;

/// <summary>Claude Code asked whether it may use a tool, and this is what it was told.</summary>
public sealed record ClaudeAgentPermissionAsked(ClaudeAgentPermissionRequest Request, ClaudeAgentPermission Decision) : ClaudeAgentEvent;

/// <summary>A turn is over.</summary>
public sealed record ClaudeAgentTurnEnded(ClaudeAgentTurnResult Result) : ClaudeAgentEvent;

/// <summary>Claude Code ended. <see cref="ErrorOutput"/> is the end of what it wrote to its error output.</summary>
public sealed record ClaudeAgentExited(int? ExitCode, string ErrorOutput) : ClaudeAgentEvent;

/// <summary>How a turn ended, what it cost and how long it took.</summary>
public sealed record ClaudeAgentTurnResult(
    bool Succeeded,
    string Subtype,
    string Text,
    TimeSpan Duration,
    TimeSpan ApiDuration,
    int Rounds,
    double CostUsd,
    double SessionCostUsd,
    ClaudeAgentUsage Usage,
    string? SessionId,
    IReadOnlyList<string> PermissionDenials);

/// <summary>Tokens of a turn: new input, input written to and read from the prompt cache, output.</summary>
public sealed record ClaudeAgentUsage(long InputTokens, long CacheCreationInputTokens, long CacheReadInputTokens, long OutputTokens);

/// <summary>The session cannot go on: Claude Code ended, refused a request or did not answer in time.</summary>
public sealed class ClaudeAgentException(string message, Exception? inner = null) : Exception(message, inner);
