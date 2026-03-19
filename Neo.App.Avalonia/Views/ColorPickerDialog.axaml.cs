using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Neo.App.Views
{
    public partial class ColorPickerDialog : Window
    {
        public string? SelectedColor { get; private set; }
        public bool Confirmed { get; private set; }

        public ColorPickerDialog() : this(string.Empty) { }

        public ColorPickerDialog(string initialColor)
        {
            InitializeComponent();
            ColorInput.Text = initialColor;
            UpdatePreview(initialColor);
        }

        private void ColorInput_TextChanged(object? sender, TextChangedEventArgs e)
        {
            UpdatePreview(ColorInput.Text ?? "");
        }

        private void UpdatePreview(string colorText)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(colorText))
                {
                    ColorPreview.Background = SolidColorBrush.Parse(colorText);
                    return;
                }
            }
            catch { }
            ColorPreview.Background = Brushes.Transparent;
        }

        private void Swatch_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string color)
            {
                ColorInput.Text = color;
            }
        }

        private void Ok_Click(object? sender, RoutedEventArgs e)
        {
            SelectedColor = ColorInput.Text?.Trim();
            Confirmed = true;
            Close();
        }

        private void Cancel_Click(object? sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }
    }
}
