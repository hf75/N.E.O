using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Neo.Collab;
using Neo.HotReload;
using Neo.IPC;

namespace Neo.App
{
    /// <summary>
    /// Teleport: in a shared session the running app moves to another person, with everything in it. Otherwise
    /// only code travels and each person's data stays on their PC; this is the exception, by a click of the
    /// sender and a yes of the receiver, and nothing leaves before that yes. The sender's window goes away and
    /// its copy waits as it was, until the app comes home, the sender wants it back or the other person is gone.
    /// </summary>
    public partial class AppController
    {
        private static readonly TimeSpan TeleportAnswerTime = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan TeleportArrivalTime = TimeSpan.FromMinutes(2);

        private readonly Dictionary<string, (string Person, TaskCompletionSource<TeleportMessage?> Reply)> _teleportExchanges = new();

        /// <summary>Who has the running app now. Null: it is here.</summary>
        public string? AppIsAt { get; private set; }

        /// <summary>Sends the running app to someone in the session. Throws with the reason when it stays here.</summary>
        public async Task TeleportAppAsync(string person)
        {
            var session = Sharing;
            if (session == null || !InSharedSession) throw new InvalidOperationException("Teleporting needs a shared session.");
            if (AppIsAt != null) throw new InvalidOperationException($"The app is at {AppIsAt}'s.");
            if (CurrentStatus != AppStatus.Idle) throw new InvalidOperationException("N.E.O. is busy with the app right now.");
            if (!ChildProcessService.HasLoadedControl) throw new InvalidOperationException("There is no running app to send.");

            var id = Guid.NewGuid().ToString("N");
            var answer = await ExchangeAsync(session, new TeleportMessage(TeleportPhases.Offer, id, session.LocalName, person), TeleportAnswerTime);
            if (answer is not { Ok: true })
                throw new InvalidOperationException(answer?.Reason ?? $"{person} did not answer.");

            // Taken now and not before: the user may have gone on working while the other person decided.
            var state = await ChildProcessService.CaptureAppStateAsync();
            if (state?.Image == null)
                throw new InvalidOperationException("The state of the app could not be taken" + (state is { Left.Count: > 0 } ? $": {state.Left[0]}" : "."));

            await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Visible: false));

            var arrived = await ExchangeAsync(session,
                new TeleportMessage(TeleportPhases.State, id, session.LocalName, person) { Image = state.Image, CodeHash = CodeHash(GetCurrentCode()) },
                TeleportArrivalTime);
            if (arrived is { Ok: true })
            {
                AppIsAt = person;
                Logger.LogMessage($"The app is at {person}'s now: {state.Values} value(s), {Size(state.Bytes)}. Your copy waits here as it was."
                    + (state.Left.Count > 0 ? $" These did not go along: {string.Join(", ", state.Left.Take(5))}." : ""), BubbleType.Info);
                RaiseSharingChanged();
                return;
            }

            var why = arrived?.Reason ?? $"{person} did not say whether it arrived.";
            await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Visible: true, Banner: $"Not sent: {why}", BannerColor: BannerRed, BannerSeconds: 8));
            throw new InvalidOperationException(why);
        }

        /// <summary>This person's copy of the app comes back, as it was when it left. The one at the other person's goes on there.</summary>
        public Task ShowMyCopyAsync() => BackFromTeleportAsync("Your copy of the app is back, as it was when it left.");

        private async Task BackFromTeleportAsync(string why)
        {
            if (AppIsAt == null) return;
            AppIsAt = null;
            await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Visible: true, Banner: why, BannerColor: BannerDark, BannerSeconds: 8));
            Logger.LogMessage(why, BubbleType.Info);
            RaiseSharingChanged();
        }

        private async Task<TeleportMessage?> ExchangeAsync(SharedSession session, TeleportMessage message, TimeSpan patience)
        {
            var reply = new TaskCompletionSource<TeleportMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _teleportExchanges[message.Id] = (message.To, reply);
            try
            {
                await session.SendTeleportAsync(message);
                return await Task.WhenAny(reply.Task, Task.Delay(patience)) == reply.Task ? await reply.Task : null;
            }
            catch (InvalidOperationException ex)
            {
                return new TeleportMessage(message.Phase, message.Id, message.To, message.From) { Reason = ex.Message };
            }
            finally
            {
                _teleportExchanges.Remove(message.Id);
            }
        }

        // Whoever has left will not answer any more. Null: everyone.
        private void EndTeleportExchanges(IReadOnlyList<string>? stillHere = null)
        {
            foreach (var (person, reply) in _teleportExchanges.Values.ToList())
                if (stillHere == null || !stillHere.Contains(person))
                    reply.TrySetResult(null);
        }

        internal Task TeleportMessageArrivedAsync(TeleportMessage message)
        {
            switch (message.Phase)
            {
                case TeleportPhases.Offer:
                    _ = AnswerOfferAsync(message); // people take their time, and the session goes on meanwhile
                    return Task.CompletedTask;

                case TeleportPhases.State:
                    return TakeTheAppAsync(message);

                default:
                    if (_teleportExchanges.TryGetValue(message.Id, out var exchange))
                        exchange.Reply.TrySetResult(message);
                    return Task.CompletedTask;
            }
        }

        private async Task AnswerOfferAsync(TeleportMessage offer)
        {
            var session = Sharing;
            if (session == null) return;

            string? problem = CurrentStatus != AppStatus.Idle ? $"N.E.O. at {session.LocalName}'s is busy right now."
                : !ChildProcessService.HasLoadedControl ? $"{session.LocalName} has no running app to take it."
                : null;

            // What was sent away comes home without a question.
            bool yes = problem == null && (AppIsAt == offer.From || await _view.AskAsync(
                "Take the running app?",
                $"{offer.From} wants to send you the running app, with everything in it.\n\nWhat is in your copy of the app now is replaced.",
                "Take it", "No, thanks"));

            await TellAsync(session, new TeleportMessage(TeleportPhases.Answer, offer.Id, session.LocalName, offer.From)
            {
                Ok = yes,
                Reason = yes ? null : problem ?? $"{session.LocalName} did not want to take it.",
            });
        }

        private async Task TakeTheAppAsync(TeleportMessage state)
        {
            var session = Sharing;
            if (session == null) return;

            string? problem = null;
            AppStateRestoredMessage? restored = null;
            if (state.CodeHash != CodeHash(GetCurrentCode()))
            {
                problem = "a change is on its way between the two copies of the app. Try again in a moment.";
            }
            else
            {
                restored = await ChildProcessService.RestoreAppStateAsync(new RestoreAppStateRequest(state.Image ?? "", HotReloadCoordinator.UiMethodNames.ToList()));
                if (restored is not { Success: true }) problem = restored?.Error ?? "the app did not answer.";
            }

            if (problem == null)
            {
                AppIsAt = null;
                await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Visible: true, BringToFront: true, Banner: $"The app came from {state.From}, as it was.", BannerColor: BannerGreen, BannerSeconds: 6));
                Logger.LogMessage($"{state.From} sent you the running app: {restored!.Values} value(s), {Size(state.Image?.Length ?? 0)}."
                    + (restored.Failed.Count > 0 ? $" Not these: {string.Join("; ", restored.Failed.Take(5))}." : ""), BubbleType.Info);
                RaiseSharingChanged();
            }
            else
            {
                Logger.LogMessage($"{state.From}'s app could not be taken here: {problem}", BubbleType.Info);
            }

            await TellAsync(session, new TeleportMessage(TeleportPhases.Arrived, state.Id, session.LocalName, state.From) { Ok = problem == null, Reason = problem });
        }

        private static async Task TellAsync(SharedSession session, TeleportMessage message)
        {
            try { await session.SendTeleportAsync(message); }
            catch (InvalidOperationException ex) { Debug.WriteLine("[Teleport] " + ex.Message); } // they have left, which they will notice
        }

        /// <summary>Which version of the app a state belongs to. Line ends may differ between the two PCs.</summary>
        internal static string CodeHash(string? code) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((code ?? "").Replace("\r\n", "\n").Trim())));

        private static string Size(long bytes) => bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : $"{bytes / (1024.0 * 1024):0.0} MB";
    }
}
