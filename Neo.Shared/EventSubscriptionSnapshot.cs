using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace Neo.Shared
{
    /// <summary>
    /// A UI toolkit's routed-event handlers (WPF and Avalonia keep them in their own stores, not in
    /// delegate fields): which ones an element has, and how to take one exact subscription away.
    /// </summary>
    public interface IRoutedEventHandlers
    {
        /// <summary>(routed event, handler) for every handler attached to <paramref name="element"/>, oldest first; empty for non-elements.</summary>
        IReadOnlyList<(object RoutedEvent, Delegate Handler)> Read(object element);

        /// <summary>Removes the subscription of this very delegate — one subscription, even when others are equal to it.</summary>
        void Remove(object element, object routedEvent, Delegate handler);

        /// <summary>Long-lived elements an app reaches without a field of its own — the window it is shown in.</summary>
        IEnumerable<object> Hosts(object root);
    }

    /// <summary>
    /// Rebuilding the UI runs its subscriptions again. For the controls it creates anew that is
    /// right, their old versions are gone with their handlers — but a subscription to something
    /// that outlives the rebuild (a collection or a timer in a field, the control itself, its
    /// window, a static event) would afterwards fire twice: a timer ticking at double speed, an
    /// "add" that adds two items.
    ///
    /// <para>Captured before the rebuild, <see cref="RemoveReplaced"/> pairs every subscription the
    /// rebuild made with the subscription it replaces — same handler method, or a lambda from the
    /// same method when Roslyn had to give the lambda a new body — and removes the old one. The
    /// new one stays: it is bound to the new UI. Only the app's own handlers are touched, never
    /// the toolkit's (bindings, weak event managers).</para>
    /// </summary>
    public sealed class EventSubscriptionSnapshot
    {
        private const BindingFlags InstanceMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        private const BindingFlags StaticMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
        private const int MaxObjects = 20_000;

        private static readonly ConcurrentDictionary<Type, FieldInfo[]> s_eventFields = new();

        private readonly Assembly _plugin;
        private readonly IRoutedEventHandlers? _routed;
        private readonly List<(object? Target, FieldInfo Field, Delegate[] Before)> _clr = [];
        private readonly List<(object Element, object RoutedEvent, Delegate[] Before)> _routedBefore = [];

        private EventSubscriptionSnapshot(Assembly plugin, IRoutedEventHandlers? routed)
        {
            _plugin = plugin;
            _routed = routed;
        }

        public static EventSubscriptionSnapshot Capture(object root, IRoutedEventHandlers? routed)
        {
            var snapshot = new EventSubscriptionSnapshot(root.GetType().Assembly, routed);
            try
            {
                foreach (var target in snapshot.LongLivedObjects(root))
                    snapshot.CaptureInstance(target);
                snapshot.CaptureStatics();
            }
            catch { /* best effort: whatever was captured is still consistent */ }
            return snapshot;
        }

        /// <summary>Call after the rebuild. Returns how many old subscriptions were removed.</summary>
        public int RemoveReplaced()
        {
            int removed = 0;

            foreach (var (target, field, before) in _clr)
            {
                try
                {
                    var after = (field.GetValue(target) as Delegate)?.GetInvocationList() ?? [];
                    foreach (var old in Replaced(before, after))
                    {
                        // What a field-like event's remove accessor does.
                        field.SetValue(target, Delegate.Remove(field.GetValue(target) as Delegate, old));
                        removed++;
                    }
                }
                catch { /* this event stays as it is */ }
            }

            if (_routed != null)
            {
                foreach (var (element, routedEvent, before) in _routedBefore)
                {
                    try
                    {
                        var after = _routed.Read(element).Where(h => Equals(h.RoutedEvent, routedEvent)).Select(h => h.Handler).ToArray();
                        foreach (var old in Replaced(before, after))
                        {
                            _routed.Remove(element, routedEvent, old);
                            removed++;
                        }
                    }
                    catch { /* this event stays as it is */ }
                }
            }

            return removed;
        }

        // Old subscriptions (still attached) that a subscription made during the rebuild replaces.
        private List<Delegate> Replaced(Delegate[] before, Delegate[] after)
        {
            var result = new List<Delegate>();
            if (before.Length == 0) return result;

            var added = after.Where(d => !before.Contains(d, ReferenceEqualityComparer.Instance) && IsOwn(d)).ToList();
            if (added.Count == 0) return result;

            var survivors = before.Where(d => after.Contains(d, ReferenceEqualityComparer.Instance) && IsOwn(d)).ToList();
            foreach (var fresh in added)
            {
                var old = survivors.FirstOrDefault(d => d.Method == fresh.Method)
                    ?? (LambdaOwner(fresh.Method) is { } owner
                        ? survivors.FirstOrDefault(d => LambdaOwner(d.Method) == owner && OuterType(d.Method) == OuterType(fresh.Method))
                        : null);
                if (old == null) continue;
                survivors.Remove(old);
                result.Add(old);
            }
            return result;
        }

        private bool IsOwn(Delegate handler) => handler.Method.DeclaringType?.Assembly == _plugin;

        // The class a lambda was written in (its closure classes are nested in it).
        private static Type? OuterType(MethodInfo method)
        {
            var type = method.DeclaringType;
            while (type?.DeclaringType != null) type = type.DeclaringType;
            return type;
        }

        /// <summary>"BuildUi" for the lambdas and local functions the compiler generated from BuildUi's body.</summary>
        private static string? LambdaOwner(MethodInfo method)
        {
            var name = method.Name;
            if (name.Length < 4 || name[0] != '<') return null;
            int close = name.IndexOf('>');
            if (close <= 1 || close + 3 > name.Length) return null;
            var kind = name.AsSpan(close + 1, 3);
            return kind is "b__" or "g__" ? name[1..close] : null;
        }

        private void CaptureInstance(object target)
        {
            foreach (var field in EventFields(target.GetType()))
            {
                if (field.GetValue(target) is Delegate current)
                    _clr.Add((target, field, current.GetInvocationList()));
            }

            if (_routed == null) return;
            foreach (var group in _routed.Read(target).GroupBy(h => h.RoutedEvent))
                _routedBefore.Add((target, group.Key, group.Select(h => h.Handler).ToArray()));
        }

        private void CaptureStatics()
        {
            Type[] types;
            try { types = _plugin.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

            foreach (var type in types)
            {
                if (type.ContainsGenericParameters) continue;
                foreach (var ev in type.GetEvents(StaticMembers))
                {
                    if (BackingField(type, ev, StaticMembers) is { } field && field.GetValue(null) is Delegate current)
                        _clr.Add((null, field, current.GetInvocationList()));
                }
            }
        }

        /// <summary>
        /// The root, what the app's objects hold in their fields (collections, timers, controls,
        /// view models) and the elements of those collections, the plugin's static fields and the
        /// window. Only the app's own objects are walked into.
        /// </summary>
        private List<object> LongLivedObjects(object root)
        {
            var result = new List<object>();
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var pending = new Queue<object>();

            pending.Enqueue(root);
            foreach (var type in SafeTypes())
            {
                if (type.ContainsGenericParameters) continue;
                foreach (var field in type.GetFields(StaticMembers))
                {
                    if (field.IsLiteral || !IsReference(field.FieldType)) continue;
                    try { if (field.GetValue(null) is { } value) pending.Enqueue(value); } catch { }
                }
            }
            if (_routed != null)
                foreach (var host in _routed.Hosts(root)) pending.Enqueue(host);

            while (pending.Count > 0 && visited.Count < MaxObjects)
            {
                var current = pending.Dequeue();
                if (current is string || current is Delegate || current.GetType().IsValueType || !visited.Add(current)) continue;
                result.Add(current);

                var type = current.GetType();
                if (type.Assembly == _plugin)
                {
                    for (var t = type; t != null && t.Assembly == _plugin; t = t.BaseType)
                    {
                        foreach (var field in t.GetFields(InstanceMembers))
                        {
                            if (!IsReference(field.FieldType)) continue;
                            try { if (field.GetValue(current) is { } value) pending.Enqueue(value); } catch { }
                        }
                    }
                }
                else if (current is IDictionary dictionary)
                {
                    try
                    {
                        foreach (DictionaryEntry entry in dictionary)
                            if (entry.Value != null) pending.Enqueue(entry.Value);
                    }
                    catch { /* changed while we looked */ }
                }
                else if (current is ICollection collection)
                {
                    try
                    {
                        foreach (var item in collection)
                            if (item != null) pending.Enqueue(item);
                    }
                    catch { /* changed while we looked */ }
                }
            }
            return result;
        }

        private IEnumerable<Type> SafeTypes()
        {
            try { return _plugin.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
        }

        private static bool IsReference(Type type) => !type.IsPrimitive && !type.IsPointer && type != typeof(string);

        /// <summary>The delegate fields behind the type's events (field-like events, or custom ones with a single field of the handler type).</summary>
        private static FieldInfo[] EventFields(Type type) => s_eventFields.GetOrAdd(type, static t =>
        {
            var fields = new List<FieldInfo>();
            for (var current = t; current != null && current != typeof(object); current = current.BaseType)
            {
                foreach (var ev in current.GetEvents(InstanceMembers))
                {
                    if (BackingField(current, ev, InstanceMembers) is { } field && !fields.Contains(field))
                        fields.Add(field);
                }
            }
            return fields.ToArray();
        });

        private static FieldInfo? BackingField(Type declaringType, EventInfo ev, BindingFlags flags)
        {
            var handlerType = ev.EventHandlerType;
            if (handlerType == null) return null;

            var named = declaringType.GetField(ev.Name, flags);
            if (named != null && named.FieldType == handlerType) return named;

            // Explicit interface events ("INotifyPropertyChanged.PropertyChanged") and events with
            // hand-written accessors: the one field of the handler type, if there is exactly one.
            var candidates = declaringType.GetFields(flags).Where(f => f.FieldType == handlerType).ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }
    }
}
