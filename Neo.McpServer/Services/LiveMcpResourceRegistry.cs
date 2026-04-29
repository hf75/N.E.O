using System.Collections.Concurrent;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Neo.IPC;
using Neo.McpServer.LiveMcp;
using McpServerInstance = ModelContextProtocol.Server.McpServer;

namespace Neo.McpServer.Services;

/// <summary>
/// Phase 2B watch_observable registry. Owns the <see cref="McpServerPrimitiveCollection{T}"/> of
/// dynamic resources (one per <c>[McpObservable(Watchable = true)]</c>), caches the last value
/// per URI, and coalesces rapid-fire <c>ObservableValue</c> IPC frames into a single
/// <c>notifications/resources/updated</c> per coalesce window.
///
/// <para><b>Why coalesce?</b> A bound counter that ticks 60 times a second would otherwise
/// spam Claude with notifications and a follow-up <c>resources/read</c> per tick. The 200 ms
/// window collapses bursts; if no further change arrives within the window, exactly one
/// notification fires. Per VISION.md §8.</para>
///
/// <para><b>Hot-reload behaviour:</b> resources are tracked per (appId, observableName).
/// Subscribed URIs survive a manifest refresh — when a new manifest arrives we re-issue
/// <see cref="SubscribeObservableMessage"/> IPC to the app for any URI Claude is still
/// subscribed to, since the new UserControl instance has fresh INPC state.</para>
/// </summary>
public sealed class LiveMcpResourceRegistry
{
    private readonly McpServerResourceCollection _resourceCollection = new();

    // appId → (observableName → resource)
    private readonly ConcurrentDictionary<string, Dictionary<string, RegisteredResource>> _byApp =
        new(StringComparer.OrdinalIgnoreCase);

    // uri → last cached value (JSON). Survives hot-reloads so resources/read returns something
    // sensible until the new control emits its first ObservableValue.
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    // uri → set indicator. Entries exist iff Claude has an active resources/subscribe.
    private readonly ConcurrentDictionary<string, bool> _subscribed = new(StringComparer.Ordinal);

    // uri → pending coalesce timer state.
    private readonly ConcurrentDictionary<string, CoalesceState> _coalesce = new(StringComparer.Ordinal);

    private readonly TimeSpan _coalesceWindow;
    private readonly object _mutateGate = new();

    private McpServerInstance? _server;

    public LiveMcpResourceRegistry() : this(coalesceWindow: null) { }

    /// <summary>Test seam — short coalesce window for unit tests.</summary>
    internal LiveMcpResourceRegistry(TimeSpan? coalesceWindow)
    {
        _coalesceWindow = coalesceWindow ?? TimeSpan.FromMilliseconds(200);
    }

    /// <summary>The collection passed to <c>McpServerOptions.ResourceCollection</c>.</summary>
    public McpServerResourceCollection ResourceCollection => _resourceCollection;

    /// <summary>Wire the running McpServer instance so notifications can be pushed.</summary>
    public void SetServer(McpServerInstance server) => _server = server;

    /// <summary>Diagnostic: URIs currently registered for one app.</summary>
    public IReadOnlyList<string> GetRegisteredUrisForApp(string appId) =>
        _byApp.TryGetValue(appId, out var map)
            ? map.Values.Select(r => r.Uri).ToList()
            : Array.Empty<string>();

    /// <summary>Compute the canonical resource URI for an (appId, observableName).</summary>
    public static string BuildUri(string appId, string observableName) =>
        $"app://{LiveMcpToolRegistry.SanitizeAppId(appId)}/{observableName}";

    // ─────────────────────────────────────────────────────────────────────────
    // Manifest-driven (re)registration
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Register or refresh resources for an app. Returns the list of URIs that need a
    /// re-issued Subscribe IPC frame because (a) they're still subscribed by Claude and
    /// (b) the manifest still exposes them. Caller (PreviewSessionManager) does the actual
    /// IPC send so we don't need an IPC dependency in this service.
    /// </summary>
    public IReadOnlyList<string> RegisterApp(string appId, AppManifestMessage manifest)
    {
        if (string.IsNullOrEmpty(appId)) throw new ArgumentException("appId required.", nameof(appId));
        if (manifest == null) throw new ArgumentNullException(nameof(manifest));

        var desired = new Dictionary<string, McpObservableEntry>(StringComparer.Ordinal);
        foreach (var o in manifest.Observables.Where(o => o.Watchable))
            desired[BuildUri(appId, o.Name)] = o;

        var resubscribe = new List<string>();

        lock (_mutateGate)
        {
            var existing = _byApp.GetOrAdd(appId, _ => new Dictionary<string, RegisteredResource>(StringComparer.Ordinal));

            // Remove URIs that are gone.
            var toRemove = existing.Keys.Where(name => !desired.ContainsKey(BuildUri(appId, name))).ToList();
            foreach (var name in toRemove)
            {
                if (existing.TryGetValue(name, out var rr))
                {
                    _resourceCollection.Remove(rr.Resource);
                    _cache.TryRemove(rr.Uri, out _);
                    _subscribed.TryRemove(rr.Uri, out _);
                    _coalesce.TryRemove(rr.Uri, out _);
                    existing.Remove(name);
                }
            }

            // Add URIs that are new; re-mark surviving subscriptions for re-issue.
            foreach (var (uri, obs) in desired)
            {
                if (!existing.ContainsKey(obs.Name))
                {
                    var resource = new LiveMcpDynamicResource(
                        uri: uri,
                        name: obs.Name,
                        description: obs.Description,
                        readCached: u => _cache.TryGetValue(u, out var v) ? v : "null");
                    _resourceCollection.Add(resource);
                    existing[obs.Name] = new RegisteredResource(uri, resource);
                }
                if (_subscribed.ContainsKey(uri))
                    resubscribe.Add(obs.Name);
            }
        }

        return resubscribe;
    }

    public void UnregisterApp(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return;
        lock (_mutateGate)
        {
            if (!_byApp.TryRemove(appId, out var map)) return;
            foreach (var rr in map.Values)
            {
                _resourceCollection.Remove(rr.Resource);
                _cache.TryRemove(rr.Uri, out _);
                _subscribed.TryRemove(rr.Uri, out _);
                _coalesce.TryRemove(rr.Uri, out _);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Subscriptions (called from MCP request handlers)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mark a URI as subscribed. Returns the (appId, observableName) pair so the caller can
    /// send <see cref="SubscribeObservableMessage"/> IPC to the right app — or <c>null</c> if
    /// the URI does not match a registered resource.
    /// </summary>
    public (string AppId, string Name)? Subscribe(string uri)
    {
        var match = TryResolveUri(uri);
        if (match == null) return null;
        _subscribed[uri] = true;
        return match;
    }

    public (string AppId, string Name)? Unsubscribe(string uri)
    {
        var match = TryResolveUri(uri);
        if (match == null) return null;
        _subscribed.TryRemove(uri, out _);
        _coalesce.TryRemove(uri, out _);
        return match;
    }

    /// <summary>True if Claude currently has an active subscription on the given URI.</summary>
    public bool IsSubscribed(string uri) => _subscribed.ContainsKey(uri);

    private (string AppId, string Name)? TryResolveUri(string uri)
    {
        foreach (var (appId, map) in _byApp)
        {
            foreach (var (name, rr) in map)
            {
                if (rr.Uri == uri) return (appId, name);
            }
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Inbound IPC: app pushed an ObservableValue
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Update the cache and, if Claude is subscribed, schedule a coalesced
    /// <c>notifications/resources/updated</c>. Safe to call regardless of subscription state.
    /// </summary>
    public void OnObservableValue(string appId, string observableName, string valueJson)
    {
        var uri = BuildUri(appId, observableName);
        _cache[uri] = valueJson ?? "null";

        if (!_subscribed.ContainsKey(uri)) return;

        var state = _coalesce.GetOrAdd(uri, _ => new CoalesceState());
        lock (state)
        {
            state.LastUpdateUtc = DateTime.UtcNow;
            if (state.Scheduled) return;        // a fire is already pending in the window
            state.Scheduled = true;
            _ = FireAfterDelayAsync(uri, state);
        }
    }

    private async Task FireAfterDelayAsync(string uri, CoalesceState state)
    {
        await Task.Delay(_coalesceWindow);
        lock (state) { state.Scheduled = false; }

        if (!_subscribed.ContainsKey(uri)) return;
        if (_server == null) return;

        try
        {
            await _server.SendNotificationAsync(
                NotificationMethods.ResourceUpdatedNotification,
                new ResourceUpdatedNotificationParams { Uri = uri },
                serializerOptions: null,
                cancellationToken: default);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[live-mcp] resources/updated push failed for '{uri}': {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    private sealed record RegisteredResource(string Uri, McpServerResource Resource);
    private sealed class CoalesceState
    {
        public bool Scheduled;
        public DateTime LastUpdateUtc;
    }
}
