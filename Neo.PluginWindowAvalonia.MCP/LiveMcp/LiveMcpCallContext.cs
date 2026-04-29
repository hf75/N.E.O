using System.Threading;

namespace Neo.PluginWindowAvalonia.MCP.LiveMcp;

/// <summary>
/// Per-call context for Live-MCP method invocation. Holds the loop-protection
/// hop counter that flows down from the host's InvokeMethod frame, so any
/// <c>Ai.Trigger</c> call made from inside a [McpCallable] method can stamp
/// the outgoing AppEvent with the correct hop count.
///
/// Implemented as <see cref="AsyncLocal{T}"/> so it flows across
/// <c>await</c> boundaries within a single method dispatch.
/// </summary>
internal static class LiveMcpCallContext
{
    private static readonly AsyncLocal<int?> _hops = new();

    /// <summary>Current hop count, or 0 if the call did not originate from InvokeMethod.</summary>
    public static int CurrentHops => _hops.Value ?? 0;

    public static void Set(int hops) => _hops.Value = hops;
    public static void Clear() => _hops.Value = null;
}
