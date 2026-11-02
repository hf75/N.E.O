using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Neo.AssemblyForge;

namespace Neo.App.Views
{
    /// <summary>
    /// What an app link would connect: the model suggests, the person chooses, and can ask for more in their
    /// own words. Opens at once, because the model takes a moment and the drop should show something.
    /// </summary>
    public partial class LinkDialog : Window
    {
        private readonly Func<string?, LinkProposal?, CancellationToken, Task<LinkProposal>>? _propose;
        private readonly CancellationTokenSource _closing = new();
        private readonly List<CheckBox> _checks = [];
        private LinkProposal? _proposal;
        private bool _asking;

        /// <summary>What was chosen; null: nothing, no link.</summary>
        public LinkChoice? Choice { get; private set; }

        public LinkDialog() : this("", null) { }

        public LinkDialog(string title, Func<string?, LinkProposal?, CancellationToken, Task<LinkProposal>>? propose)
        {
            InitializeComponent();
            Title = title;
            Heading.Text = title;
            _propose = propose;
            Opened += async (_, _) => await AskAsync(null, "Reading both programs...");
            Closed += (_, _) => _closing.Cancel();
        }

        private async Task AskAsync(string? wish, string waiting)
        {
            if (_propose == null) return;

            _asking = true;
            WaitingText.Text = waiting;
            Waiting.IsVisible = true;
            NoteText.IsVisible = false;
            UpdateButtons();
            try
            {
                _proposal = await _propose(wish, _proposal, _closing.Token);
                Show(_proposal);
            }
            catch (OperationCanceledException) { /* the window was closed */ }
            catch (Exception ex)
            {
                NoteText.Text = "The model could not be asked: " + ex.Message;
                NoteText.IsVisible = true;
            }
            finally
            {
                _asking = false;
                Waiting.IsVisible = false;
                UpdateButtons();
            }
        }

        private void Show(LinkProposal proposal)
        {
            BlocksPanel.Children.Clear();
            _checks.Clear();

            foreach (var block in proposal.Blocks)
            {
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = block.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
                text.Children.Add(new TextBlock { Text = block.Detail, Foreground = Brush.Parse("#666666"), TextWrapping = TextWrapping.Wrap });

                var check = new CheckBox { Content = text, IsChecked = true };
                AutomationProperties.SetName(check, block.Title);
                check.IsCheckedChanged += (_, _) => UpdateButtons();
                _checks.Add(check);
                BlocksPanel.Children.Add(check);
            }

            NoteText.Text = proposal.Note;
            NoteText.IsVisible = !string.IsNullOrWhiteSpace(proposal.Note);

            var code = new StringBuilder();
            foreach (var block in proposal.Blocks)
                code.Append("// ").Append(block.Title).Append('\n').Append(block.Code).Append("\n\n");
            CodeBox.Text = code.ToString().TrimEnd();
            CodeExpander.IsVisible = proposal.Blocks.Count > 0;
        }

        private void UpdateButtons()
        {
            BtnConnect.IsEnabled = !_asking && _checks.Any(c => c.IsChecked == true);
            BtnAskAgain.IsEnabled = !_asking && !string.IsNullOrWhiteSpace(WishBox.Text);
        }

        private void Wish_Changed(object? sender, TextChangedEventArgs e) => UpdateButtons();

        private async void AskAgain_Click(object? sender, RoutedEventArgs e)
        {
            var wish = WishBox.Text;
            WishBox.Text = "";
            await AskAsync(wish, "Asking again...");
        }

        private void Connect_Click(object? sender, RoutedEventArgs e)
        {
            if (_proposal == null) return;
            var chosen = Enumerable.Range(0, _checks.Count).Where(i => _checks[i].IsChecked == true).ToList();
            Choice = new LinkChoice(chosen, chosen.Select(i => _proposal.Blocks[i].Title).ToList());
            Close();
        }

        private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
    }
}
