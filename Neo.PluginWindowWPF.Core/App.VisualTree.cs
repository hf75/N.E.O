using Neo.IPC;
using Neo.Shared;
using System.Windows.Threading;
using System.Windows;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Reflection;
using System.Globalization;

namespace Neo.PluginWindowWPF
{
    public partial class App
    {
        private string SerializeVisualTree(FrameworkElement element, int depth)
        {
            const int maxDepth = 15;
            const int maxNodes = 500;
            if (depth > maxDepth || _serializedNodeCount > maxNodes)
                return "{ \"type\": \"...\", \"truncated\": true }";
            _serializedNodeCount++;

            var sb = new StringBuilder();
            sb.Append("{ ");

            sb.Append($"\"type\": \"{element.GetType().Name}\"");

            if (!string.IsNullOrEmpty(element.Name))
                sb.Append($", \"name\": \"{EscapeJson(element.Name)}\"");

            var props = new Dictionary<string, string>();
            CollectKeyProperties(element, props);
            if (props.Count > 0)
            {
                sb.Append(", \"properties\": { ");
                sb.Append(string.Join(", ", props.Select(kv =>
                    $"\"{EscapeJson(kv.Key)}\": \"{EscapeJson(kv.Value)}\"")));
                sb.Append(" }");
            }

            if (element.ActualWidth > 0 || element.ActualHeight > 0)
            {
                var pos = element.TranslatePoint(new Point(0, 0), MainWin);
                sb.Append($", \"bounds\": {{ \"x\": {pos.X:F0}, \"y\": {pos.Y:F0}, \"w\": {element.ActualWidth:F0}, \"h\": {element.ActualHeight:F0} }}");
            }

            var children = new List<FrameworkElement>();
            int childCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childCount; i++)
            {
                if (VisualTreeHelper.GetChild(element, i) is FrameworkElement child)
                    children.Add(child);
            }

            if (children.Count > 0)
            {
                sb.Append(", \"children\": [ ");
                sb.Append(string.Join(", ", children.Select(c => SerializeVisualTree(c, depth + 1))));
                sb.Append(" ]");
            }

            sb.Append(" }");
            return sb.ToString();
        }

        private static void CollectKeyProperties(FrameworkElement element, Dictionary<string, string> props)
        {
            if (element is TextBlock tb)
            {
                if (tb.Text != null) props["Text"] = tb.Text;
                props["FontSize"] = tb.FontSize.ToString(CultureInfo.InvariantCulture);
                if (tb.Foreground != null) props["Foreground"] = tb.Foreground.ToString()!;
                if (tb.FontWeight != default) props["FontWeight"] = tb.FontWeight.ToString();
            }
            else if (element is Button btn)
            {
                if (btn.Content != null) props["Content"] = btn.Content.ToString()!;
                props["IsEnabled"] = btn.IsEnabled.ToString();
                if (btn.Background != null) props["Background"] = btn.Background.ToString()!;
            }
            else if (element is TextBox txb)
            {
                if (txb.Text != null) props["Text"] = txb.Text;
                props["IsReadOnly"] = txb.IsReadOnly.ToString();
            }
            else if (element is ListBox lb)
            {
                props["ItemCount"] = lb.Items.Count.ToString();
                if (lb.SelectedIndex >= 0) props["SelectedIndex"] = lb.SelectedIndex.ToString();
            }
            else if (element is ComboBox cb)
            {
                props["ItemCount"] = cb.Items.Count.ToString();
                if (cb.SelectedIndex >= 0) props["SelectedIndex"] = cb.SelectedIndex.ToString();
            }
            else if (element is CheckBox chk)
            {
                props["IsChecked"] = chk.IsChecked?.ToString() ?? "null";
                if (chk.Content != null) props["Content"] = chk.Content.ToString()!;
            }
            else if (element is Slider sl)
            {
                props["Value"] = sl.Value.ToString(CultureInfo.InvariantCulture);
                props["Minimum"] = sl.Minimum.ToString(CultureInfo.InvariantCulture);
                props["Maximum"] = sl.Maximum.ToString(CultureInfo.InvariantCulture);
            }
            else if (element is Image img)
            {
                props["Source"] = img.Source?.ToString() ?? "null";
            }
            else if (element is ProgressBar pb)
            {
                props["Value"] = pb.Value.ToString(CultureInfo.InvariantCulture);
                props["IsIndeterminate"] = pb.IsIndeterminate.ToString();
            }

            if (element.Visibility != Visibility.Visible) props["Visibility"] = element.Visibility.ToString();
            if (element.Opacity < 1.0) props["Opacity"] = element.Opacity.ToString("F2", CultureInfo.InvariantCulture);
            try
            {
                var bg = (element as Control)?.Background ?? (element as Panel)?.Background;
                if (bg != null && !props.ContainsKey("Background"))
                    props["Background"] = bg.ToString()!;
            }
            catch { }
        }

        private static FrameworkElement? FindControlInTree(FrameworkElement root, string target)
        {
            var all = new List<FrameworkElement>();
            CollectControls(root, all);

            var byName = all.FirstOrDefault(c => string.Equals(c.Name, target, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;

            var parts = target.Split(':', 2);
            var typeName = parts[0];
            int index = parts.Length > 1 && int.TryParse(parts[1], out var idx) ? idx : 0;

            var byType = all.Where(c => c.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (index < byType.Count) return byType[index];
            if (byType.Count > 0) return byType[0];

            var partial = all.FirstOrDefault(c => c.Name != null &&
                c.Name.Contains(target, StringComparison.OrdinalIgnoreCase));
            return partial;
        }

        private static void CollectControls(DependencyObject parent, List<FrameworkElement> result)
        {
            if (parent is FrameworkElement fe)
                result.Add(fe);
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                CollectControls(child, result);
            }
        }

        private static string EscapeJson(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }
}
