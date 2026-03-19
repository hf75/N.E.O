using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Neo.App.Views
{
    public partial class CodeEditorWindow : Window
    {
        public string? EditedCode { get; private set; }
        public bool Applied { get; private set; }

        public CodeEditorWindow() : this(string.Empty) { }

        public CodeEditorWindow(string currentCode)
        {
            InitializeComponent();
            EditedCode = currentCode;
            codeTextBox.Text = currentCode;
        }

        private void Apply_Click(object? sender, RoutedEventArgs e)
        {
            EditedCode = codeTextBox.Text;
            Applied = true;
            Close();
        }

        private void Revert_Click(object? sender, RoutedEventArgs e)
        {
            Applied = false;
            Close();
        }
    }
}
