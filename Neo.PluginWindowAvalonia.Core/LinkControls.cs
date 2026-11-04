using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

using Neo.App;
using Neo.Shared;

namespace Neo.PluginWindowAvalonia
{
    /// <summary>
    /// The controls and fields of a running app, by the addresses an app link knows them by: the name of a
    /// control, or its type and number among those of that type ("Slider:2").
    /// </summary>
    internal static class LinkControls
    {
        private const int MaxLines = 150;
        private const int MaxText = 80;

        // What the app put there, not the parts a slider is made of. Both the map and the search count these.
        private static IEnumerable<Control> Of(Control root) =>
            root.GetVisualDescendants().OfType<Control>().Where(c => c.TemplatedParent == null);

        public static Control? Find(Control root, string address)
        {
            var all = Of(root).ToList();
            var named = all.FirstOrDefault(c => string.Equals(c.Name, address, StringComparison.OrdinalIgnoreCase));
            if (named != null) return named;

            var parts = address.Split(':', 2);
            int number = 0;
            if (parts.Length == 2 && !int.TryParse(parts[1], out number)) return null;

            var same = all.Where(c => c.GetType().Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase)).ToList();
            return number >= 0 && number < same.Count ? same[number] : null;
        }

        public static AvaloniaProperty? PropertyOf(Control control, string name) =>
            AvaloniaPropertyRegistry.Instance.GetRegistered(control.GetType())
                .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        public static PropertyInfo? ClrPropertyOf(Control control, string name) =>
            control.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        /// <summary>
        /// What the model reads of a running program: its controls with their addresses, and the names of the
        /// fields that are data. It has the source anyway, but what someone entered or loaded stays on this PC:
        /// a text that is not in the source goes as its shape, and a field without its value.
        /// </summary>
        /// <param name="code">The source of the program.</param>
        public static string Describe(Control root, string code)
        {
            var text = new StringBuilder("Controls (address, then what it shows now; a text that is not in the source only as its shape, with letters as x and digits as 9):\n");
            var counted = new Dictionary<Type, int>();
            int lines = 0;

            foreach (var control in Of(root))
            {
                counted.TryGetValue(control.GetType(), out var number);
                counted[control.GetType()] = number + 1;
                if (lines >= MaxLines) continue;

                var address = string.IsNullOrEmpty(control.Name) ? $"{control.GetType().Name}:{number}" : control.Name;
                if (Line(control, address, root, code) is { } line)
                {
                    text.Append("  ").Append(line).Append('\n');
                    lines++;
                }
            }
            if (lines >= MaxLines) text.Append("  (and more)\n");

            text.Append("Fields (name, type):\n");
            foreach (var field in AppStateImage.DataFieldsOf(root.GetType()).OrderBy(f => f.Name, StringComparer.Ordinal))
                text.Append("  ").Append(field.Name).Append(" (").Append(TypeName(field.FieldType)).Append(")\n");
            return text.ToString();
        }

        /// <summary>
        /// A part of a running app, torn out of it (Fission): the part and what is in it, by their addresses in the whole app,
        /// so that the model finds the part in the source. What someone entered goes as its shape, as for a link.
        /// </summary>
        public static string DescribePart(Control root, Control part, string code)
        {
            var text = new StringBuilder("Controls of the part (address in the app, then what it shows now; a text that is not in the source only as its shape, with letters as x and digits as 9):\n");
            var counted = new Dictionary<Type, int>();
            int lines = 0;

            foreach (var control in Of(root))
            {
                counted.TryGetValue(control.GetType(), out var number);
                counted[control.GetType()] = number + 1;
                if (lines >= MaxLines || (!ReferenceEquals(control, part) && !part.IsVisualAncestorOf(control))) continue;

                var address = string.IsNullOrEmpty(control.Name) ? $"{control.GetType().Name}:{number}" : control.Name;
                var line = Line(control, address, root, code) ?? (ReferenceEquals(control, part) ? address : null);
                if (line == null) continue;
                text.Append("  ").Append(line).Append('\n');
                lines++;
            }
            if (lines >= MaxLines) text.Append("  (and more)\n");
            return text.ToString();
        }

        /// <summary>The address of a control in the app, as the map gives it: its name, or its type and number.</summary>
        public static string AddressOf(Control root, Control control)
        {
            if (!string.IsNullOrEmpty(control.Name) && control.TemplatedParent == null) return control.Name;
            var same = Of(root).Where(c => c.GetType() == control.GetType()).ToList();
            return $"{control.GetType().Name}:{Math.Max(0, same.IndexOf(control))}";
        }

        private static string? Line(Control control, string address, Control root, string code)
        {
            string Shown(string? text) => InTheSource(text, code) ? Shorten(text) : Shape(text);

            string Beside()
            {
                if (control.Parent is not Panel panel) return "";
                var label = panel.Children.TakeWhile(c => !ReferenceEquals(c, control)).OfType<TextBlock>().LastOrDefault();
                return label?.Text is { Length: > 0 } text ? $" beside=\"{Shown(text)}\"" : "";
            }

            string Caption(object? content) => content switch
            {
                string text => Shown(text),
                TextBlock block => Shown(block.Text),
                _ => "",
            };

            switch (control)
            {
                case ToggleButton toggle:
                    return $"{address} \"{Caption(toggle.Content)}\" IsChecked={toggle.IsChecked}";
                case Button button:
                    return $"{address} \"{Caption(button.Content)}\"";
                case TextBox box:
                    return $"{address} Text=\"{Shown(box.Text)}\"{Beside()}" + (string.IsNullOrEmpty(box.PlaceholderText) ? "" : $" placeholder=\"{Shown(box.PlaceholderText)}\"");
                case TextBlock block:
                    // The caption of a button is the button.
                    if (block.FindAncestorOfType<Button>() != null) return null;
                    return $"{address} Text=\"{Shown(block.Text)}\"";
                case Slider slider:
                    return $"{address} Value={Number(slider.Value)} from {Number(slider.Minimum)} to {Number(slider.Maximum)}{Beside()}";
                case ProgressBar bar:
                    return $"{address} Value={Number(bar.Value)} from {Number(bar.Minimum)} to {Number(bar.Maximum)}{Beside()}";
                case NumericUpDown number:
                    return $"{address} Value={Shape(number.Value?.ToString(CultureInfo.InvariantCulture))}{Beside()}";
                case ComboBox combo:
                    return $"{address} SelectedIndex={combo.SelectedIndex} of {combo.ItemCount}{Beside()}";
                case ListBox list:
                    return $"{address} SelectedIndex={list.SelectedIndex} of {list.ItemCount}{Beside()}";
                case Image:
                    return address;
                case Border border when border.Background != null && !ReferenceEquals(border, root):
                    return $"{address} Background={border.Background}";
                case Panel panel when panel.Background != null:
                    return $"{address} Background={panel.Background}";
            }

            // A control the program brought along.
            return control.GetType().Assembly == root.GetType().Assembly ? address : null;
        }

        private static bool InTheSource(string? text, string code) => string.IsNullOrEmpty(text) || code.Contains(text, StringComparison.Ordinal);

        // How a text looks, so that the model can read the format of a number from it, and nothing of what it says.
        internal static string Shape(string? text)
        {
            if (text == null) return "";
            var shape = new StringBuilder(Math.Min(text.Length, MaxText));
            foreach (var c in text.AsSpan(0, Math.Min(text.Length, MaxText)))
                shape.Append(char.IsDigit(c) ? '9' : char.IsLetter(c) ? (char.IsUpper(c) ? 'X' : 'x') : char.IsWhiteSpace(c) ? ' ' : c);
            return shape.ToString();
        }

        private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Shorten(string? text) =>
            text == null ? "" : text.Length <= MaxText ? text.ReplaceLineEndings(" ") : text[..MaxText].ReplaceLineEndings(" ") + "...";

        private static string TypeName(Type type)
        {
            if (Nullable.GetUnderlyingType(type) is { } inner) return TypeName(inner) + "?";
            if (type.IsArray) return TypeName(type.GetElementType()!) + "[]";
            if (!type.IsGenericType) return type.Name;
            return type.Name[..type.Name.IndexOf('`')] + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
        }
    }
}
