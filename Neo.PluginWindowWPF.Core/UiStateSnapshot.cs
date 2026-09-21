using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Neo.PluginWindowWPF
{
    /// <summary>
    /// What the user did to the UI that is not in the app's fields: text typed into a box, the
    /// selected row, the scroll position, the open tab, the focus. A hot reload that rebuilds the
    /// visual tree takes a snapshot of the old tree and puts these values back into the new one.
    ///
    /// <para>Controls are matched by <c>Name</c> when they have one, otherwise by their position in
    /// the tree (type and index among siblings of the same type, all the way up). A control that
    /// cannot be found again, or that changed its type, is simply left as the new code built it.
    /// Values are only written when they differ, so change handlers do not fire for nothing.</para>
    /// </summary>
    internal sealed class UiStateSnapshot
    {
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private string? _focusedKey;

        private sealed record Entry(Type ControlType, Dictionary<string, object?> Values);

        public int Count => _entries.Count;

        public static UiStateSnapshot Capture(FrameworkElement root)
        {
            var snapshot = new UiStateSnapshot();
            var focused = Keyboard.FocusedElement as DependencyObject;

            foreach (var element in Descendants(root))
            {
                var values = Read(element);
                if (values == null && !ReferenceEquals(element, focused)) continue;

                var key = KeyOf(element, root);
                if (key == null) continue;
                if (values != null) snapshot._entries[key] = new Entry(element.GetType(), values);
                if (ReferenceEquals(element, focused)) snapshot._focusedKey = key;
            }
            return snapshot;
        }

        /// <summary>Returns how many controls got a value back.</summary>
        public int Restore(FrameworkElement root)
        {
            // Templates are applied during layout; without it the new tree has no scroll viewers yet.
            try { root.UpdateLayout(); } catch { /* not attached */ }

            int restored = 0;
            FrameworkElement? focus = null;
            foreach (var element in new List<FrameworkElement>(Descendants(root)))
            {
                var key = KeyOf(element, root);
                if (key == null) continue;
                if (key == _focusedKey) focus = element;
                if (_entries.TryGetValue(key, out var entry) && entry.ControlType == element.GetType())
                {
                    try { if (Write(element, entry.Values)) restored++; }
                    catch { /* a value the new control does not accept */ }
                }
            }

            try { focus?.Focus(); } catch { /* not focusable any more */ }
            return restored;
        }

        private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
        {
            var pending = new Stack<DependencyObject>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                int count = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(current) : 0;
                for (int i = count - 1; i >= 0; i--)
                {
                    var child = VisualTreeHelper.GetChild(current, i);
                    if (child is FrameworkElement element) yield return element;
                    pending.Push(child);
                }
            }
        }

        private static string? KeyOf(FrameworkElement element, FrameworkElement root)
        {
            if (!string.IsNullOrEmpty(element.Name) && element.TemplatedParent == null)
                return "#" + element.Name;

            var segments = new List<string>();
            DependencyObject? current = element;
            while (current != null && !ReferenceEquals(current, root))
            {
                var parent = VisualTreeHelper.GetParent(current);
                if (parent == null) return null; // not under root
                var type = current.GetType();
                int index = 0;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var sibling = VisualTreeHelper.GetChild(parent, i);
                    if (ReferenceEquals(sibling, current)) break;
                    if (sibling.GetType() == type) index++;
                }
                segments.Add($"{type.Name}[{index}]");
                current = parent;
            }
            segments.Reverse();
            return string.Join("/", segments);
        }

        private static Dictionary<string, object?>? Read(FrameworkElement element) => element switch
        {
            TextBox t => new() { ["Text"] = t.Text, ["CaretIndex"] = t.CaretIndex, ["SelectionStart"] = t.SelectionStart, ["SelectionLength"] = t.SelectionLength },
            PasswordBox => null, // never copy secrets around
            ToggleButton t => new() { ["IsChecked"] = t.IsChecked },
            Slider s => new() { ["Value"] = s.Value },
            Selector s => new() { ["SelectedIndex"] = s.SelectedIndex },
            Expander e => new() { ["IsExpanded"] = e.IsExpanded },
            ScrollViewer s => s.VerticalOffset == 0 && s.HorizontalOffset == 0 ? null
                : new() { ["VerticalOffset"] = s.VerticalOffset, ["HorizontalOffset"] = s.HorizontalOffset },
            DatePicker d => new() { ["SelectedDate"] = d.SelectedDate },
            _ => null,
        };

        private static bool Write(FrameworkElement element, Dictionary<string, object?> values)
        {
            switch (element)
            {
                case TextBox t:
                    bool changed = false;
                    if (!string.Equals(t.Text, (string?)values["Text"], StringComparison.Ordinal)) { t.Text = (string?)values["Text"] ?? ""; changed = true; }
                    t.Select((int)values["SelectionStart"]!, (int)values["SelectionLength"]!);
                    t.CaretIndex = (int)values["CaretIndex"]!;
                    return changed;
                case ToggleButton t:
                    return Set(t.IsChecked, (bool?)values["IsChecked"], v => t.IsChecked = v);
                case Slider s:
                    return Set(s.Value, (double)values["Value"]!, v => s.Value = v);
                case Selector s:
                    var index = (int)values["SelectedIndex"]!;
                    return index < s.Items.Count && Set(s.SelectedIndex, index, v => s.SelectedIndex = v);
                case Expander e:
                    return Set(e.IsExpanded, (bool)values["IsExpanded"]!, v => e.IsExpanded = v);
                case ScrollViewer s:
                    s.ScrollToVerticalOffset((double)values["VerticalOffset"]!);
                    s.ScrollToHorizontalOffset((double)values["HorizontalOffset"]!);
                    return true;
                case DatePicker d:
                    return Set(d.SelectedDate, (DateTime?)values["SelectedDate"], v => d.SelectedDate = v);
            }
            return false;
        }

        private static bool Set<T>(T current, T wanted, Action<T> assign)
        {
            if (EqualityComparer<T>.Default.Equals(current, wanted)) return false;
            assign(wanted);
            return true;
        }
    }
}
