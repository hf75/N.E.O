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
        // =======================================================
        // Data Injection + Reading (WPF)
        // =======================================================
        private InjectDataResult InjectDataOnControl(InjectDataRequest req)
        {
            var root = MainWin.GetLoadedUserControl();
            if (root == null)
                return new InjectDataResult(false, "No control loaded.");

            if (string.Equals(req.Mode, "fill", StringComparison.OrdinalIgnoreCase))
                return InjectFillMode(root, req);

            var target = string.Equals(req.Target, "root", StringComparison.OrdinalIgnoreCase)
                ? root
                : FindControlInTree(root, req.Target);

            if (target == null)
                return new InjectDataResult(false, $"Control '{req.Target}' not found.");

            if (target is not ItemsControl itemsControl)
                return new InjectDataResult(false,
                    $"Control '{target.GetType().Name}' is not an ItemsControl. Use mode 'fill' for form controls.");

            var items = new List<Dictionary<string, object>>();
            string[] fields;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(req.DataJson);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return new InjectDataResult(false, "DataJson must be a JSON array for replace/append mode.");

                foreach (var elem in doc.RootElement.EnumerateArray())
                {
                    if (elem.ValueKind == System.Text.Json.JsonValueKind.Object)
                        items.Add(JsonElementToDict(elem));
                    else
                        items.Add(new Dictionary<string, object> { ["value"] = elem.ToString() });
                }

                var fieldSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                    foreach (var key in item.Keys)
                        fieldSet.Add(key);
                fields = fieldSet.ToArray();
            }
            catch (System.Text.Json.JsonException ex)
            {
                return new InjectDataResult(false, $"Invalid JSON: {ex.Message}");
            }

            bool isAppend = string.Equals(req.Mode, "append", StringComparison.OrdinalIgnoreCase);

            if (isAppend && itemsControl.ItemsSource is System.Collections.ObjectModel.ObservableCollection<Dictionary<string, object>> existing)
            {
                foreach (var item in items)
                    existing.Add(item);
            }
            else
            {
                var collection = new System.Collections.ObjectModel.ObservableCollection<Dictionary<string, object>>();
                if (isAppend && itemsControl.ItemsSource is System.Collections.IEnumerable oldItems)
                {
                    foreach (var old in oldItems)
                    {
                        if (old is Dictionary<string, object> dict)
                            collection.Add(dict);
                    }
                }
                foreach (var item in items)
                    collection.Add(item);
                itemsControl.ItemsSource = collection;
            }

            int totalCount = 0;
            if (itemsControl.ItemsSource is System.Collections.IEnumerable countable)
                foreach (var _ in countable) totalCount++;

            return new InjectDataResult(true,
                $"{(isAppend ? "Appended" : "Replaced")} {items.Count} items on {target.GetType().Name}.",
                totalCount, fields);
        }

        private InjectDataResult InjectFillMode(FrameworkElement root, InjectDataRequest req)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(req.DataJson);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                    return new InjectDataResult(false, "DataJson must be a JSON object for fill mode.");

                int filled = 0;
                var errors = new List<string>();

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var control = FindControlInTree(root, prop.Name);
                    if (control == null) { errors.Add($"'{prop.Name}' not found"); continue; }

                    try
                    {
                        switch (control)
                        {
                            case TextBox tb:
                                tb.Text = prop.Value.GetString() ?? prop.Value.GetRawText();
                                break;
                            case TextBlock tb:
                                tb.Text = prop.Value.GetString() ?? prop.Value.GetRawText();
                                break;
                            case CheckBox cb:
                                cb.IsChecked = prop.Value.ValueKind == System.Text.Json.JsonValueKind.True;
                                break;
                            case Slider sl:
                                sl.Value = prop.Value.GetDouble();
                                break;
                            case ComboBox combo:
                                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                                    combo.SelectedIndex = prop.Value.GetInt32();
                                else
                                {
                                    var text = prop.Value.GetString();
                                    for (int i = 0; i < combo.Items.Count; i++)
                                        if (combo.Items[i]?.ToString() == text) { combo.SelectedIndex = i; break; }
                                }
                                break;
                            case System.Windows.Controls.Primitives.ToggleButton ts:
                                ts.IsChecked = prop.Value.ValueKind == System.Text.Json.JsonValueKind.True;
                                break;
                            default:
                                var textProp = control.GetType().GetProperty("Text");
                                if (textProp != null && textProp.CanWrite)
                                    textProp.SetValue(control, prop.Value.GetString() ?? prop.Value.GetRawText());
                                else
                                    errors.Add($"'{prop.Name}' ({control.GetType().Name}): unsupported type");
                                continue;
                        }
                        filled++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"'{prop.Name}': {ex.Message}");
                    }
                }

                var msg = $"Filled {filled} control(s).";
                if (errors.Count > 0) msg += $" Issues: {string.Join("; ", errors)}";
                return new InjectDataResult(true, msg, filled);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return new InjectDataResult(false, $"Invalid JSON: {ex.Message}");
            }
        }

        private ReadDataResult ReadDataFromControl(ReadDataRequest req)
        {
            var root = MainWin.GetLoadedUserControl();
            if (root == null)
                return new ReadDataResult(false, "No control loaded.");

            var target = string.Equals(req.Target, "root", StringComparison.OrdinalIgnoreCase)
                ? root
                : FindControlInTree(root, req.Target);

            if (target == null)
                return new ReadDataResult(false, $"Control '{req.Target}' not found.");

            var scope = req.Scope?.ToLowerInvariant();
            if (scope == null)
            {
                if (target is ItemsControl) scope = "items";
                else if (target is TextBox || target is CheckBox || target is Slider) scope = "value";
                else scope = "form";
            }

            switch (scope)
            {
                case "items": return ReadItemsData(target);
                case "form": return ReadFormData(target);
                case "value": return ReadSingleValue(target);
                default: return new ReadDataResult(false, $"Unknown scope '{scope}'. Use 'items', 'form', or 'value'.");
            }
        }

        private ReadDataResult ReadItemsData(FrameworkElement target)
        {
            if (target is not ItemsControl ic)
                return new ReadDataResult(false, $"{target.GetType().Name} is not an ItemsControl.");

            var items = new List<object>();
            if (ic.ItemsSource is System.Collections.IEnumerable source)
                foreach (var item in source)
                    items.Add(item is Dictionary<string, object> dict ? dict : item?.ToString() ?? "null");

            var json = System.Text.Json.JsonSerializer.Serialize(items);
            return new ReadDataResult(true, $"Read {items.Count} items.", json);
        }

        private ReadDataResult ReadFormData(FrameworkElement root)
        {
            var all = new List<FrameworkElement>();
            CollectControls(root, all);

            var data = new Dictionary<string, object>();
            foreach (var el in all.Where(c => !string.IsNullOrEmpty(c.Name)))
            {
                switch (el)
                {
                    case TextBox tb: data[el.Name!] = tb.Text ?? ""; break;
                    case TextBlock tb: data[el.Name!] = tb.Text ?? ""; break;
                    case CheckBox cb: data[el.Name!] = cb.IsChecked ?? false; break;
                    case Slider sl: data[el.Name!] = sl.Value; break;
                    case ComboBox combo:
                        data[el.Name!] = combo.SelectedItem?.ToString() ?? "";
                        break;
                }
            }

            var json = System.Text.Json.JsonSerializer.Serialize(data);
            return new ReadDataResult(true, $"Read {data.Count} form fields.", json);
        }

        private ReadDataResult ReadSingleValue(FrameworkElement target)
        {
            string? val = target switch
            {
                TextBox tb => tb.Text,
                TextBlock tb => tb.Text,
                CheckBox cb => cb.IsChecked?.ToString(),
                Slider sl => sl.Value.ToString(CultureInfo.InvariantCulture),
                ComboBox combo => combo.SelectedItem?.ToString(),
                _ => null
            };

            if (val == null)
            {
                var textProp = target.GetType().GetProperty("Text");
                val = textProp?.GetValue(target)?.ToString();
            }

            return new ReadDataResult(true, $"Value from {target.GetType().Name}.", val ?? "null");
        }

        private static Dictionary<string, object> JsonElementToDict(System.Text.Json.JsonElement elem)
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in elem.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    _ => prop.Value.GetString() ?? prop.Value.GetRawText()
                };
            }
            return dict;
        }
    }
}
