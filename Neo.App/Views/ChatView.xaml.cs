using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace Neo.App
{
    public partial class ChatView : System.Windows.Controls.UserControl
    {
        // Eine ObservableCollection sorgt dafür, dass die UI automatisch aktualisiert wird,
        // wenn wir neue Nachrichten hinzufügen.
        public ObservableCollection<ChatMessage> Messages { get; } = new ObservableCollection<ChatMessage>();

        public ChatView()
        {
            InitializeComponent();
            // Wir binden die ItemsControl an unsere Nachrichten-Sammlung.
            ChatItemsControl.ItemsSource = Messages;
        }

        /// <summary>
        /// Fügt eine neue Nachricht zum Chat-Verlauf hinzu.
        /// </summary>
        /// <param name="text">Der Text der Nachricht.</param>
        /// <param name="type">Der Typ der Nachricht (Prompt, Answer, etc.).</param>
        public void AddMessage(string text, BubbleType type)
        {
            // Erstellt ein neues ChatMessage-Objekt (dieses kümmert sich selbst um die Code-Extraktion)
            var message = new ChatMessage(text.Trim('\n'), type);

            // Fügt die Nachricht zur Sammlung hinzu. Die UI wird sich selbst aktualisieren.
            Messages.Add(message);

            // Automatisch zum Ende scrollen, damit die neuste Nachricht sichtbar ist.
            ScrollToEndDeferred();
        }

        public void AddMessageWithMarkdownFormatting(string markdown, BubbleType type)
        {
            var message = new ChatMessage(markdown.Trim('\n'), type, isMarkdown: true);
            Messages.Add(message);
            ScrollToEndDeferred();
        }

        private void ScrollToEndDeferred()
        {
            // Stellt sicher, dass nach Measure/Arrange wirklich bis ans Ende gescrollt wird.
            Dispatcher.InvokeAsync(() => ScrollViewer.ScrollToEnd(), DispatcherPriority.Background);
        }

        /// <summary>
        /// Löscht alle Nachrichten aus der Ansicht.
        /// </summary>
        public void Clear()
        {
            Messages.Clear();
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                var uri = e.Uri;
                if (uri != null && (uri.Scheme == "http" || uri.Scheme == "https"))
                {
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                }
            }
            catch { /* optional: Logging/Toast */ }
            e.Handled = true;
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            // Der 'sender' ist der Button, der geklickt wurde.
            // Sein 'DataContext' ist das ChatMessage-Objekt, das zu dieser Bubble gehört.
            if ((sender as FrameworkElement)?.DataContext is ChatMessage message)
            {
                try
                {
                    // Den Text der Nachricht in die Windows-Zwischenablage kopieren.
                    System.Windows.Clipboard.SetText(message.Text);
                }
                catch
                {
                    // Kann fehlschlagen, wenn eine andere Anwendung die Zwischenablage blockiert.
                    // Hier könnte man eine Fehlermeldung anzeigen, ist aber meist nicht nötig.
                }
            }
        }

        private void RouteMouseWheelToChatScroll(object sender, MouseWheelEventArgs e)
        {
            if (ScrollViewer == null) return;

            // „Smooth“ Weitergabe: Offset um Delta verschieben
            // e.Delta: +120 (nach oben), -120 (nach unten) – Precision-Touchpads liefern ggf. kleinere Schritte
            var newOffset = ScrollViewer.VerticalOffset - e.Delta;
            if (newOffset < 0) newOffset = 0;
            if (newOffset > ScrollViewer.ScrollableHeight) newOffset = ScrollViewer.ScrollableHeight;

            ScrollViewer.ScrollToVerticalOffset(newOffset);
            e.Handled = true; // verhindert, dass das Kind den Wheel-Event „verbraucht“
        }
    }
}