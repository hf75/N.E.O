using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Neo.App.Mcp.Internal;

/// <summary>
/// Frozen-Mode merge of Dev-Mode's app-side <c>LiveMcpObservableSubscriptions</c> +
/// server-side <c>LiveMcpResourceRegistry</c>: hooks <see cref="INotifyPropertyChanged"/>
/// on the loaded UserControl, caches the latest JSON value per observable, coalesces
/// rapid-fire updates into one notification per window, and triggers the supplied
/// notification callback so the caller can fire <c>notifications/resources/updated</c>
/// on the MCP wire.
///
/// <para>Cache lookup is the source of truth for <c>resources/read</c>. The cache is seeded
/// from a fresh property read at <see cref="SubscribeAsync"/>-time so reads work even before
/// the first PropertyChanged fires.</para>
/// </summary>
internal sealed class ObservableSubscriptions
{
    private readonly Control _userControl;
    private readonly Func<string, Task> _onUpdated;     // (uri) → push notifications/resources/updated
    private readonly TimeSpan _coalesceWindow;

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveSub> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CoalesceState> _coalesce = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ObservableSubscriptions(Control userControl, Func<string, Task> onUpdated, TimeSpan? coalesceWindow = null)
    {
        _userControl = userControl;
        _onUpdated = onUpdated;
        _coalesceWindow = coalesceWindow ?? TimeSpan.FromMilliseconds(200);
    }

    /// <summary>Cache lookup for <c>resources/read</c>. Returns "null" if no value has been observed yet.</summary>
    public string GetCachedJson(string observableName) =>
        _cache.TryGetValue(observableName, out var v) ? v : "null";

    /// <summary>Hook PropertyChanged for the named observable. Idempotent.</summary>
    public async Task SubscribeAsync(string observableName)
    {
        if (string.IsNullOrEmpty(observableName)) return;

        var prop = _userControl.GetType().GetProperty(
            observableName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (prop == null || !prop.CanRead) return;

        Action? unhook = null;
        if (_userControl is INotifyPropertyChanged inpc)
        {
            PropertyChangedEventHandler handler = (s, e) =>
            {
                if (e.PropertyName == observableName)
                    _ = ReadAndPushAsync(observableName, prop);
            };
            inpc.PropertyChanged += handler;
            unhook = () => inpc.PropertyChanged -= handler;
        }

        lock (_gate)
        {
            if (_active.TryGetValue(observableName, out var existing))
                existing.Unhook?.Invoke();
            _active[observableName] = new ActiveSub(unhook);
        }

        // Seed the cache with the current value so resources/read returns something
        // sensible even before the first PropertyChanged fire.
        await ReadAndCacheAsync(observableName, prop);
    }

    public Task UnsubscribeAsync(string observableName)
    {
        if (string.IsNullOrEmpty(observableName)) return Task.CompletedTask;
        lock (_gate)
        {
            if (_active.TryGetValue(observableName, out var sub))
            {
                sub.Unhook?.Invoke();
                _active.Remove(observableName, out _);
            }
        }
        _coalesce.TryRemove(observableName, out _);
        return Task.CompletedTask;
    }

    public bool IsSubscribed(string observableName) => _active.ContainsKey(observableName);

    private async Task ReadAndCacheAsync(string name, PropertyInfo prop)
    {
        try
        {
            var value = await Dispatcher.UIThread.InvokeAsync(() => prop.GetValue(_userControl));
            var json = value == null
                ? "null"
                : JsonSerializer.Serialize(value, value.GetType(), JsonOptions.Default);
            _cache[name] = json;
        }
        catch
        {
            // Read errors are non-fatal — keep the prior cached value (or "null" on first error).
        }
    }

    private async Task ReadAndPushAsync(string name, PropertyInfo prop)
    {
        await ReadAndCacheAsync(name, prop);

        // Coalesce rapid-fire updates: if a fire is already pending in the window, this
        // change folds into it. Same semantics as Dev-Mode LiveMcpResourceRegistry.
        var state = _coalesce.GetOrAdd(name, _ => new CoalesceState());
        lock (state)
        {
            if (state.Scheduled) return;
            state.Scheduled = true;
        }

        _ = FireAfterDelayAsync(name, state);
    }

    private async Task FireAfterDelayAsync(string name, CoalesceState state)
    {
        await Task.Delay(_coalesceWindow);
        lock (state) { state.Scheduled = false; }

        if (!_active.ContainsKey(name)) return;
        try { await _onUpdated(Naming.BuildResourceUri(name)); }
        catch (Exception ex) { Console.Error.WriteLine($"[neo-mcp] resources/updated push failed for '{name}': {ex.Message}"); }
    }

    /// <summary>Drop all subscriptions — call on shutdown.</summary>
    public void DisposeAll()
    {
        lock (_gate)
        {
            foreach (var sub in _active.Values) sub.Unhook?.Invoke();
            _active.Clear();
        }
    }

    private sealed record ActiveSub(Action? Unhook);
    private sealed class CoalesceState { public bool Scheduled; }
}
