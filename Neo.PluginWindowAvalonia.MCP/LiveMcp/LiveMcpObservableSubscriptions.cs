using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using Neo.IPC;

namespace Neo.PluginWindowAvalonia.MCP.LiveMcp;

/// <summary>
/// Phase 2B watch_observable runtime: tracks the set of properties Claude has subscribed to and
/// hooks <see cref="INotifyPropertyChanged.PropertyChanged"/> on the loaded UserControl so a
/// change fires an outgoing <see cref="ObservableValueMessage"/> IPC frame. The host server
/// coalesces those into <c>notifications/resources/updated</c> for Claude.
///
/// <para>If the UserControl does not implement <see cref="INotifyPropertyChanged"/>, all reads
/// happen at <see cref="Subscribe"/>-time only — change-detection then needs the polling
/// fallback (not yet implemented; see commit message). Apps that want push semantics should
/// implement INPC, which is also the convention the Phase 1 TODO sample uses.</para>
///
/// <para>Subscriptions are scoped to one Control instance — on
/// <see cref="ClearForControlUnload"/> we drop all hookups. The host re-issues Subscribe IPC
/// frames after a hot-reload, since that's where the surviving Claude-side subscription set
/// is tracked.</para>
/// </summary>
internal sealed class LiveMcpObservableSubscriptions
{
    private readonly Func<object?> _getControl;
    private readonly Func<string, string, Task> _emit;            // (propertyName, valueJson) → push IPC
    private readonly Dictionary<string, ActiveSub> _active = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public LiveMcpObservableSubscriptions(Func<object?> getControl, Func<string, string, Task> emit)
    {
        _getControl = getControl;
        _emit = emit;
    }

    /// <summary>
    /// Begin watching a property. Idempotent — calling twice with the same name is a no-op.
    /// Emits the current value once so the host's cache has a starting point even before any
    /// change fires.
    /// </summary>
    public async Task SubscribeAsync(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return;

        var control = await Dispatcher.UIThread.InvokeAsync(() => _getControl());
        if (control == null) return;

        var prop = control.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (prop == null || !prop.CanRead) return;

        Action? unhook = null;
        if (control is INotifyPropertyChanged inpc)
        {
            PropertyChangedEventHandler handler = (s, e) =>
            {
                if (e.PropertyName == propertyName)
                    _ = ReadAndEmitAsync(propertyName, prop, control);
            };
            inpc.PropertyChanged += handler;
            unhook = () => inpc.PropertyChanged -= handler;
        }

        lock (_gate)
        {
            // If a previous subscription exists, unhook first to avoid double-fires.
            if (_active.TryGetValue(propertyName, out var existing))
                existing.Unhook?.Invoke();
            _active[propertyName] = new ActiveSub(unhook);
        }

        // Emit once on subscribe so the host's cache starts populated.
        await ReadAndEmitAsync(propertyName, prop, control);
    }

    public Task UnsubscribeAsync(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return Task.CompletedTask;
        lock (_gate)
        {
            if (_active.TryGetValue(propertyName, out var sub))
            {
                sub.Unhook?.Invoke();
                _active.Remove(propertyName);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>Drop every subscription — called when the loaded UserControl is unloaded/replaced.</summary>
    public void ClearForControlUnload()
    {
        lock (_gate)
        {
            foreach (var sub in _active.Values)
                sub.Unhook?.Invoke();
            _active.Clear();
        }
    }

    private async Task ReadAndEmitAsync(string propertyName, PropertyInfo prop, object control)
    {
        try
        {
            var value = await Dispatcher.UIThread.InvokeAsync(() => prop.GetValue(control));
            var json = value == null ? "null" : JsonSerializer.Serialize(value, value.GetType(), Json.Options);
            await _emit(propertyName, json);
        }
        catch
        {
            // Silently swallow — observable emission must never crash the IPC loop.
            // Read errors will surface next time Claude calls read_observable directly.
        }
    }

    private sealed record ActiveSub(Action? Unhook);
}
