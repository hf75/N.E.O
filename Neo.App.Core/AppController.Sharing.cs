using System.Diagnostics;
using System.IO;
using System.Reflection;
using Neo.Collab;
using Newtonsoft.Json;

namespace Neo.App
{
    /// <summary>
    /// Shared sessions: several N.E.O.s, possibly far apart, work on the same app. One person
    /// changes the app at a time (<see cref="RunSharedChangeAsync"/> asks the session for the turn);
    /// afterwards the code, packages, AI history and chat go to everyone else, and each N.E.O.
    /// compiles and hot-reloads the change into its own running app — whatever each person has
    /// entered in their app stays there. The connection is direct (WebRTC), set up with an
    /// invitation code and a reply code; no server of ours is involved.
    /// </summary>
    public partial class AppController
    {
        private SessionChatRecorder _chat = null!;
        private int _sharedChangeDepth;
        private bool _applyingRemote;
        private bool _takingTurn;
        private string? _remoteTurnHolder;
        private bool _remoteBusy;

        /// <summary>The shared session this N.E.O. is in, or null.</summary>
        public SharedSession? Sharing { get; private set; }

        /// <summary>Joined, left, someone came or went: the share button and dialog update.</summary>
        public event Action? SharingChanged;

        /// <summary>What the session needs both sides to agree on to build the same app.</summary>
        public string SessionFramework =>
            (Settings.UseAvalonia ? "avalonia" : "wpf") + (Settings.UsePython ? "+python" : "") + (Settings.UseReactUi ? "+react" : "");

        private static string AppVersion =>
            (Assembly.GetEntryAssembly() ?? typeof(AppController).Assembly)
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

        // ── Starting, joining, leaving ───────────────────────────────────────

        /// <summary>
        /// Shares this session (the first time) and returns a new invitation code. Call it again for
        /// every further person. The person sends back a reply code: <see cref="AcceptGuestReplyAsync"/>.
        /// </summary>
        public async Task<string> CreateSessionInviteAsync(string name, CancellationToken cancellationToken = default)
        {
            if (Sharing is { Role: SessionRole.Guest })
                throw new InvalidOperationException("You are a guest in someone else's session. Leave it first to invite people yourself.");
            Sharing ??= SharedSession.Host(name, AppVersion, SessionFramework, CreateSessionCallbacks(), SynchronizationContext.Current);
            RaiseSharingChanged();
            return await Sharing.CreateInviteAsync(cancellationToken);
        }

        /// <summary>Uses a reply code; the guest is in the session when this returns.</summary>
        public async Task AcceptGuestReplyAsync(string replyCode, CancellationToken cancellationToken = default)
        {
            if (Sharing is not { Role: SessionRole.Host } session)
                throw new InvalidOperationException("Create an invitation first.");
            await session.AcceptReplyAsync(replyCode, cancellationToken);
            RaiseSharingChanged();
        }

        /// <summary>
        /// Answers an invitation and returns the reply code for the host. Then
        /// <see cref="CompleteJoinAsync"/>: once the host has pasted the reply, this N.E.O. gets the
        /// host's app — its current app is replaced.
        /// </summary>
        public async Task<string> JoinSessionAsync(string inviteCode, string name, CancellationToken cancellationToken = default)
        {
            if (Sharing != null)
                throw new InvalidOperationException("You are already in a session. Leave it first.");
            var session = await SharedSession.JoinAsync(inviteCode, name, AppVersion, SessionFramework,
                CreateSessionCallbacks(), SynchronizationContext.Current, cancellationToken);
            Sharing = session;
            RaiseSharingChanged();
            return session.ReplyCode!;
        }

        /// <summary>Waits until the host lets this N.E.O. in (the host's app arrives right after).</summary>
        public async Task CompleteJoinAsync(CancellationToken cancellationToken = default)
        {
            var session = Sharing ?? throw new InvalidOperationException("Not joining a session.");
            try
            {
                await session.WaitJoinedAsync(cancellationToken);
            }
            catch
            {
                if (Sharing == session)
                {
                    Sharing = null;
                    await session.LeaveAsync();
                    RaiseSharingChanged();
                }
                throw;
            }
            RaiseSharingChanged();
        }

        /// <summary>Leaves the session (as host: ends it for everyone). This N.E.O. keeps the app as it is.</summary>
        public async Task LeaveSessionAsync()
        {
            var session = Sharing;
            if (session == null) return;
            Sharing = null;
            await session.LeaveAsync();
            await EndRemoteBusyAsync();
            Logger.LogMessage(session.Role == SessionRole.Host ? "You ended the shared session." : "You left the shared session.", BubbleType.Info);
            EndTeleportExchanges();
            await BackFromTeleportAsync("Your copy of the app is back, as it was when it left.");
            RaiseSharingChanged();
        }

        private void RaiseSharingChanged()
        {
            try { SharingChanged?.Invoke(); }
            catch (Exception ex) { Debug.WriteLine("[Sharing] " + ex); }
        }

        private SessionCallbacks CreateSessionCallbacks() => new()
        {
            ApplyState = ApplyRemoteStateAsync,
            CaptureState = () => Task.FromResult(CaptureSessionState(StateKinds.Initial, "session", [])),
            CheckGuest = hello => hello.Framework == SessionFramework
                ? null
                : $"{hello.Name} builds {Describe(hello.Framework)} apps, this session builds {Describe(SessionFramework)} apps. Pick the same UI framework in the settings.",
            TurnTaken = (who, what) =>
            {
                _remoteTurnHolder = who;
                Logger.LogMessage($"{who} is {what} …", BubbleType.Info);
                _ = BeginRemoteBusyAsync(who);
            },
            TurnReleased = who =>
            {
                if (_remoteTurnHolder == who) _remoteTurnHolder = null;
                _ = EndRemoteBusyAsync();
            },
            ParticipantsChanged = names =>
            {
                if (AppIsAt != null && !names.Contains(AppIsAt))
                    _ = BackFromTeleportAsync($"{AppIsAt} left the session. Your copy of the app is back, as it was when it left.");
                EndTeleportExchanges(names);
                RaiseSharingChanged();
            },
            Teleport = TeleportMessageArrivedAsync,
            Notice = line => Logger.LogMessage(line, BubbleType.Info),
            Ended = reason =>
            {
                Sharing = null;
                _remoteTurnHolder = null;
                _ = EndRemoteBusyAsync();
                Logger.LogMessage(reason + " Your app stays as it is.", BubbleType.Info);
                EndTeleportExchanges();
                _ = BackFromTeleportAsync("Your copy of the app is back, as it was when it left.");
                RaiseSharingChanged();
            },
        };

        private static string Describe(string framework) =>
            framework.Replace("avalonia", "Avalonia").Replace("wpf", "WPF").Replace("+python", " + Python").Replace("+react", " + React");

        // ── Local changes: take the turn, change, tell the others ────────────

        private bool InSharedSession => Sharing is { } s && (s.Role == SessionRole.Host || s.IsConnected);

        /// <summary>
        /// Runs a change of the app. In a shared session it first asks for the turn (someone else
        /// may be changing the app right now), then sends the result — code, packages, AI history,
        /// the chat it produced — to the others, and gives the turn back. Outside a session, or
        /// inside another change, or while applying someone else's change, it just runs.
        /// </summary>
        /// <param name="activity">What the others are told while it runs ("working on a prompt: …").</param>
        /// <param name="title">How the change is named in their history; default: the activity.</param>
        private async Task<T> RunSharedChangeAsync<T>(string kind, string activity, T refused, Func<Task<T>> change, string? title = null)
        {
            var session = Sharing;
            if (!InSharedSession || session == null || _applyingRemote || _sharedChangeDepth > 0)
            {
                _sharedChangeDepth++;
                try { return await change(); }
                finally { _sharedChangeDepth--; }
            }

            if (CurrentStatus != AppStatus.Idle || _takingTurn)
            {
                if (_remoteTurnHolder != null)
                    Logger.LogMessage($"{_remoteTurnHolder} is changing the app right now — your turn comes when they are done.", BubbleType.Info);
                return refused;
            }

            TurnResult turn;
            _takingTurn = true;
            try { turn = await session.TryTakeTurnAsync(activity); }
            finally { _takingTurn = false; }
            if (!turn.Granted)
            {
                Logger.LogMessage((turn.Reason ?? "Someone else is changing the app right now.") + " Try again in a moment.", BubbleType.Info);
                return refused;
            }

            var before = CaptureSessionState(kind, title ?? activity, []);
            var chat = new List<ChatLine>();
            var recording = _chat.Record(chat);
            _sharedChangeDepth++;
            try
            {
                return await change();
            }
            finally
            {
                _sharedChangeDepth--;
                recording.Dispose();
                try
                {
                    var after = CaptureSessionState(kind, title ?? activity, chat);
                    bool codeChanged = after.Code != before.Code || !SamePackages(after.Packages, before.Packages);
                    bool replaces = kind is StateKinds.Clear or StateKinds.Import;
                    if (codeChanged || replaces || after.History != before.History || chat.Count > 0)
                        await session.PublishAsync(codeChanged || replaces ? after : after with { Code = null });
                }
                catch (Exception ex)
                {
                    Logger.LogMessage("Your change could not be sent to the others: " + ex.Message, BubbleType.CompletionError);
                }
                finally
                {
                    try { await session.ReleaseTurnAsync(); } catch { /* the session is gone */ }
                }
            }
        }

        private SessionState CaptureSessionState(string kind, string title, IReadOnlyList<ChatLine> chat) => new(
            kind,
            Sharing?.LocalName ?? "",
            title,
            chat.ToList(),
            GetCurrentCode() ?? AppState.LastCode ?? "",
            AppState.History ?? "",
            new Dictionary<string, string>(AppState.PackageVersions ?? new(), StringComparer.OrdinalIgnoreCase),
            SessionFramework);

        private static bool SamePackages(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
            a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);

        // ── Someone else's change ────────────────────────────────────────────

        private async Task ApplyRemoteStateAsync(SessionState state)
        {
            // An export, a crash repair of our own, … finish first.
            var waitUntil = DateTime.UtcNow.AddMinutes(2);
            while (CurrentStatus != AppStatus.Idle && !_remoteBusy && DateTime.UtcNow < waitUntil)
                await Task.Delay(100);

            // The lock shown while they worked must go: clearing and importing only run when idle.
            if (_remoteBusy)
            {
                _remoteBusy = false;
                if (CurrentStatus == AppStatus.Busy)
                    await SetStatusAsync(AppStatus.Idle, false);
            }
            _applyingRemote = true;
            try
            {
                switch (state.Kind)
                {
                    case StateKinds.Initial:
                        await ReplaceWithRemoteSessionAsync(state, $"You joined {state.Author}'s session. What you change now, everyone in it gets; one person changes the app at a time.");
                        break;

                    case StateKinds.Import:
                        await ReplaceWithRemoteSessionAsync(state, $"{state.Author} imported a project.");
                        break;

                    case StateKinds.Clear:
                        await ClearSessionCoreAsync();
                        Logger.LogMessage($"{state.Author} cleared the session.", BubbleType.Info);
                        break;

                    default:
                        ShowRemoteChat(state);
                        await ApplyRemoteChangeAsync(state);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"{state.Author}'s change could not be applied here: {ex.Message}", BubbleType.CompletionError);
            }
            finally
            {
                _applyingRemote = false;
                if (_remoteTurnHolder != null)
                    await BeginRemoteBusyAsync(_remoteTurnHolder);
            }
        }

        private void ShowRemoteChat(SessionState state)
        {
            if (state.Chat.Count == 0)
            {
                var note = state.Kind switch
                {
                    StateKinds.Undo => "undid the last change",
                    StateKinds.Redo => "redid a change",
                    StateKinds.Checkout => "went to another version from the history",
                    StateKinds.Edit => "edited the code",
                    _ => "changed the app",
                };
                Logger.LogMessage($"{state.Author} {note}.", BubbleType.Info);
                return;
            }
            foreach (var line in state.Chat)
            {
                var text = line.Kind == "prompt" ? $"{state.Author}: {line.Text}" : line.Text;
                var bubble = SessionChatRecorder.BubbleFor(line.Kind);
                if (line.Markdown) Logger.LogMessageWithMarkdownFormating(text, bubble);
                else Logger.LogMessage(text, bubble);
            }
        }

        /// <summary>A change on top of the current app: new packages, then the code — hot-reloaded when it can be.</summary>
        private async Task ApplyRemoteChangeAsync(SessionState state)
        {
            var known = AppState.PackageVersions ?? new Dictionary<string, string>();
            var missing = state.Packages
                .Where(p => !known.TryGetValue(p.Key, out var version) || version != p.Value)
                .ToDictionary(p => p.Key, p => p.Value);
            var currentCode = GetCurrentCode() ?? AppState.LastCode ?? "";
            var code = state.Code ?? currentCode;
            bool rebuild = code != currentCode || missing.Count > 0;

            await SetQuietStatusAsync(AppStatus.Compiling, $"Applying {state.Author}'s change…");
            try
            {
                if (missing.Count > 0)
                    await LoadAndApplyNugetPackages(missing);

                ApplyState(s =>
                {
                    s.History = state.History;
                    s.LastCode = code;
                }, title: $"{state.Author}: {Truncate(ToSingleLine(state.Title), 60)}", description: $"Shared session ({state.Kind})");

                VirtualProjectFiles?.UpdateFileContent("./currentcode.cs", code);
                if (_promptToDllSession != null)
                {
                    _promptToDllSession.State.CurrentCode = code;
                    _promptToDllSession.State.History = state.History;
                }

                if (rebuild && VirtualProjectFiles != null)
                    await CompileAndShowAsync(VirtualProjectFiles.GetSourceCodeAsStrings());
            }
            finally
            {
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        /// <summary>Someone's whole session replaces this one (joining, or an import over there).</summary>
        private async Task ReplaceWithRemoteSessionAsync(SessionState state, string announce)
        {
            var file = Path.Combine(Path.GetTempPath(), $"neo-session-{Guid.NewGuid():N}.resx");
            File.WriteAllText(file, JsonConvert.SerializeObject(new ResxData
            {
                Version = 2,
                Code = state.Code ?? "",
                Nuget = new Dictionary<string, string>(state.Packages, StringComparer.OrdinalIgnoreCase),
                History = state.History,
            }));
            try
            {
                await ImportProjectCoreAsync(file, announce);
            }
            finally
            {
                try { File.Delete(file); } catch { /* temp file */ }
            }
        }

        // ── While someone else changes the app ───────────────────────────────

        /// <summary>
        /// The prompt is locked while someone else changes the app — but the app stays usable, so no
        /// overlay (the WPF host would hide the app behind it).
        /// </summary>
        private async Task BeginRemoteBusyAsync(string who)
        {
            if (CurrentStatus != AppStatus.Idle || _applyingRemote) return;
            _remoteBusy = true;
            await SetQuietStatusAsync(AppStatus.Busy, $"{who} is changing the app…");
        }

        private async Task EndRemoteBusyAsync()
        {
            if (!_remoteBusy) return;
            _remoteBusy = false;
            if (CurrentStatus == AppStatus.Busy)
                await SetStatusAsync(AppStatus.Idle, false);
        }

        private async Task SetQuietStatusAsync(AppStatus status, string message)
        {
            CurrentStatus = status;
            _cancellationSource = new CancellationTokenSource();
            await _view.SetUiBusyState(isBusy: true, message: message, showCancel: false, showOverlay: false);
        }
    }
}
