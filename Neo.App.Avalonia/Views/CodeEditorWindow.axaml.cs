using Avalonia.Controls;
using Avalonia.Interactivity;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace Neo.App.Views
{
    public partial class CodeEditorWindow : Window
    {
        private string _initialCode;
        private bool _editorInitialized;

        public string? EditedCode { get; private set; }
        public bool Applied { get; private set; }

        public CodeEditorWindow() : this(string.Empty) { }

        public CodeEditorWindow(string currentCode)
        {
            _initialCode = currentCode;
            EditedCode = currentCode;
            InitializeComponent();
        }

        private void CodeEditor_AttachedToVisualTree(object? sender, global::Avalonia.VisualTreeAttachmentEventArgs e)
        {
            if (_editorInitialized) return;
            _editorInitialized = true;

            // TextMate syntax highlighting
            var registryOptions = new RegistryOptions(ThemeName.LightPlus);
            var installation = codeEditor.InstallTextMate(registryOptions);
            installation.SetGrammar(registryOptions.GetScopeByLanguageId("csharp"));

            // Set text AFTER TextMate is installed so colors apply immediately
            codeEditor.Text = _initialCode;
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
