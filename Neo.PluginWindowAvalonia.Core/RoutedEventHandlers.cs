using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Interactivity;

using Neo.Shared;

namespace Neo.PluginWindowAvalonia
{
    /// <summary>
    /// Avalonia's routed-event handlers for <see cref="EventSubscriptionSnapshot"/>. Avalonia has no
    /// public way to list them; they live in <c>Interactive._eventHandlers</c>. When that field is
    /// not there (another Avalonia version), routed events are simply left alone.
    /// </summary>
    internal sealed class RoutedEventHandlers : IRoutedEventHandlers
    {
        public static RoutedEventHandlers Instance { get; } = new();

        private static readonly FieldInfo? s_handlers =
            typeof(Interactive).GetField("_eventHandlers", BindingFlags.Instance | BindingFlags.NonPublic);

        public IReadOnlyList<(object RoutedEvent, Delegate Handler)> Read(object element)
            => Subscriptions(element).Select(s => ((object)s.Event, s.Handler)).ToList();

        public void Remove(object element, object routedEvent, Delegate handler)
        {
            if (element is not Interactive interactive || routedEvent is not RoutedEvent re) return;

            // Avalonia's RemoveHandler takes away every subscription equal to the handler (same
            // target and method), so the equal ones that are not this very delegate go back on.
            var putBack = Subscriptions(element)
                .Where(s => s.Event == re && s.Handler.Equals(handler) && !ReferenceEquals(s.Handler, handler))
                .ToList();
            interactive.RemoveHandler(re, handler);
            foreach (var s in putBack)
                interactive.AddHandler(re, s.Handler, s.Routes, s.HandledEventsToo);
        }

        public IEnumerable<object> Hosts(object root)
        {
            if (root is Avalonia.Visual visual && TopLevel.GetTopLevel(visual) is { } topLevel)
                yield return topLevel;
        }

        private static List<(RoutedEvent Event, Delegate Handler, RoutingStrategies Routes, bool HandledEventsToo)> Subscriptions(object element)
        {
            var result = new List<(RoutedEvent, Delegate, RoutingStrategies, bool)>();
            if (element is not Interactive || s_handlers?.GetValue(element) is not IDictionary map) return result;

            foreach (DictionaryEntry entry in map)
            {
                if (entry.Key is not RoutedEvent re || entry.Value is not IList subscriptions) continue;
                foreach (var subscription in subscriptions)
                {
                    if (subscription == null) continue;
                    var type = subscription.GetType();
                    if (type.GetProperty("Handler")?.GetValue(subscription) is not Delegate handler) continue;
                    var routes = type.GetProperty("Routes")?.GetValue(subscription) is RoutingStrategies r ? r : RoutingStrategies.Direct | RoutingStrategies.Bubble;
                    var handledToo = type.GetProperty("HandledEventsToo")?.GetValue(subscription) is true;
                    result.Add((re, handler, routes, handledToo));
                }
            }
            return result;
        }
    }
}
