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

            if (!string.IsNullOrWhiteSpace(initialColor))
            {
                try
                {
                    var color = Color.Parse(initialColor);
                    ColorPicker.Color = color;
                }
                catch { /* invalid color string — use default */ }
            }
        }

        private void Ok_Click(object? sender, RoutedEventArgs e)
        {
            var c = ColorPicker.Color;
            SelectedColor = c.A == 255
                ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
                : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
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
