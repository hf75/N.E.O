using Avalonia.Controls;
using Avalonia.Interactivity;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace Neo.App.Views
{
    public partial class CodeEditorWindow : Window
    {
        public string? EditedCode { get; private set; }
        public bool Applied { get; private set; }

        public CodeEditorWindow() { InitializeComponent(); }

        public CodeEditorWindow(string currentCode)
        {
            InitializeComponent();

            // Set up C# syntax highlighting via TextMate
            var registryOptions = new RegistryOptions(ThemeName.Light);
            var textMateInstallation = codeEditor.InstallTextMate(registryOptions);
            textMateInstallation.SetGrammar(registryOptions.GetScopeByLanguageId("csharp"));

            codeEditor.Text = currentCode;
            EditedCode = currentCode;
        }

        private void Apply_Click(object? sender, RoutedEventArgs e)
        {
            EditedCode = codeEditor.Text;
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
