using System;
using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Neo.App.Views
{
    public partial class ApiKeySetupWindow : Window
    {
        public bool KeysSaved { get; private set; }

        public ApiKeySetupWindow()
        {
            InitializeComponent();
        }

        private void PasswordBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            BtnSave.IsEnabled =
                (TxtAnthropic.Text ?? "").Trim().Length > 0 ||
                (TxtOpenAI.Text ?? "").Trim().Length > 0 ||
                (TxtGemini.Text ?? "").Trim().Length > 0;

            if (BtnSave.IsEnabled)
            {
                BtnSave.Background = SolidColorBrush.Parse("#4A90E2");
                BtnSave.Foreground = Brushes.White;
            }
            else
            {
                BtnSave.Background = SolidColorBrush.Parse("#E0E0E0");
                BtnSave.Foreground = SolidColorBrush.Parse("#999999");
            }
        }

        private void BtnSave_Click(object? sender, RoutedEventArgs e)
        {
            var anthropic = (TxtAnthropic.Text ?? "").Trim();
            var openai = (TxtOpenAI.Text ?? "").Trim();
            var gemini = (TxtGemini.Text ?? "").Trim();

            if (anthropic.Length > 0)
                Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", anthropic, EnvironmentVariableTarget.User);

            if (openai.Length > 0)
                Environment.SetEnvironmentVariable("OPENAI_API_KEY", openai, EnvironmentVariableTarget.User);

            if (gemini.Length > 0)
                Environment.SetEnvironmentVariable("GEMINI_API_KEY", gemini, EnvironmentVariableTarget.User);

            KeysSaved = true;
            Close();
        }

        private void BtnSkip_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnReveal_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string targetName) return;

            var textBox = this.FindControl<TextBox>(targetName);
            if (textBox == null) return;

            // Toggle password char
            if (textBox.PasswordChar != default(char))
            {
                textBox.PasswordChar = default(char);
                btn.Content = "Hide";
            }
            else
            {
                textBox.PasswordChar = '*';
                btn.Content = "Eye";
            }
        }
    }
}
