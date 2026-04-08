using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Neo.PluginWindowWPF.MCP;

/// <summary>
/// Chat overlay for the WPF PluginWindow. Toggle with Ctrl+K.
/// User types a request -> Claude generates code -> SmartCompiler compiles -> Hot-Reload.
/// </summary>
internal sealed class ChatOverlay : Border
{
    private readonly TextBox _input;
    private readonly StackPanel _chatHistory;
    private readonly ScrollViewer _scrollViewer;
    private readonly TextBlock _statusText;
    private readonly Border _statusBar;
    private bool _isProcessing;

    /// <summary>Fired when the user submits a prompt.</summary>
    public event Func<string, Task>? PromptSubmitted;

    public ChatOverlay()
    {
        Visibility = Visibility.Collapsed;
        Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x10, 0x10, 0x20));
        Padding = new Thickness(16);
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Stretch;
        Width = 420;

        _chatHistory = new StackPanel();

        _scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _chatHistory,
        };

        _input = new TextBox
        {
            FontSize = 14,
            AcceptsReturn = false,
        };
        // WPF doesn't have Watermark natively, set placeholder via GotFocus/LostFocus
        _input.Text = "Describe a change... (Enter to send, Esc to close)";
        _input.Foreground = Brushes.Gray;
        _input.GotFocus += (_, _) =>
        {
            if (_input.Text == "Describe a change... (Enter to send, Esc to close)")
            {
                _input.Text = "";
                _input.Foreground = Brushes.White;
            }
        };
        _input.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_input.Text))
            {
                _input.Text = "Describe a change... (Enter to send, Esc to close)";
                _input.Foreground = Brushes.Gray;
            }
        };
        _input.KeyDown += OnInputKeyDown;

        _statusText = new TextBlock
        {
            Text = "Ctrl+K to toggle",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x90)),
        };

        _statusBar = new Border
        {
            Padding = new Thickness(4, 2, 4, 2),
            Child = _statusText,
        };

        var header = new TextBlock
        {
            Text = "N.E.O. Smart Edit",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8),
        };

        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_statusBar, Dock.Bottom);
        DockPanel.SetDock(_input, Dock.Bottom);

        var dockPanel = new DockPanel();
        dockPanel.Children.Add(header);
        dockPanel.Children.Add(_statusBar);
        dockPanel.Children.Add(_input);
        dockPanel.Children.Add(_scrollViewer);

        Child = dockPanel;
    }

    public void Toggle()
    {
        Visibility = Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (Visibility == Visibility.Visible)
        {
            _input.Focus();
            _input.SelectAll();
        }
    }

    public void AddUserMessage(string text)
    {
        var msg = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(40, 0, 0, 8),
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                FontSize = 13,
            }
        };
        _chatHistory.Children.Add(msg);
        ScrollToEnd();
    }

    public void AddAssistantMessage(string text, bool isError = false)
    {
        var msg = new Border
        {
            Background = new SolidColorBrush(isError ? Color.FromArgb(0x40, 0xFF, 0x44, 0x44) : Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 40, 8),
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = isError ? new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x88)) : new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xD0)),
                FontSize = 13,
            }
        };
        _chatHistory.Children.Add(msg);
        ScrollToEnd();
    }

    public void SetStatus(string status)
    {
        _statusText.Text = status;
    }

    public void SetProcessing(bool processing)
    {
        _isProcessing = processing;
        _input.IsEnabled = !processing;
        _statusText.Text = processing ? "Generating..." : "Ready";
        if (!processing)
            _input.Focus();
    }

    private void ScrollToEnd()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _scrollViewer.ScrollToEnd();
        }));
    }

    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && !_isProcessing)
        {
            var text = _input.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text) || text == "Describe a change... (Enter to send, Esc to close)")
                return;

            _input.Text = "";
            _input.Foreground = Brushes.White;
            e.Handled = true;

            AddUserMessage(text);

            if (PromptSubmitted != null)
            {
                try
                {
                    await PromptSubmitted.Invoke(text);
                }
                catch (Exception ex)
                {
                    AddAssistantMessage($"Error: {ex.Message}", isError: true);
                    SetProcessing(false);
                }
            }
        }
    }
}
