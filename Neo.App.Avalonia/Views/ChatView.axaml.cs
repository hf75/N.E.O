using System.Collections.ObjectModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Neo.App
{
    public partial class ChatView : UserControl
    {
        public ObservableCollection<ChatMessage> Messages { get; } = new ObservableCollection<ChatMessage>();

        public ChatView()
        {
            InitializeComponent();
            ChatItemsControl.ItemsSource = Messages;
        }

        public void AddMessage(string text, BubbleType type)
        {
            var message = new ChatMessage(text.Trim('\n'), type);
            Messages.Add(message);
            ScrollToEndDeferred();
        }

        public void AddMessageWithMarkdownFormatting(string markdown, BubbleType type)
        {
            // For Avalonia, we display markdown as plain text initially
            var message = new ChatMessage(markdown.Trim('\n'), type, isMarkdown: true);
            Messages.Add(message);
            ScrollToEndDeferred();
        }

        private void ScrollToEndDeferred()
        {
            Dispatcher.UIThread.Post(() =>
            {
                ScrollViewer.ScrollToEnd();
            }, DispatcherPriority.Background);
        }

        public void Clear()
        {
            Messages.Clear();
        }

        private void CopyButton_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Control control && control.DataContext is ChatMessage message)
            {
                try
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard != null)
                        _ = clipboard.SetTextAsync(message.Text);
                }
                catch
                {
                    // Clipboard may be unavailable
                }
            }
        }
    }
}
