using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Neo.IPC;
using Neo.Shared;

namespace Neo.PluginWindowAvalonia
{
    public partial class App
    {
        private string SerializeVisualTree(Avalonia.Controls.Control control, int depth)
        {
            const int maxDepth = 15;
            const int maxNodes = 500;
            if (depth > maxDepth || _serializedNodeCount > maxNodes)
                return "{ \"type\": \"...\", \"truncated\": true }";
            _serializedNodeCount++;

            var sb = new StringBuilder();
            sb.Append("{ ");

            // Type
            sb.Append($"\"type\": \"{control.GetType().Name}\"");

            // Name
            if (!string.IsNullOrEmpty(control.Name))
                sb.Append($", \"name\": \"{EscapeJson(control.Name)}\"");

            // Key properties based on control type
            var props = new Dictionary<string, string>();
            CollectKeyProperties(control, props);
            if (props.Count > 0)
            {
                sb.Append(", \"properties\": { ");
                sb.Append(string.Join(", ", props.Select(kv =>
                    $"\"{EscapeJson(kv.Key)}\": \"{EscapeJson(kv.Value)}\"")));
                sb.Append(" }");
            }

            // Bounds
            var bounds = control.Bounds;
            if (bounds.Width > 0 || bounds.Height > 0)
                sb.Append($", \"bounds\": {{ \"x\": {bounds.X:F0}, \"y\": {bounds.Y:F0}, \"w\": {bounds.Width:F0}, \"h\": {bounds.Height:F0} }}");

            // Children
            var children = control.GetVisualChildren()
                .OfType<Avalonia.Controls.Control>()
                .ToList();

            if (children.Count > 0)
            {
                sb.Append(", \"children\": [ ");
                sb.Append(string.Join(", ", children.Select(c => SerializeVisualTree(c, depth + 1))));
                sb.Append(" ]");
            }

            sb.Append(" }");
            return sb.ToString();
        }

        private static void CollectKeyProperties(Avalonia.Controls.Control control, Dictionary<string, string> props)
        {
            // Text content
            if (control is Avalonia.Controls.TextBlock tb)
            {
                if (tb.Text != null) props["Text"] = tb.Text;
                props["FontSize"] = tb.FontSize.ToString();
                if (tb.Foreground != null) props["Foreground"] = tb.Foreground.ToString()!;
                if (tb.FontWeight != default) props["FontWeight"] = tb.FontWeight.ToString();
            }
            // Before Button: in Avalonia CheckBox, RadioButton and ToggleSwitch are Buttons too, and
            // their checked state is what matters.
            else if (control is Avalonia.Controls.Primitives.ToggleButton toggle)
            {
                props["IsChecked"] = toggle.IsChecked?.ToString() ?? "null";
                if (toggle.Content != null) props["Content"] = toggle.Content.ToString()!;
                props["IsEnabled"] = toggle.IsEnabled.ToString();
            }
            else if (control is Avalonia.Controls.Button btn)
            {
                if (btn.Content != null) props["Content"] = btn.Content.ToString()!;
                props["IsEnabled"] = btn.IsEnabled.ToString();
                if (btn.Background != null) props["Background"] = btn.Background.ToString()!;
            }
            else if (control is Avalonia.Controls.TextBox txb)
            {
                if (txb.Text != null) props["Text"] = txb.Text;
                if (txb.PlaceholderText != null) props["PlaceholderText"] = txb.PlaceholderText;
                props["IsReadOnly"] = txb.IsReadOnly.ToString();
            }
            else if (control is Avalonia.Controls.ListBox lb)
            {
                props["ItemCount"] = lb.ItemCount.ToString();
                if (lb.SelectedIndex >= 0) props["SelectedIndex"] = lb.SelectedIndex.ToString();
            }
            else if (control is Avalonia.Controls.ComboBox cb)
            {
                props["ItemCount"] = cb.ItemCount.ToString();
                if (cb.SelectedIndex >= 0) props["SelectedIndex"] = cb.SelectedIndex.ToString();
            }
            else if (control is Avalonia.Controls.TreeViewItem tvi)
            {
                if (tvi.Header != null) props["Header"] = tvi.Header.ToString()!;
                props["IsExpanded"] = tvi.IsExpanded.ToString();
                props["IsSelected"] = tvi.IsSelected.ToString();
            }
            else if (control is Avalonia.Controls.Expander exp)
            {
                props["IsExpanded"] = exp.IsExpanded.ToString();
            }
            else if (control is Avalonia.Controls.Slider sl)
            {
                props["Value"] = sl.Value.ToString();
                props["Minimum"] = sl.Minimum.ToString();
                props["Maximum"] = sl.Maximum.ToString();
            }
            else if (control is Avalonia.Controls.Image img)
            {
                props["Source"] = img.Source?.ToString() ?? "null";
            }
            else if (control is Avalonia.Controls.ProgressBar pb)
            {
                props["Value"] = pb.Value.ToString();
                props["IsIndeterminate"] = pb.IsIndeterminate.ToString();
            }

            // Common layout properties for all controls
            if (control.IsVisible == false) props["IsVisible"] = "false";
            if (control.Opacity < 1.0) props["Opacity"] = control.Opacity.ToString("F2");
            try
            {
                var bg = (control as Avalonia.Controls.Primitives.TemplatedControl)?.Background
                      ?? (control as Avalonia.Controls.Panel)?.Background;
                if (bg != null && !props.ContainsKey("Background"))
                    props["Background"] = bg.ToString()!;
            }
            catch { }
        }

        private static string EscapeJson(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

        private static Avalonia.Controls.Control? FindControlInTree(Avalonia.Controls.Control root, string target)
        {
            // Collect all controls in the visual tree
            var all = new List<Avalonia.Controls.Control>();
            CollectControls(root, all);

            // 1. Try by Name
            var byName = all.FirstOrDefault(c => string.Equals(c.Name, target, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;

            // 2. Try by Type:Index (e.g. "TextBlock:0", "Button:2")
            var parts = target.Split(':', 2);
            var typeName = parts[0];
            int index = parts.Length > 1 && int.TryParse(parts[1], out var idx) ? idx : 0;

            var byType = all.Where(c => c.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (index < byType.Count) return byType[index];

            // 3. Try by Type alone (first match)
            if (byType.Count > 0) return byType[0];

            // 4. Partial name match
            var partial = all.FirstOrDefault(c => c.Name != null &&
                c.Name.Contains(target, StringComparison.OrdinalIgnoreCase));
            if (partial != null) return partial;

            return null;
        }

        private static void CollectControls(Avalonia.Visual visual, List<Avalonia.Controls.Control> result)
        {
            if (visual is Avalonia.Controls.Control ctrl)
                result.Add(ctrl);
            var children = visual.GetVisualChildren();
            foreach (var child in children)
            {
                if (child is Avalonia.Visual v)
                    CollectControls(v, result);
            }
        }
    }
}
