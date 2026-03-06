using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace Neo.App.Views;

public partial class ApiKeySetupWindow : Window
{
    public bool KeysSaved { get; private set; }

    public ApiKeySetupWindow()
    {
        InitializeComponent();
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        BtnSave.IsEnabled =
            TxtAnthropic.Password.Trim().Length > 0 ||
            TxtOpenAI.Password.Trim().Length > 0 ||
            TxtGemini.Password.Trim().Length > 0;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var anthropic = TxtAnthropic.Password.Trim();
        var openai = TxtOpenAI.Password.Trim();
        var gemini = TxtGemini.Password.Trim();

        if (anthropic.Length > 0)
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", anthropic, EnvironmentVariableTarget.User);

        if (openai.Length > 0)
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", openai, EnvironmentVariableTarget.User);

        if (gemini.Length > 0)
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", gemini, EnvironmentVariableTarget.User);

        KeysSaved = true;
        Close();
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void BtnReveal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string targetName) return;

        var passwordBox = (PasswordBox)FindName(targetName);
        if (passwordBox == null) return;

        var parent = (System.Windows.Controls.Grid)passwordBox.Parent;
        var existing = parent.Children.OfType<System.Windows.Controls.TextBox>().FirstOrDefault();

        if (existing != null)
        {
            // Switch back to PasswordBox
            passwordBox.Password = existing.Text;
            passwordBox.Visibility = Visibility.Visible;
            parent.Children.Remove(existing);
            btn.Content = "\uE7B3"; // Eye icon
        }
        else
        {
            // Switch to TextBox
            var textBox = new System.Windows.Controls.TextBox
            {
                Text = passwordBox.Password,
                Padding = passwordBox.Padding,
                FontSize = passwordBox.FontSize,
            };
            textBox.TextChanged += (_, _) => passwordBox.Password = textBox.Text;
            System.Windows.Controls.Grid.SetColumn(textBox, 0);
            parent.Children.Add(textBox);
            passwordBox.Visibility = Visibility.Collapsed;
            btn.Content = "\uED1A"; // Hide icon
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
