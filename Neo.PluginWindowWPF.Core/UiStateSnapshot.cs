using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    /// <para>Every control is remembered under its position in the tree (type and index among
    /// siblings of the same type, all the way up) and, when it has one, under its <c>Name</c>. The
    /// name wins when both versions have it; the position covers controls that gained or lost a name
    /// in between (the next version names what it wants to address). A control that cannot be found
    /// again, or that changed its type, is left as the new code built it. Values are only written
    /// when they differ, so change handlers do not fire for nothing.</para>
    ///
    /// <para>A selection is carried over as the selected object when the new list still holds it —
    /// lists usually show the app's own data, which survives the rebuild even when the new code
    /// sorts it differently — and as the position otherwise.</para>
    /// </summary>
    internal sealed class UiStateSnapshot
    {
        // Expanding a tree node or filling a list creates the controls below it only with the next
        // layout pass; each pass restores what has appeared since the previous one.
        private const int MaxPasses = 6;

        private static readonly ConcurrentDictionary<Type, (PropertyInfo? Index, PropertyInfo? Item)> s_selectors = new();

        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private List<string> _focusedKeys = new();

        private sealed record Entry(Type ControlType, Dictionary<string, object?> Values);

        /// <summary>How many controls had a value worth carrying over.</summary>
        public int Count { get; private set; }

        public static UiStateSnapshot Capture(FrameworkElement root)
        {
            var snapshot = new UiStateSnapshot();
            var focused = Keyboard.FocusedElement as DependencyObject;

            foreach (var element in Descendants(root))
            {
                var values = Read(element);
                if (values == null && !ReferenceEquals(element, focused)) continue;

                var keys = KeysOf(element, root);
                if (keys.Count == 0) continue;
                if (values != null)
                {
                    var entry = new Entry(element.GetType(), values);
                    foreach (var key in keys) snapshot._entries[key] = entry;
                    snapshot.Count++;
                }
                if (ReferenceEquals(element, focused)) snapshot._focusedKeys = keys;
            }
            return snapshot;
        }

        /// <summary>Returns how many controls got a value back.</summary>
        public int Restore(FrameworkElement root)
        {
            var restored = new HashSet<FrameworkElement>(ReferenceEqualityComparer.Instance);
            FrameworkElement? focus = null;
            for (int pass = 0; pass < MaxPasses; pass++)
            {
                // Templates are applied and containers generated during layout.
                try { root.UpdateLayout(); } catch { /* not attached */ }
                if (!RestorePass(root, restored, ref focus)) break;
            }

            try { focus?.Focus(); } catch { /* not focusable any more */ }
            return restored.Count;
        }

        private bool RestorePass(FrameworkElement root, HashSet<FrameworkElement> restored, ref FrameworkElement? focus)
        {
            bool changed = false;
            foreach (var element in new List<FrameworkElement>(Descendants(root)))
            {
                var keys = KeysOf(element, root);
                if (keys.Count == 0) continue;
                if (focus == null && keys.Exists(_focusedKeys.Contains)) focus = element;

                // Name first, then position.
                Entry? entry = null;
                foreach (var key in keys)
                {
                    if (_entries.TryGetValue(key, out var candidate) && candidate.ControlType == element.GetType())
                    {
                        entry = candidate;
                        break;
                    }
                }
                if (entry == null) continue;
                try
                {
                    if (!Write(element, entry.Values)) continue;
                    changed = true;
                    restored.Add(element);
                }
                catch { /* a value the new control does not accept */ }
            }
            return changed;
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

        /// <summary>"#name" (own names only — template parts repeat) and the position path; either may be missing.</summary>
        private static List<string> KeysOf(FrameworkElement element, FrameworkElement root)
        {
            var keys = new List<string>(2);
            if (!string.IsNullOrEmpty(element.Name) && element.TemplatedParent == null)
                keys.Add("#" + element.Name);

            var segments = new List<string>();
            DependencyObject? current = element;
            while (current != null && !ReferenceEquals(current, root))
            {
                var parent = VisualTreeHelper.GetParent(current);
                if (parent == null) return keys; // not under root
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
            keys.Add(string.Join("/", segments));
            return keys;
        }

        private static Dictionary<string, object?>? Read(FrameworkElement element) => element switch
        {
            TextBox t => new() { ["Text"] = t.Text, ["CaretIndex"] = t.CaretIndex, ["SelectionStart"] = t.SelectionStart, ["SelectionLength"] = t.SelectionLength },
            PasswordBox => null, // never copy secrets around
            ToggleButton t => new() { ["IsChecked"] = t.IsChecked },
            Slider s => new() { ["Value"] = s.Value },
            ComboBox { IsEditable: true } c => WithText(Selection(c, null), c.Text),
            ListBox l when l.SelectionMode != SelectionMode.Single => Selection(l, l.SelectedItems),
            MultiSelector m => Selection(m, m.SelectedItems), // DataGrid
            Selector s => Selection(s, null),
            TreeViewItem t => new() { ["IsExpanded"] = t.IsExpanded, ["IsSelected"] = t.IsSelected },
            Expander e => new() { ["IsExpanded"] = e.IsExpanded },
            ScrollViewer s => s.VerticalOffset == 0 && s.HorizontalOffset == 0 ? null
                : new() { ["VerticalOffset"] = s.VerticalOffset, ["HorizontalOffset"] = s.HorizontalOffset },
            DatePicker d => new() { ["SelectedDate"] = d.SelectedDate },
            Calendar c => new() { ["SelectedDate"] = c.SelectedDate },
            _ => ReadReflectedSelection(element),
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
                case ComboBox { IsEditable: true } c when values.ContainsKey("Text"):
                    bool selected = RestoreSelection(c, values);
                    return Set(c.Text, (string?)values["Text"], v => c.Text = v ?? "") | selected;
                case ListBox l when values.ContainsKey("SelectedItems") && l.SelectionMode != SelectionMode.Single:
                    return RestoreMultiple(l.SelectedItems, l.Items, values);
                case MultiSelector m when values.ContainsKey("SelectedItems"):
                    return RestoreMultiple(m.SelectedItems, m.Items, values);
                case Selector s:
                    return RestoreSelection(s, values);
                case TreeViewItem t:
                    // Expanded first: its children appear with the next pass and get their own state.
                    bool expanded = Set(t.IsExpanded, (bool)values["IsExpanded"]!, v => t.IsExpanded = v);
                    return Set(t.IsSelected, (bool)values["IsSelected"]!, v => t.IsSelected = v) | expanded;
                case Expander e:
                    return Set(e.IsExpanded, (bool)values["IsExpanded"]!, v => e.IsExpanded = v);
                case ScrollViewer s:
                    // Offsets are applied with the next layout pass; the extent may still be growing.
                    var vertical = (double)values["VerticalOffset"]!;
                    var horizontal = (double)values["HorizontalOffset"]!;
                    if (s.VerticalOffset == vertical && s.HorizontalOffset == horizontal) return false;
                    s.ScrollToVerticalOffset(vertical);
                    s.ScrollToHorizontalOffset(horizontal);
                    return true;
                case DatePicker d:
                    return Set(d.SelectedDate, (DateTime?)values["SelectedDate"], v => d.SelectedDate = v);
                case Calendar c:
                    return Set(c.SelectedDate, (DateTime?)values["SelectedDate"], v => c.SelectedDate = v);
            }
            return WriteReflectedSelection(element, values);
        }

        // -- selection ------------------------------------------------------------------------

        private static Dictionary<string, object?> Selection(Selector selector, IList? selectedItems)
        {
            var values = new Dictionary<string, object?> { ["SelectedIndex"] = selector.SelectedIndex, ["SelectedItem"] = selector.SelectedItem };
            if (selectedItems is { Count: > 1 })
            {
                var items = selectedItems.Cast<object?>().ToList();
                values["SelectedItems"] = items;
                values["SelectedIndexes"] = items.Select(i => selector.Items.IndexOf(i)).Where(i => i >= 0).ToList();
            }
            return values;
        }

        private static Dictionary<string, object?> WithText(Dictionary<string, object?> values, string? text)
        {
            values["Text"] = text;
            return values;
        }

        /// <summary>The selected object if the new items still hold it, else the old position.</summary>
        private static bool RestoreSelection(Selector selector, Dictionary<string, object?> values)
        {
            int index = IndexOf(selector.Items, values["SelectedItem"]);
            if (index < 0) index = (int)values["SelectedIndex"]!;
            return index < selector.Items.Count && Set(selector.SelectedIndex, index, v => selector.SelectedIndex = v);
        }

        private static bool RestoreMultiple(IList selected, ItemCollection items, Dictionary<string, object?> values)
        {
            var wanted = ((List<object?>)values["SelectedItems"]!).Where(item => IndexOf(items, item) >= 0).ToList();
            if (wanted.Count == 0)
                wanted = ((List<int>)values["SelectedIndexes"]!).Where(i => i < items.Count).Select(i => (object?)items[i]).ToList();
            if (wanted.Count == 0 || (selected.Count == wanted.Count && wanted.All(selected.Contains)))
                return false;

            selected.Clear();
            foreach (var item in wanted) selected.Add(item);
            return true;
        }

        // Only the app's data can be found again; controls used as items were rebuilt.
        private static int IndexOf(IEnumerable items, object? item)
        {
            if (item == null || item is DependencyObject) return -1;
            if (items is IList list) return list.IndexOf(item);
            int i = 0;
            foreach (var candidate in items)
            {
                if (Equals(candidate, item)) return i;
                i++;
            }
            return -1;
        }

        // Selectors outside WPF's own controls (third-party grids and lists) that have public
        // SelectedIndex and SelectedItem properties.
        private static (PropertyInfo? Index, PropertyInfo? Item) Selector(Type type) => s_selectors.GetOrAdd(type, static t =>
        {
            var index = t.GetProperty("SelectedIndex", BindingFlags.Public | BindingFlags.Instance);
            var item = t.GetProperty("SelectedItem", BindingFlags.Public | BindingFlags.Instance);
            return index is { CanRead: true, CanWrite: true } && index.PropertyType == typeof(int) &&
                   item is { CanRead: true } && item.PropertyType == typeof(object)
                ? (index, item) : (null, null);
        });

        private static Dictionary<string, object?>? ReadReflectedSelection(FrameworkElement element)
        {
            var (index, item) = Selector(element.GetType());
            if (index == null || item == null) return null;
            try { return new() { ["SelectedIndex"] = (int)index.GetValue(element)!, ["SelectedItem"] = item.GetValue(element) }; }
            catch { return null; }
        }

        private static bool WriteReflectedSelection(FrameworkElement element, Dictionary<string, object?> values)
        {
            var (index, item) = Selector(element.GetType());
            if (index == null || item == null) return false;

            var items = element.GetType().GetProperty("ItemsSource", BindingFlags.Public | BindingFlags.Instance)?.GetValue(element) as IEnumerable;
            int wanted = items != null ? IndexOf(items, values["SelectedItem"]) : -1;
            if (wanted < 0) wanted = (int)values["SelectedIndex"]!;
            if (wanted < 0) return false;
            return Set((int)index.GetValue(element)!, wanted, v => index.SetValue(element, v));
        }

        private static bool Set<T>(T current, T wanted, Action<T> assign)
        {
            if (EqualityComparer<T>.Default.Equals(current, wanted)) return false;
            assign(wanted);
            return true;
        }
    }
}
