using System;
using System.Collections.Generic;
using System.Linq;

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
    /// </summary>
    internal sealed class UiStateSnapshot
    {
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
            // Templates are applied during layout; without it the new tree has no scroll viewers yet.
            try { root.UpdateLayout(); } catch { /* not attached */ }

            int restored = 0;
            Control? focus = null;
            foreach (var control in Descendants(root).ToList())
            {
                var keys = KeysOf(control, root);
                if (keys.Count == 0) continue;
                if (focus == null && keys.Any(_focusedKeys.Contains)) focus = control;

                // Name first, then position.
                var entry = keys.Select(k => _entries.TryGetValue(k, out var e) ? e : null)
                                .FirstOrDefault(e => e != null && e.ControlType == control.GetType());
                if (entry == null) continue;
                try { if (Write(control, entry.Values)) restored++; }
                catch { /* a value the new control does not accept */ }
            }

            try { focus?.Focus(); } catch { /* not focusable any more */ }
            return restored;
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
            ToggleButton t => new() { ["IsChecked"] = t.IsChecked },
            Slider s => new() { ["Value"] = s.Value },
            NumericUpDown n => new() { ["Value"] = n.Value },
            SelectingItemsControl s => new() { ["SelectedIndex"] = s.SelectedIndex },
            Expander e => new() { ["IsExpanded"] = e.IsExpanded },
            ScrollViewer s => s.Offset == default ? null : new() { ["Offset"] = s.Offset },
            CalendarDatePicker d => new() { ["SelectedDate"] = d.SelectedDate },
            DatePicker d => new() { ["SelectedDate"] = d.SelectedDate },
            TimePicker t => new() { ["SelectedTime"] = t.SelectedTime },
            _ => null,
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
                case ToggleButton t:
                    return Set(t.IsChecked, (bool?)values["IsChecked"], v => t.IsChecked = v);
                case Slider s:
                    return Set(s.Value, (double)values["Value"]!, v => s.Value = v);
                case NumericUpDown n:
                    return Set(n.Value, (decimal?)values["Value"], v => n.Value = v);
                case SelectingItemsControl s:
                    var index = (int)values["SelectedIndex"]!;
                    return index < s.ItemCount && Set(s.SelectedIndex, index, v => s.SelectedIndex = v);
                case Expander e:
                    return Set(e.IsExpanded, (bool)values["IsExpanded"]!, v => e.IsExpanded = v);
                case ScrollViewer s:
                    return Set(s.Offset, (Vector)values["Offset"]!, v => s.Offset = v);
                case CalendarDatePicker d:
                    return Set(d.SelectedDate, (DateTime?)values["SelectedDate"], v => d.SelectedDate = v);
                case DatePicker d:
                    return Set(d.SelectedDate, (DateTimeOffset?)values["SelectedDate"], v => d.SelectedDate = v);
                case TimePicker t:
                    return Set(t.SelectedTime, (TimeSpan?)values["SelectedTime"], v => t.SelectedTime = v);
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
