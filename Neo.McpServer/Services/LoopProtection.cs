using System.Collections.Concurrent;

namespace Neo.McpServer.Services;

/// <summary>
/// Live-MCP loop protection. Tracks per-app call chains so a method that fires
/// <c>Ai.Trigger</c> from inside an <c>invoke_method</c> call cannot drive an
/// unbounded Claude↔App ping-pong.
///
/// <para>
/// Server-side enforcement was chosen over protocol-level passing (Phase 0 Task 3):
/// putting the counter in the protocol relies on Claude correctly threading it
/// through, which is fragile across model versions and clients. Server-side state
/// is tamper-proof from the prompt side.
/// </para>
///
/// Default depth: 5. Configurable via <c>NEO_LIVEMCP_MAX_DEPTH</c> env var.
/// Inactivity decay: 30 s — a fresh user prompt 30 s later starts at hops=1.
/// </summary>
public sealed class LoopProtection
{
    private readonly ConcurrentDictionary<string, CallChain> _chains = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxDepth;
    private readonly TimeSpan _decay;

    public LoopProtection() : this(decay: null) { }

    /// <summary>
    /// Test seam: lets tests inject a short decay window so the time-based reset path can be exercised
    /// without real-time waits. Production code uses the parameterless constructor (30 s decay).
    /// </summary>
    internal LoopProtection(TimeSpan? decay)
    {
        _maxDepth = int.TryParse(Environment.GetEnvironmentVariable("NEO_LIVEMCP_MAX_DEPTH"), out var m) && m > 0
            ? m
            : 5;
        _decay = decay ?? TimeSpan.FromSeconds(30);
    }

    public int MaxDepth => _maxDepth;

    /// <summary>
    /// Called when an <c>invoke_method</c> tool call arrives from Claude.
    /// Increments the per-app chain (or resets it if the chain is older than the
    /// decay window). Returns the new hop count.
    /// </summary>
    /// <exception cref="LoopLimitExceededException">If the increment would exceed <see cref="MaxDepth"/>.</exception>
    public int OnInvokeMethod(string appId)
    {
        var chain = _chains.GetOrAdd(appId, _ => new CallChain());
        lock (chain)
        {
            if (DateTime.UtcNow - chain.LastActivityUtc > _decay) chain.Hops = 0;
            chain.Hops++;
            chain.LastActivityUtc = DateTime.UtcNow;
            if (chain.Hops > _maxDepth)
                throw new LoopLimitExceededException(appId, chain.Hops, _maxDepth);
            return chain.Hops;
        }
    }

    /// <summary>
    /// Called when an <c>AppEvent</c> (typically a user_trigger from <c>Ai.Trigger</c>)
    /// arrives from the app. Seeds the chain with <c>Math.Max(current, hopsFromApp)</c> so
    /// cross-app or app-side-initiated chains keep their hop budget when Claude reacts.
    /// </summary>
    public void OnAppEvent(string appId, int hopsFromApp)
    {
        if (hopsFromApp < 0) hopsFromApp = 0;
        var chain = _chains.GetOrAdd(appId, _ => new CallChain());
        lock (chain)
        {
            chain.Hops = Math.Max(chain.Hops, hopsFromApp);
            chain.LastActivityUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Drop the chain for an app — e.g. when the preview session ends.</summary>
    public void ResetApp(string appId) => _chains.TryRemove(appId, out _);

    private sealed class CallChain
    {
        public int Hops;
        public DateTime LastActivityUtc;
    }
}

public sealed class LoopLimitExceededException : Exception
{
    public string AppId { get; }
    public int Hops { get; }
    public int MaxDepth { get; }

    public LoopLimitExceededException(string appId, int hops, int maxDepth)
        : base($"Live-MCP loop limit exceeded for app '{appId}' at hops={hops} (max={maxDepth}).")
    {
        AppId = appId;
        Hops = hops;
        MaxDepth = maxDepth;
    }
}
