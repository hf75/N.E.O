using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;

using Neo.Shared;

namespace Neo.PluginWindowWPF
{
    /// <summary>
    /// WPF's routed-event handlers for <see cref="EventSubscriptionSnapshot"/>. WPF keeps them in
    /// the element's internal <c>EventHandlersStore</c>; when that is not reachable, routed events
    /// are simply left alone.
    /// </summary>
    internal sealed class RoutedEventHandlers : IRoutedEventHandlers
    {
        public static RoutedEventHandlers Instance { get; } = new();

        private static readonly PropertyInfo? s_store =
            typeof(UIElement).GetProperty("EventHandlersStore", BindingFlags.Instance | BindingFlags.NonPublic);

        public IReadOnlyList<(object RoutedEvent, Delegate Handler)> Read(object element)
        {
            var result = new List<(object, Delegate)>();
            if (element is not UIElement ui || s_store?.GetValue(ui) is not { } store) return result;

            var get = store.GetType().GetMethod("GetRoutedEventHandlers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                [typeof(RoutedEvent)]);
            if (get == null) return result;

            foreach (var routedEvent in EventManager.GetRoutedEvents())
            {
                if (get.Invoke(store, [routedEvent]) is not RoutedEventHandlerInfo[] handlers) continue;
                foreach (var info in handlers)
                    result.Add((routedEvent, info.Handler));
            }
            return result;
        }

        // WPF's RemoveHandler takes away the first subscription equal to the handler. The old one is
        // older than its replacement, and equal subscriptions are interchangeable — either way the
        // right number stays.
        public void Remove(object element, object routedEvent, Delegate handler)
        {
            if (element is UIElement ui && routedEvent is RoutedEvent re)
                ui.RemoveHandler(re, handler);
        }

        public IEnumerable<object> Hosts(object root)
        {
            if (root is DependencyObject d && Window.GetWindow(d) is { } window)
                yield return window;
        }
    }
}
