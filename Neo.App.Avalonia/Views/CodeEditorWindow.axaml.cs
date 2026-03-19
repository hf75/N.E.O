using System;
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

        public string? EditedCode { get; private set; }
        public bool Applied { get; private set; }

        public CodeEditorWindow() : this(string.Empty) { }

        public CodeEditorWindow(string currentCode)
        {
            _initialCode = currentCode;
            EditedCode = currentCode;
            InitializeComponent();

            // Defer initialization to after layout is complete
            codeEditor.Loaded += (_, _) =>
            {
                try
                {
                    var registryOptions = new RegistryOptions(ThemeName.LightPlus);
                    var installation = codeEditor.InstallTextMate(registryOptions);
                    installation.SetGrammar(registryOptions.GetScopeByLanguageId("csharp"));
                }
                catch { /* TextMate optional */ }

                codeEditor.Text = _initialCode;
                codeEditor.ScrollToLine(1);
            };
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
