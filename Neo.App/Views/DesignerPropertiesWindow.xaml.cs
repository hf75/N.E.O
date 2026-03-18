using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using WpfControls = System.Windows.Controls;
using WpfInput = System.Windows.Input;
using WpfMedia = System.Windows.Media;
using WinForms = System.Windows.Forms;

using Neo.IPC;

namespace Neo.App
{
    public partial class DesignerPropertiesWindow : Window
    {
        private sealed record ChoiceItem(string Display, string Value);

        private static readonly IReadOnlyList<ChoiceItem> FontSizeChoices = new List<ChoiceItem>
        {
            new("8", "8"),
            new("9", "9"),
            new("10", "10"),
            new("11", "11"),
            new("12", "12"),
            new("14", "14"),
            new("16", "16"),
            new("18", "18"),
            new("20", "20"),
            new("22", "22"),
            new("24", "24"),
            new("28", "28"),
            new("32", "32"),
            new("36", "36"),
            new("48", "48"),
            new("64", "64"),
            new("72", "72"),
        };

        private static readonly IReadOnlyList<ChoiceItem> FontWeightChoices = new List<ChoiceItem>
        {
            new("Thin", "Thin"),
            new("ExtraLight", "ExtraLight"),
            new("Light", "Light"),
            new("Normal", "Normal"),
            new("Medium", "Medium"),
            new("SemiBold", "SemiBold"),
            new("Bold", "Bold"),
            new("ExtraBold", "ExtraBold"),
            new("Black", "Black"),
        };

        private static readonly IReadOnlyList<ChoiceItem> FontStyleChoices = new List<ChoiceItem>
        {
            new("Normal", "Normal"),
            new("Italic", "Italic"),
            new("Oblique", "Oblique"),
        };

        private static readonly IReadOnlyList<ChoiceItem> ThicknessChoices = new List<ChoiceItem>
        {
            new("0", "0"),
            new("2", "2"),
            new("4", "4"),
            new("6", "6"),
            new("8", "8"),
            new("12", "12"),
            new("16", "16"),
            new("24", "24"),
            new("32", "32"),
        };

        private static IReadOnlyList<ChoiceItem>? s_fontFamilyChoices;

        private DesignerSelectionMessage? _selection;
        private Dictionary<string, string> _original = new(StringComparer.Ordinal);
        private string _foregroundValue = string.Empty;
        private string _backgroundValue = string.Empty;
        private bool _followCursor = true;

        public event EventHandler<DesignerApplyRequestedEventArgs>? ApplyRequested;

        public DesignerPropertiesWindow()
        {
            InitializeComponent();
        }

        public void SetSelection(DesignerSelectionMessage selection)
        {
            _selection = selection ?? throw new ArgumentNullException(nameof(selection));

            _original = selection.Properties != null
                ? new Dictionary<string, string>(selection.Properties, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            HeaderText.Text = ShortTypeName(selection.ControlType);
            SubheaderText.Text = selection.DesignId;

            SetTextRow(RowText, TextValue, "Text");
            SetTextRow(RowContent, ContentValue, "Content");

            SetComboRow(RowFontFamily, FontFamilyValue, "FontFamily", GetFontFamilyChoices(), includeCurrentValueAsFirstOption: true);
            SetComboRow(RowFontSize, FontSizeValue, "FontSize", FontSizeChoices, includeCurrentValueAsFirstOption: true);
            SetComboRow(RowFontWeight, FontWeightValue, "FontWeight", FontWeightChoices, includeCurrentValueAsFirstOption: true);
            SetComboRow(RowFontStyle, FontStyleValue, "FontStyle", FontStyleChoices, includeCurrentValueAsFirstOption: true);

            SetColorRow(RowForeground, ForegroundPreview, "Foreground", ref _foregroundValue);
            SetColorRow(RowBackground, BackgroundPreview, "Background", ref _backgroundValue);

            SetComboRow(RowMargin, MarginValue, "Margin", ThicknessChoices, includeCurrentValueAsFirstOption: true);
            SetComboRow(RowPadding, PaddingValue, "Padding", ThicknessChoices, includeCurrentValueAsFirstOption: true);
        }

        public void RepositionNearCursor()
        {
            if (!_followCursor)
                return;

            if (!IsLoaded)
            {
                RoutedEventHandler? handler = null;
                handler = (_, _) =>
                {
                    Loaded -= handler;
                    RepositionNearCursor();
                };
                Loaded += handler;
                return;
            }

            try
            {
                var cursor = WinForms.Control.MousePosition;
                var screen = WinForms.Screen.FromPoint(cursor);

                var dpi = WpfMedia.VisualTreeHelper.GetDpi(this);
                var scaleX = dpi.DpiScaleX;
                var scaleY = dpi.DpiScaleY;

                var desiredLeft = cursor.X / scaleX + 14;
                var desiredTop = cursor.Y / scaleY + 14;

                var wa = screen.WorkingArea;
                var waLeft = wa.Left / scaleX;
                var waTop = wa.Top / scaleY;
                var waRight = wa.Right / scaleX;
                var waBottom = wa.Bottom / scaleY;

                var w = ActualWidth > 0 ? ActualWidth : Width;
                var h = ActualHeight > 0 ? ActualHeight : Height;
                if (w <= 0) w = 380;
                if (h <= 0) h = 320;

                desiredLeft = Math.Max(waLeft + 8, Math.Min(desiredLeft, waRight - w - 8));
                desiredTop = Math.Max(waTop + 8, Math.Min(desiredTop, waBottom - h - 8));

                Left = desiredLeft;
                Top = desiredTop;
            }
            catch
            {
                // best effort
            }
        }

        private void SetTextRow(FrameworkElement row, WpfControls.TextBox box, string key)
        {
            if (!_original.TryGetValue(key, out var value))
            {
                row.Visibility = Visibility.Collapsed;
                box.Text = string.Empty;
                return;
            }

            row.Visibility = Visibility.Visible;
            box.Text = value ?? string.Empty;
        }

        private void SetComboRow(
            FrameworkElement row,
            WpfControls.ComboBox combo,
            string key,
            IReadOnlyList<ChoiceItem> choices,
            bool includeCurrentValueAsFirstOption)
        {
            if (!_original.TryGetValue(key, out var value))
            {
                row.Visibility = Visibility.Collapsed;
                combo.ItemsSource = Array.Empty<ChoiceItem>();
                combo.SelectedIndex = -1;
                return;
            }

            row.Visibility = Visibility.Visible;
            value ??= string.Empty;

            IReadOnlyList<ChoiceItem> items = choices;
            if (includeCurrentValueAsFirstOption && !string.IsNullOrWhiteSpace(value) && !choices.Any(c => string.Equals(c.Value, value, StringComparison.Ordinal)))
            {
                var list = new List<ChoiceItem>(choices.Count + 1)
                {
                    new($"Current ({value})", value)
                };
                list.AddRange(choices);
                items = list;
            }

            combo.ItemsSource = items;
            combo.SelectedValue = value;
        }

        private void SetColorRow(FrameworkElement row, WpfControls.Border preview, string key, ref string currentValue)
        {
            if (!_original.TryGetValue(key, out var value))
            {
                row.Visibility = Visibility.Collapsed;
                currentValue = string.Empty;
                preview.Background = WpfMedia.Brushes.Transparent;
                return;
            }

            row.Visibility = Visibility.Visible;
            currentValue = value ?? string.Empty;
            preview.Background = TryCreateBrushFromHex(currentValue) ?? WpfMedia.Brushes.Transparent;
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_selection == null)
                return;

            var updates = new Dictionary<string, string>(StringComparer.Ordinal);

            CollectIfChanged(updates, "Text", TextValue.Text);
            CollectIfChanged(updates, "Content", ContentValue.Text);

            CollectIfChanged(updates, "FontFamily", FontFamilyValue.SelectedValue as string);
            CollectIfChanged(updates, "FontSize", FontSizeValue.SelectedValue as string);
            CollectIfChanged(updates, "FontWeight", FontWeightValue.SelectedValue as string);
            CollectIfChanged(updates, "FontStyle", FontStyleValue.SelectedValue as string);

            CollectIfChanged(updates, "Foreground", _foregroundValue);
            CollectIfChanged(updates, "Background", _backgroundValue);

            CollectIfChanged(updates, "Margin", MarginValue.SelectedValue as string);
            CollectIfChanged(updates, "Padding", PaddingValue.SelectedValue as string);

            if (updates.Count == 0)
                return;

            ApplyRequested?.Invoke(this, new DesignerApplyRequestedEventArgs(_selection, updates));
        }

        private void CollectIfChanged(Dictionary<string, string> updates, string key, string? value)
        {
            if (!_original.ContainsKey(key))
                return;

            _original.TryGetValue(key, out var oldValue);
            oldValue ??= string.Empty;
            value ??= string.Empty;

            if (!string.Equals(value, oldValue, StringComparison.Ordinal))
                updates[key] = value;
        }

        private void Window_KeyDown(object sender, WpfInput.KeyEventArgs e)
        {
            if (e.Key == WpfInput.Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        private void Header_MouseLeftButtonDown(object sender, WpfInput.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == WpfInput.MouseButton.Left)
            {
                _followCursor = false;
                try { DragMove(); } catch { /* may fail if window not in expected state */ }
            }
        }

        private void PickForeground_Click(object sender, RoutedEventArgs e)
        {
            if (!_original.ContainsKey("Foreground"))
                return;

            if (TryPickColor(_foregroundValue, out var hex))
            {
                _foregroundValue = hex;
                ForegroundPreview.Background = TryCreateBrushFromHex(hex) ?? WpfMedia.Brushes.Transparent;
            }
        }

        private void PickBackground_Click(object sender, RoutedEventArgs e)
        {
            if (!_original.ContainsKey("Background"))
                return;

            if (TryPickColor(_backgroundValue, out var hex))
            {
                _backgroundValue = hex;
                BackgroundPreview.Background = TryCreateBrushFromHex(hex) ?? WpfMedia.Brushes.Transparent;
            }
        }

        private static bool TryPickColor(string currentHex, out string hex)
        {
            hex = string.Empty;

            using var dlg = new WinForms.ColorDialog
            {
                FullOpen = true
            };

            if (TryParseHexToDrawingColor(currentHex, out var c))
                dlg.Color = c;

            if (dlg.ShowDialog() != WinForms.DialogResult.OK)
                return false;

            var chosen = dlg.Color;
            hex = $"#FF{chosen.R:X2}{chosen.G:X2}{chosen.B:X2}";
            return true;
        }

        private static bool TryParseHexToDrawingColor(string hex, out System.Drawing.Color color)
        {
            color = System.Drawing.Color.Empty;
            if (string.IsNullOrWhiteSpace(hex))
                return false;

            var t = hex.Trim();
            if (t.StartsWith("#", StringComparison.Ordinal))
                t = t.Substring(1);

            if (t.Length == 6)
            {
                if (!int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                    return false;

                var r = (rgb >> 16) & 0xFF;
                var g = (rgb >> 8) & 0xFF;
                var b = rgb & 0xFF;
                color = System.Drawing.Color.FromArgb(255, r, g, b);
                return true;
            }

            if (t.Length == 8)
            {
                if (!int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
                    return false;

                var a = (argb >> 24) & 0xFF;
                var r = (argb >> 16) & 0xFF;
                var g = (argb >> 8) & 0xFF;
                var b = argb & 0xFF;
                color = System.Drawing.Color.FromArgb(a, r, g, b);
                return true;
            }

            return false;
        }

        private static WpfMedia.Brush? TryCreateBrushFromHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return null;

            var t = hex.Trim();
            if (!t.StartsWith("#", StringComparison.Ordinal))
                return null;

            try
            {
                var converted = WpfMedia.ColorConverter.ConvertFromString(t);
                if (converted is WpfMedia.Color c)
                    return new WpfMedia.SolidColorBrush(c);
            }
            catch
            {
                // ignore invalid formats
            }

            return null;
        }

        private static IReadOnlyList<ChoiceItem> GetFontFamilyChoices()
        {
            if (s_fontFamilyChoices != null)
                return s_fontFamilyChoices;

            try
            {
                var names = WpfMedia.Fonts.SystemFontFamilies
                    .Select(f => f.Source)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new ChoiceItem(s, s))
                    .ToList();

                s_fontFamilyChoices = names;
                return s_fontFamilyChoices;
            }
            catch
            {
                s_fontFamilyChoices = Array.Empty<ChoiceItem>();
                return s_fontFamilyChoices;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private static string ShortTypeName(string? fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return "(unknown)";

            var parts = fullName.Split('.');
            return parts.Length > 0 ? parts.Last() : fullName;
        }
    }
}
