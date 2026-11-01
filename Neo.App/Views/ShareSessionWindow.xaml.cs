using System.Windows;
using Neo.Collab;

namespace Neo.App
{
    /// <summary>
    /// Invite people into this session, or join someone else's: invitation code out, reply code in
    /// (or the other way round). Stays open beside the main window while the codes travel.
    /// </summary>
    public partial class ShareSessionWindow : Window
    {
        private readonly AppController _controller;
        private bool _busy;

        public ShareSessionWindow(AppController controller)
        {
            InitializeComponent();
            _controller = controller;

            var saved = controller.Settings.SessionName;
            NameBox.Text = string.IsNullOrWhiteSpace(saved) ? Environment.UserName : saved;
            controller.SharingChanged += OnSharingChanged;
            Closed += (_, _) => controller.SharingChanged -= OnSharingChanged;
            Refresh();
        }

        private string DisplayName
        {
            get
            {
                var name = string.IsNullOrWhiteSpace(NameBox.Text) ? Environment.UserName : NameBox.Text.Trim();
                if (name != _controller.Settings.SessionName)
                {
                    _controller.Settings.SessionName = name;
                    var stored = SettingsService.Load();
                    stored.SessionName = name;
                    SettingsService.Save(stored);
                }
                return name;
            }
        }

        private void OnSharingChanged() => Dispatcher.BeginInvoke(Refresh);

        private void Refresh()
        {
            var session = _controller.Sharing;
            bool guest = session is { Role: SessionRole.Guest };
            bool host = session is { Role: SessionRole.Host };

            NameBox.IsEnabled = session == null;
            InviteOut.IsEnabled = !string.IsNullOrEmpty(InviteOut.Text);
            ReplyOut.IsEnabled = !string.IsNullOrEmpty(ReplyOut.Text);
            BtnCreateInvite.IsEnabled = !guest && !_busy;
            BtnConnect.IsEnabled = host && !_busy && !string.IsNullOrEmpty(InviteOut.Text);
            ReplyIn.IsEnabled = BtnConnect.IsEnabled;
            BtnJoin.IsEnabled = session == null && !_busy;
            InviteIn.IsEnabled = session == null;
            BtnLeave.Visibility = session != null ? Visibility.Visible : Visibility.Collapsed;
            BtnLeave.Content = host ? "End session" : "Leave";

            StatusText.Text = session switch
            {
                null => "Not shared.",
                { Role: SessionRole.Host } s when s.Participants.Count <= 1 => string.IsNullOrEmpty(InviteOut.Text)
                    ? "Shared — nobody has joined yet."
                    : "Invitation copied — send it, then paste the reply code.",
                { Role: SessionRole.Host } s => $"You host this session. In it: {string.Join(", ", s.Participants)}.",
                { IsConnected: true } s => $"You are in {s.HostName}'s session. In it: {string.Join(", ", s.Participants)}.",
                _ => "Waiting for the host to paste your reply code …",
            };
        }

        private void ShowError(string? message)
        {
            ErrorText.Text = message ?? "";
            ErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task RunAsync(Func<Task> action)
        {
            _busy = true;
            ShowError(null);
            Refresh();
            try { await action(); }
            catch (Exception ex) { ShowError(ex.Message); }
            finally
            {
                _busy = false;
                Refresh();
            }
        }

        private async void CreateInvite_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
        {
            StatusText.Text = "Creating an invitation …";
            InviteOut.Text = await _controller.CreateSessionInviteAsync(DisplayName);
            ReplyIn.Text = "";
            BtnCopyInvite.IsEnabled = true;
            Copy(InviteOut.Text);
        });

        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            var reply = ReplyIn.Text ?? "";
            if (string.IsNullOrWhiteSpace(reply)) { ShowError("Paste the reply code first."); return; }
            await RunAsync(async () =>
            {
                StatusText.Text = "Connecting …";
                await _controller.AcceptGuestReplyAsync(reply);
                InviteOut.Text = "";
                ReplyIn.Text = "";
                BtnCopyInvite.IsEnabled = false;
            });
        }

        private async void Join_Click(object sender, RoutedEventArgs e)
        {
            var invite = InviteIn.Text ?? "";
            if (string.IsNullOrWhiteSpace(invite)) { ShowError("Paste the invitation first."); return; }
            string? reply = null;
            await RunAsync(async () =>
            {
                StatusText.Text = "Creating your reply …";
                reply = await _controller.JoinSessionAsync(invite, DisplayName);
                ReplyOut.Text = reply;
                BtnCopyReply.IsEnabled = true;
                Copy(reply);
            });
            if (reply == null) return;

            StatusText.Text = "Reply copied — send it to the host. Waiting for them to paste it …";
            try
            {
                await _controller.CompleteJoinAsync();
                InviteIn.Text = "";
                ReplyOut.Text = "";
                BtnCopyReply.IsEnabled = false;
            }
            catch (Exception ex) { ShowError(ex.Message); }
            Refresh();
        }

        private async void Leave_Click(object sender, RoutedEventArgs e) => await RunAsync(_controller.LeaveSessionAsync);

        private void CopyInvite_Click(object sender, RoutedEventArgs e) => Copy(InviteOut.Text);

        private void CopyReply_Click(object sender, RoutedEventArgs e) => Copy(ReplyOut.Text);

        private static void Copy(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { System.Windows.Clipboard.SetText(text); }
            catch { /* the clipboard is busy; the code is in the box */ }
        }
    }
}
