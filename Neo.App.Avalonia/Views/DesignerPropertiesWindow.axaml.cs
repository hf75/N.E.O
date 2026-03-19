using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

using Neo.IPC;

namespace Neo.App
{
    public partial class DesignerPropertiesWindow : Window
    {
        private sealed record ChoiceItem(string Display, string Value);

        private static readonly IReadOnlyList<ChoiceItem> FontSizeChoices = new List<ChoiceItem>
        {
            new("8", "8"), new("9", "9"), new("10", "10"), new("11", "11"),
            new("12", "12"), new("14", "14"), new("16", "16"), new("18", "18"),
            new("20", "20"), new("22", "22"), new("24", "24"), new("28", "28"),
            new("32", "32"), new("36", "36"), new("48", "48"), new("64", "64"),
            new("72", "72"),
        };

        private static readonly IReadOnlyList<ChoiceItem> FontWeightChoices = new List<ChoiceItem>
        {
            new("Thin", "Thin"), new("ExtraLight", "ExtraLight"), new("Light", "Light"),
            new("Normal", "Normal"), new("Medium", "Medium"), new("SemiBold", "SemiBold"),
            new("Bold", "Bold"), new("ExtraBold", "ExtraBold"), new("Black", "Black"),
        };

        private static readonly IReadOnlyList<ChoiceItem> FontStyleChoices = new List<ChoiceItem>
        {
            new("Normal", "Normal"), new("Italic", "Italic"), new("Oblique", "Oblique"),
        };

        private static readonly IReadOnlyList<ChoiceItem> ThicknessChoices = new List<ChoiceItem>
        {
            new("0", "0"), new("2", "2"), new("4", "4"), new("6", "6"),
            new("8", "8"), new("12", "12"), new("16", "16"), new("24", "24"),
            new("32", "32"),
        };

        private static IReadOnlyList<ChoiceItem>? s_fontFamilyChoices;

        private DesignerSelectionMessage? _selection;
        private Dictionary<string, string> _original = new(StringComparer.Ordinal);
        private string _foregroundValue = string.Empty;
        private string _backgroundValue = string.Empty;
#pragma warning disable CS0414 // Field assigned but not read — reserved for future cursor-follow feature
        private bool _followCursor = true;
#pragma warning restore CS0414

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

            SetComboRow(RowFontFamily, FontFamilyValue, "FontFamily", GetFontFamilyChoices());
            SetComboRow(RowFontSize, FontSizeValue, "FontSize", FontSizeChoices);
            SetComboRow(RowFontWeight, FontWeightValue, "FontWeight", FontWeightChoices);
            SetComboRow(RowFontStyle, FontStyleValue, "FontStyle", FontStyleChoices);

            SetColorRow(RowForeground, ForegroundPreview, "Foreground", ref _foregroundValue);
            SetColorRow(RowBackground, BackgroundPreview, "Background", ref _backgroundValue);

            SetComboRow(RowMargin, MarginValue, "Margin", ThicknessChoices);
            SetComboRow(RowPadding, PaddingValue, "Padding", ThicknessChoices);
        }

        private void SetTextRow(Visual row, TextBox box, string key)
        {
            if (row is not Control ctrl) return;
            if (!_original.TryGetValue(key, out var value))
            {
                ctrl.IsVisible = false;
                box.Text = string.Empty;
                return;
            }
            ctrl.IsVisible = true;
            box.Text = value ?? string.Empty;
        }

        private void SetComboRow(Visual row, ComboBox combo, string key, IReadOnlyList<ChoiceItem> choices)
        {
            if (row is not Control ctrl) return;
            if (!_original.TryGetValue(key, out var value))
            {
                ctrl.IsVisible = false;
                combo.ItemsSource = Array.Empty<ChoiceItem>();
                combo.SelectedIndex = -1;
                return;
            }

            ctrl.IsVisible = true;
            value ??= string.Empty;

            var items = choices.ToList();
            if (!string.IsNullOrWhiteSpace(value) && !items.Any(c => string.Equals(c.Value, value, StringComparison.Ordinal)))
                items.Insert(0, new ChoiceItem($"Current ({value})", value));

            combo.ItemsSource = items;
            combo.DisplayMemberBinding = new Binding("Display");
            var match = items.FirstOrDefault(i => i.Value == value);
            if (match != null) combo.SelectedItem = match;
        }

        private void SetColorRow(Visual row, Border preview, string key, ref string currentValue)
        {
            if (row is not Control ctrl) return;
            if (!_original.TryGetValue(key, out var value))
            {
                ctrl.IsVisible = false;
                currentValue = string.Empty;
                preview.Background = Brushes.Transparent;
                return;
            }

            ctrl.IsVisible = true;
            currentValue = value ?? string.Empty;
            preview.Background = TryCreateBrushFromHex(currentValue) ?? Brushes.Transparent;
        }

        private void Apply_Click(object? sender, RoutedEventArgs e)
        {
            if (_selection == null) return;

            var updates = new Dictionary<string, string>(StringComparer.Ordinal);

            CollectIfChanged(updates, "Text", TextValue.Text);
            CollectIfChanged(updates, "Content", ContentValue.Text);

            CollectIfChanged(updates, "FontFamily", (FontFamilyValue.SelectedItem as ChoiceItem)?.Value);
            CollectIfChanged(updates, "FontSize", (FontSizeValue.SelectedItem as ChoiceItem)?.Value);
            CollectIfChanged(updates, "FontWeight", (FontWeightValue.SelectedItem as ChoiceItem)?.Value);
            CollectIfChanged(updates, "FontStyle", (FontStyleValue.SelectedItem as ChoiceItem)?.Value);

            CollectIfChanged(updates, "Foreground", _foregroundValue);
            CollectIfChanged(updates, "Background", _backgroundValue);

            CollectIfChanged(updates, "Margin", (MarginValue.SelectedItem as ChoiceItem)?.Value);
            CollectIfChanged(updates, "Padding", (PaddingValue.SelectedItem as ChoiceItem)?.Value);

            if (updates.Count == 0) return;

            ApplyRequested?.Invoke(this, new DesignerApplyRequestedEventArgs(_selection, updates));
        }

        private void CollectIfChanged(Dictionary<string, string> updates, string key, string? value)
        {
            if (!_original.ContainsKey(key)) return;

            _original.TryGetValue(key, out var oldValue);
            oldValue ??= string.Empty;
            value ??= string.Empty;

            if (!string.Equals(value, oldValue, StringComparison.Ordinal))
                updates[key] = value;
        }

        private void Window_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _followCursor = false;
                BeginMoveDrag(e);
            }
        }

        private async void PickForeground_Click(object? sender, RoutedEventArgs e)
        {
            var picker = new Views.ColorPickerDialog(_foregroundValue);
            await picker.ShowDialog<object?>(this);
            if (picker.Confirmed && picker.SelectedColor != null)
            {
                _foregroundValue = picker.SelectedColor;
                ForegroundPreview.Background = TryCreateBrushFromHex(_foregroundValue) ?? Brushes.Transparent;
            }
        }

        private async void PickBackground_Click(object? sender, RoutedEventArgs e)
        {
            var picker = new Views.ColorPickerDialog(_backgroundValue);
            await picker.ShowDialog<object?>(this);
            if (picker.Confirmed && picker.SelectedColor != null)
            {
                _backgroundValue = picker.SelectedColor;
                BackgroundPreview.Background = TryCreateBrushFromHex(_backgroundValue) ?? Brushes.Transparent;
            }
        }

        private static IBrush? TryCreateBrushFromHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            try
            {
                return SolidColorBrush.Parse(hex);
            }
            catch
            {
                return null;
            }
        }

        private static IReadOnlyList<ChoiceItem> GetFontFamilyChoices()
        {
            if (s_fontFamilyChoices != null) return s_fontFamilyChoices;

            // Avalonia doesn't have a direct equivalent to WPF's Fonts.SystemFontFamilies
            // Use a reasonable default list
            s_fontFamilyChoices = new List<ChoiceItem>
            {
                new("Arial", "Arial"),
                new("Consolas", "Consolas"),
                new("Courier New", "Courier New"),
                new("Georgia", "Georgia"),
                new("Inter", "Inter"),
                new("Segoe UI", "Segoe UI"),
                new("Times New Roman", "Times New Roman"),
                new("Verdana", "Verdana"),
            };
            return s_fontFamilyChoices;
        }

        private void Close_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        private static string ShortTypeName(string? fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "(unknown)";
            var parts = fullName.Split('.');
            return parts.Length > 0 ? parts.Last() : fullName;
        }
    }
}
