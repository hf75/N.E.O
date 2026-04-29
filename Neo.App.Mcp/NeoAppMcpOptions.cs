namespace Neo.App.Mcp;

/// <summary>
/// Configuration for <see cref="NeoAppMcp.RunStdioAsync"/>. All values are optional —
/// defaults make sense for the typical "exported single-file Avalonia app" use case.
/// </summary>
public sealed class NeoAppMcpOptions
{
    /// <summary>
    /// MCP <c>serverInfo.name</c> reported on initialize. Defaults to the entry-assembly name
    /// so a user who runs <c>my-todo-app.exe --mcp</c> sees the server identify itself as
    /// <c>my-todo-app</c>. Override to publish a stable display name.
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>MCP <c>serverInfo.version</c>. Defaults to the entry-assembly informational version.</summary>
    public string? ServerVersion { get; set; }

    /// <summary>
    /// Coalesce window for <c>notifications/resources/updated</c>. Defaults to 200 ms — same as
    /// Dev-Mode. Lower values reduce latency on rapid-fire properties at the cost of more
    /// MCP traffic. Test seam.
    /// </summary>
    public TimeSpan? ResourceCoalesceWindow { get; set; }
}
