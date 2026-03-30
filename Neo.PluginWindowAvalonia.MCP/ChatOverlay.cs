using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Neo.PluginWindowAvalonia.MCP;

/// <summary>
/// Chat overlay for the PluginWindow. Toggle with Ctrl+K.
/// User types a request → Claude generates code → SmartCompiler compiles → Hot-Reload.
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
        IsVisible = false;
        Background = new SolidColorBrush(Color.Parse("#E0101020"));
        Padding = new Thickness(16);
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Stretch;
        Width = 420;

        _chatHistory = new StackPanel { Spacing = 8 };

        _scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _chatHistory,
        };

        _input = new TextBox
        {
            Watermark = "Describe a change... (Enter to send, Esc to close)",
            AcceptsReturn = false,
            FontSize = 14,
        };
        _input.KeyDown += OnInputKeyDown;

        _statusText = new TextBlock
        {
            Text = "Ctrl+K to toggle",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#808090")),
        };

        _statusBar = new Border
        {
            Padding = new Thickness(4, 2),
            Child = _statusText,
        };

        var header = new TextBlock
        {
            Text = "N.E.O. Smart Edit",
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8),
        };

        Child = new DockPanel
        {
            Children =
            {
                SetDock(header, Dock.Top),
                SetDock(_statusBar, Dock.Bottom),
                SetDock(_input, Dock.Bottom),
                _scrollViewer,
            }
        };
    }

    private static Control SetDock(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    public void Toggle()
    {
        IsVisible = !IsVisible;
        if (IsVisible)
        {
            _input.Focus();
            _input.SelectAll();
        }
    }

    public void AddUserMessage(string text)
    {
        var msg = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#30ffffff")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(40, 0, 0, 0),
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
            Background = new SolidColorBrush(isError ? Color.Parse("#40ff4444") : Color.Parse("#20ffffff")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 0, 40, 0),
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = isError ? new SolidColorBrush(Color.Parse("#ff8888")) : new SolidColorBrush(Color.Parse("#c0c0d0")),
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
        Dispatcher.UIThread.Post(() =>
        {
            _scrollViewer.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            IsVisible = false;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && !_isProcessing)
        {
            var text = _input.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            _input.Text = "";
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
