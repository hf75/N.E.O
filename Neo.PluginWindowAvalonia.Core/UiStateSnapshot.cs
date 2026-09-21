using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Neo.PluginWindowAvalonia
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
        private List<string> _focusedKeys = [];

        private sealed record Entry(Type ControlType, Dictionary<string, object?> Values);

        /// <summary>How many controls had a value worth carrying over.</summary>
        public int Count { get; private set; }

        public static UiStateSnapshot Capture(Control root)
        {
            var snapshot = new UiStateSnapshot();
            var focused = TopLevel.GetTopLevel(root)?.FocusManager?.GetFocusedElement() as Control;

            foreach (var control in Descendants(root))
            {
                var values = Read(control);
                if (values == null && !ReferenceEquals(control, focused)) continue;

                var keys = KeysOf(control, root);
                if (keys.Count == 0) continue;
                if (values != null)
                {
                    var entry = new Entry(control.GetType(), values);
                    foreach (var key in keys) snapshot._entries[key] = entry;
                    snapshot.Count++;
                }
                if (ReferenceEquals(control, focused)) snapshot._focusedKeys = keys;
            }
            return snapshot;
        }

        /// <summary>Returns how many controls got a value back.</summary>
        public int Restore(Control root)
        {
            var restored = new HashSet<Control>(ReferenceEqualityComparer.Instance);
            Control? focus = null;
            for (int pass = 0; pass < MaxPasses; pass++)
            {
                // Templates are applied and containers realized during layout.
                try { root.UpdateLayout(); } catch { /* not attached */ }
                if (!RestorePass(root, restored, ref focus)) break;
            }

            try { focus?.Focus(); } catch { /* not focusable any more */ }
            return restored.Count;
        }

        private bool RestorePass(Control root, HashSet<Control> restored, ref Control? focus)
        {
            bool changed = false;
            foreach (var control in Descendants(root).ToList())
            {
                var keys = KeysOf(control, root);
                if (keys.Count == 0) continue;
                if (focus == null && keys.Any(_focusedKeys.Contains)) focus = control;

                // Name first, then position.
                var entry = keys.Select(k => _entries.TryGetValue(k, out var e) ? e : null)
                                .FirstOrDefault(e => e != null && e.ControlType == control.GetType());
                if (entry == null) continue;
                try
                {
                    if (!Write(control, entry.Values)) continue;
                    changed = true;
                    restored.Add(control);
                }
                catch { /* a value the new control does not accept */ }
            }
            return changed;
        }

        private static IEnumerable<Control> Descendants(Control root)
            => root.GetVisualDescendants().OfType<Control>();

        /// <summary>"#name" (own names only — template parts repeat) and the position path; either may be missing.</summary>
        private static List<string> KeysOf(Control control, Control root)
        {
            var keys = new List<string>(2);
            if (!string.IsNullOrEmpty(control.Name) && control.TemplatedParent == null)
                keys.Add("#" + control.Name);

            var segments = new List<string>();
            Visual? current = control;
            while (current != null && !ReferenceEquals(current, root))
            {
                var parent = current.GetVisualParent();
                if (parent == null) return keys; // not under root
                var type = current.GetType();
                int index = 0;
                foreach (var sibling in parent.GetVisualChildren())
                {
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

        private static Dictionary<string, object?>? Read(Control control) => control switch
        {
            TextBox t => new() { ["Text"] = t.Text, ["CaretIndex"] = t.CaretIndex, ["SelectionStart"] = t.SelectionStart, ["SelectionEnd"] = t.SelectionEnd },
            AutoCompleteBox a => new() { ["Text"] = a.Text },
            ToggleButton t => new() { ["IsChecked"] = t.IsChecked },
            Slider s => new() { ["Value"] = s.Value },
            NumericUpDown n => new() { ["Value"] = n.Value },
            ListBox l => l.SelectionMode.HasFlag(SelectionMode.Multiple)
                ? Selection(l.SelectedIndex, l.SelectedItem, l.SelectedItems, l.Selection.SelectedIndexes)
                : Selection(l.SelectedIndex, l.SelectedItem, null, null),
            SelectingItemsControl s => Selection(s.SelectedIndex, s.SelectedItem, null, null),
            TreeViewItem t => new() { ["IsExpanded"] = t.IsExpanded, ["IsSelected"] = t.IsSelected },
            Expander e => new() { ["IsExpanded"] = e.IsExpanded },
            SplitView s => new() { ["IsPaneOpen"] = s.IsPaneOpen },
            ScrollViewer s => s.Offset == default ? null : new() { ["Offset"] = s.Offset },
            CalendarDatePicker d => new() { ["SelectedDate"] = d.SelectedDate },
            DatePicker d => new() { ["SelectedDate"] = d.SelectedDate },
            TimePicker t => new() { ["SelectedTime"] = t.SelectedTime },
            Calendar c => new() { ["SelectedDate"] = c.SelectedDate },
            _ => ReadReflectedSelection(control),
        };

        private static bool Write(Control control, Dictionary<string, object?> values)
        {
            switch (control)
            {
                case TextBox t:
                    bool changed = false;
                    if (!string.Equals(t.Text, (string?)values["Text"], StringComparison.Ordinal)) { t.Text = (string?)values["Text"]; changed = true; }
                    t.SelectionStart = (int)values["SelectionStart"]!;
                    t.SelectionEnd = (int)values["SelectionEnd"]!;
                    t.CaretIndex = (int)values["CaretIndex"]!;
                    return changed;
                case AutoCompleteBox a:
                    return Set(a.Text, (string?)values["Text"], v => a.Text = v);
                case ToggleButton t:
                    return Set(t.IsChecked, (bool?)values["IsChecked"], v => t.IsChecked = v);
                case Slider s:
                    return Set(s.Value, (double)values["Value"]!, v => s.Value = v);
                case NumericUpDown n:
                    return Set(n.Value, (decimal?)values["Value"], v => n.Value = v);
                case ListBox l when values.ContainsKey("SelectedItems") && l.SelectionMode.HasFlag(SelectionMode.Multiple):
                    return RestoreMultiple(l, values);
                case SelectingItemsControl s:
                    return RestoreSelection(s.Items, s.ItemCount, s.SelectedIndex, v => s.SelectedIndex = v, values);
                case TreeViewItem t:
                    // Expanded first: its children appear with the next pass and get their own state.
                    bool expanded = Set(t.IsExpanded, (bool)values["IsExpanded"]!, v => t.IsExpanded = v);
                    return Set(t.IsSelected, (bool)values["IsSelected"]!, v => t.IsSelected = v) | expanded;
                case Expander e:
                    return Set(e.IsExpanded, (bool)values["IsExpanded"]!, v => e.IsExpanded = v);
                case SplitView s:
                    return Set(s.IsPaneOpen, (bool)values["IsPaneOpen"]!, v => s.IsPaneOpen = v);
                case ScrollViewer s:
                    return Set(s.Offset, (Vector)values["Offset"]!, v => s.Offset = v);
                case CalendarDatePicker d:
                    return Set(d.SelectedDate, (DateTime?)values["SelectedDate"], v => d.SelectedDate = v);
                case DatePicker d:
                    return Set(d.SelectedDate, (DateTimeOffset?)values["SelectedDate"], v => d.SelectedDate = v);
                case TimePicker t:
                    return Set(t.SelectedTime, (TimeSpan?)values["SelectedTime"], v => t.SelectedTime = v);
                case Calendar c:
                    return Set(c.SelectedDate, (DateTime?)values["SelectedDate"], v => c.SelectedDate = v);
            }
            return WriteReflectedSelection(control, values);
        }

        // -- selection ------------------------------------------------------------------------

        private static Dictionary<string, object?> Selection(int index, object? item, IList? items, IReadOnlyList<int>? indexes)
        {
            var values = new Dictionary<string, object?> { ["SelectedIndex"] = index, ["SelectedItem"] = item };
            if (items is { Count: > 1 })
            {
                values["SelectedItems"] = items.Cast<object?>().ToList();
                values["SelectedIndexes"] = indexes?.ToList() ?? [];
            }
            return values;
        }

        /// <summary>The selected object if the new items still hold it, else the old position.</summary>
        private static bool RestoreSelection(IList items, int count, int current, Action<int> select, Dictionary<string, object?> values)
        {
            int index = IndexOf(items, values["SelectedItem"]);
            if (index < 0) index = (int)values["SelectedIndex"]!;
            return index < count && Set(current, index, select);
        }

        private static bool RestoreMultiple(ListBox list, Dictionary<string, object?> values)
        {
            var wanted = ((List<object?>)values["SelectedItems"]!).Select(item => IndexOf(list.Items, item)).Where(i => i >= 0).ToList();
            if (wanted.Count == 0)
                wanted = ((List<int>)values["SelectedIndexes"]!).Where(i => i < list.ItemCount).ToList();
            if (wanted.Count == 0 || list.Selection.SelectedIndexes.OrderBy(i => i).SequenceEqual(wanted.OrderBy(i => i)))
                return false;

            list.Selection.Clear();
            foreach (var index in wanted) list.Selection.Select(index);
            return true;
        }

        // Only the app's data can be found again; controls used as items were rebuilt.
        private static int IndexOf(IEnumerable items, object? item)
        {
            if (item == null || item is Control) return -1;
            if (items is IList list) return list.IndexOf(item);
            int i = 0;
            foreach (var candidate in items)
            {
                if (Equals(candidate, item)) return i;
                i++;
            }
            return -1;
        }

        // Selectors outside the core controls - DataGrid, third-party grids and lists - that have
        // public SelectedIndex and SelectedItem properties.
        private static (PropertyInfo? Index, PropertyInfo? Item) Selector(Type type) => s_selectors.GetOrAdd(type, static t =>
        {
            var index = t.GetProperty("SelectedIndex", BindingFlags.Public | BindingFlags.Instance);
            var item = t.GetProperty("SelectedItem", BindingFlags.Public | BindingFlags.Instance);
            return index is { CanRead: true, CanWrite: true } && index.PropertyType == typeof(int) &&
                   item is { CanRead: true } && item.PropertyType == typeof(object)
                ? (index, item) : (null, null);
        });

        private static Dictionary<string, object?>? ReadReflectedSelection(Control control)
        {
            var (index, item) = Selector(control.GetType());
            if (index == null || item == null) return null;
            try { return Selection((int)index.GetValue(control)!, item.GetValue(control), null, null); }
            catch { return null; }
        }

        private static bool WriteReflectedSelection(Control control, Dictionary<string, object?> values)
        {
            var (index, item) = Selector(control.GetType());
            if (index == null || item == null) return false;

            var items = control.GetType().GetProperty("ItemsSource", BindingFlags.Public | BindingFlags.Instance)?.GetValue(control) as IEnumerable;
            int wanted = items != null ? IndexOf(items, values["SelectedItem"]) : -1;
            if (wanted < 0) wanted = (int)values["SelectedIndex"]!;
            if (wanted < 0) return false;
            return Set((int)index.GetValue(control)!, wanted, v => index.SetValue(control, v));
        }

        private static bool Set<T>(T current, T wanted, Action<T> assign)
        {
            if (EqualityComparer<T>.Default.Equals(current, wanted)) return false;
            assign(wanted);
            return true;
        }
    }
}
