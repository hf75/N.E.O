using System.Threading.Channels;

namespace Neo.Collab;

public enum SessionRole { Host, Guest }

/// <summary>Whether this participant may change the app now; if not, who is changing it.</summary>
public readonly record struct TurnResult(bool Granted, string? Holder, string? Reason);

/// <summary>What the session needs from the app, and what it tells the app. All calls arrive in order, on the session's context.</summary>
public sealed class SessionCallbacks
{
    /// <summary>Apply another participant's change (or, right after joining, the host's session). Awaited.</summary>
    public Func<SessionState, Task> ApplyState { get; init; } = _ => Task.CompletedTask;

    /// <summary>Host: the current state, for someone who just joined.</summary>
    public Func<Task<SessionState>>? CaptureState { get; init; }

    /// <summary>Host: can this guest work on the same app? Returns the reason when not.</summary>
    public Func<HelloMessage, string?> CheckGuest { get; init; } = _ => null;

    /// <summary>Someone else started changing the app: (name, what they do).</summary>
    public Action<string, string> TurnTaken { get; init; } = (_, _) => { };

    /// <summary>That person is done.</summary>
    public Action<string> TurnReleased { get; init; } = _ => { };

    /// <summary>Everyone in the session, host first.</summary>
    public Action<IReadOnlyList<string>> ParticipantsChanged { get; init; } = _ => { };

    /// <summary>Joins, leaves, rejections — one line for the chat.</summary>
    public Action<string> Notice { get; init; } = _ => { };

    /// <summary>The session is over for this participant; the argument says why.</summary>
    public Action<string> Ended { get; init; } = _ => { };
}

/// <summary>
/// Several N.E.O.s working on one app. The host holds the session; guests connect to it (a star:
/// the host forwards every change to the other guests). One participant changes the app at a
/// time — the host decides whose turn it is — and publishes the resulting
/// <see cref="SessionState"/> when done; everyone else applies it to their own copy of the app.
///
/// <para>Messages from the other participants are handled one after the other on the
/// <see cref="SynchronizationContext"/> the session was created with (the UI thread in the
/// hosts), so the app is only ever touched from there.</para>
/// </summary>
public sealed class SharedSession : IAsyncDisposable
{
    public const int ProtocolVersion = 1;
    public static TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);
    public static TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(25);
    public static TimeSpan TurnRequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    private sealed class Peer(IPeerLink link)
    {
        public IPeerLink Link { get; } = link;
        public string Name { get; set; } = "?";
        public bool Ready { get; set; }
        public DateTime LastHeard { get; set; } = DateTime.UtcNow;
    }

    private sealed record LinkClosed(string Reason) : SessionMessage;

    private readonly SessionCallbacks _callbacks;
    private readonly SynchronizationContext? _context;
    private readonly string _appVersion;
    private readonly string _framework;
    private readonly object _gate = new();
    private readonly List<Peer> _peers = new();
    private readonly Dictionary<uint, WebRtcPeerLink> _pendingInvites = new();
    private readonly Channel<(Peer Peer, SessionMessage Message)> _inbox = Channel.CreateUnbounded<(Peer, SessionMessage)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<int, TaskCompletionSource<TurnResult>> _pendingTurns = new();
    private readonly TaskCompletionSource<WelcomeMessage> _welcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebRtcPeerLink? _joinLink;
    private IReadOnlyList<string> _participants;
    private string? _turnHolder;        // host: who changes the app now (the host's own name included)
    private string _turnActivity = "";
    private int _nextRequestId;
    private bool _ended;

    private SharedSession(SessionRole role, string name, string appVersion, string framework, SessionCallbacks callbacks, SynchronizationContext? context)
    {
        Role = role;
        LocalName = string.IsNullOrWhiteSpace(name) ? (role == SessionRole.Host ? "Host" : "Guest") : name.Trim();
        _appVersion = appVersion;
        _framework = framework;
        _callbacks = callbacks;
        _context = context;
        _participants = [LocalName];

        if (_context != null) _context.Post(_ => _ = RunLoopAsync(), null);
        else _ = Task.Run(RunLoopAsync);
        _ = HeartbeatAsync(_stop.Token);
    }

    public SessionRole Role { get; }

    /// <summary>This participant's name (a guest's may get a suffix when it was taken).</summary>
    public string LocalName { get; private set; }

    public string? HostName { get; private set; }

    /// <summary>Everyone in the session, host first.</summary>
    public IReadOnlyList<string> Participants { get { lock (_gate) return _participants; } }

    public bool IsConnected { get { lock (_gate) return _peers.Any(p => p.Ready); } }

    /// <summary>The guest's reply code (after <see cref="JoinAsync"/>), to send back to the host.</summary>
    public string? ReplyCode => _joinLink?.Code;

    // ── Creating and joining ─────────────────────────────────────────────────

    public static SharedSession Host(string name, string appVersion, string framework, SessionCallbacks callbacks, SynchronizationContext? context = null)
        => new(SessionRole.Host, name, appVersion, framework, callbacks, context);

    /// <summary>Tests and other transports: a guest whose connection comes from <see cref="AttachHostAsync"/>.</summary>
    internal static SharedSession Guest(string name, string appVersion, string framework, SessionCallbacks callbacks, SynchronizationContext? context = null)
        => new(SessionRole.Guest, name, appVersion, framework, callbacks, context);

    /// <summary>A new invitation; send the returned code to the person you invite. Several may be open at once.</summary>
    public async Task<string> CreateInviteAsync(CancellationToken cancellationToken = default)
    {
        if (Role != SessionRole.Host) throw new InvalidOperationException("Only the host invites.");
        var link = await WebRtcPeerLink.CreateInviteAsync(cancellationToken: cancellationToken);
        lock (_gate) _pendingInvites[link.LocalCode.SessionId] = link;
        return link.Code;
    }

    /// <summary>Uses a guest's reply: connects, then the guest joins (it gets the session right away).</summary>
    public async Task AcceptReplyAsync(string replyCode, CancellationToken cancellationToken = default)
    {
        var reply = ConnectionCode.Decode(replyCode);
        WebRtcPeerLink? link;
        lock (_gate)
        {
            // One try per invitation: a failed attempt needs a fresh one anyway.
            if (_pendingInvites.Remove(reply.SessionId, out link) == false) link = null;
        }
        if (link == null)
            throw new FormatException(reply.Kind == ConnectionCodeKind.Invite
                ? "This is an invitation code. Paste the reply your guest sent back."
                : "This reply belongs to none of your open invitations. Create a new invitation.");

        // Listen before the connection opens: the guest says hello the moment it does.
        var peer = AttachGuest(link);
        try
        {
            await link.AcceptReplyAsync(replyCode, cancellationToken);
        }
        catch
        {
            lock (_gate) _peers.Remove((Peer)peer);
            link.Close("no connection");
            throw;
        }
    }

    /// <summary>Answers an invitation. Send <see cref="ReplyCode"/> back to the host, then <see cref="WaitJoinedAsync"/>.</summary>
    public static async Task<SharedSession> JoinAsync(string inviteCode, string name, string appVersion, string framework,
        SessionCallbacks callbacks, SynchronizationContext? context = null, CancellationToken cancellationToken = default)
    {
        var link = await WebRtcPeerLink.AnswerInviteAsync(inviteCode, cancellationToken: cancellationToken);
        var session = new SharedSession(SessionRole.Guest, name, appVersion, framework, callbacks, context) { _joinLink = link };
        return session;
    }

    /// <summary>Waits until the host has the reply and has let this guest in; throws with the reason otherwise.</summary>
    public async Task WaitJoinedAsync(CancellationToken cancellationToken = default)
    {
        if (_joinLink == null) throw new InvalidOperationException("Not joining.");
        var peer = AddHostPeer(_joinLink);
        await _joinLink.WaitOpenAsync(WebRtcPeerLink.ExchangeWindow, cancellationToken);
        await SendAsync(peer, new HelloMessage(LocalName, ProtocolVersion, _appVersion, _framework));
        await WaitWelcomeAsync(cancellationToken);
    }

    /// <summary>Tests and other transports: a guest connected by other means.</summary>
    internal object AttachGuest(IPeerLink link)
    {
        var peer = new Peer(link);
        lock (_gate) _peers.Add(peer);
        Wire(peer);
        return peer;
    }

    /// <summary>Tests and other transports: this guest's connection to the host.</summary>
    internal async Task AttachHostAsync(IPeerLink link)
    {
        var peer = AddHostPeer(link);
        await SendAsync(peer, new HelloMessage(LocalName, ProtocolVersion, _appVersion, _framework));
    }

    private Peer AddHostPeer(IPeerLink link)
    {
        var peer = new Peer(link) { Name = "host" };
        lock (_gate) _peers.Add(peer);
        Wire(peer);
        return peer;
    }

    internal async Task WaitWelcomeAsync(CancellationToken cancellationToken)
    {
        var done = await Task.WhenAny(_welcome.Task, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken));
        if (done != _welcome.Task)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The host did not let you in.");
        }
        await _welcome.Task; // throws with the host's reason when refused
    }

    private void Wire(Peer peer)
    {
        peer.Link.MessageReceived += data =>
        {
            peer.LastHeard = DateTime.UtcNow;
            var message = SessionMessage.FromBytes(data);
            if (message != null && message is not PingMessage)
                _inbox.Writer.TryWrite((peer, message));
        };
        peer.Link.Closed += reason => _inbox.Writer.TryWrite((peer, new LinkClosed(reason)));
    }

    // ── Turns ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks to change the app. Granted: go ahead, then <see cref="PublishAsync"/> and
    /// <see cref="ReleaseTurnAsync"/>. Refused: someone else is changing it (<see cref="TurnResult.Holder"/>).
    /// </summary>
    public async Task<TurnResult> TryTakeTurnAsync(string activity)
    {
        if (Role == SessionRole.Host)
        {
            lock (_gate)
            {
                if (_turnHolder != null && _turnHolder != LocalName)
                    return new TurnResult(false, _turnHolder, $"{_turnHolder} is changing the app right now.");
                _turnHolder = LocalName;
                _turnActivity = activity;
            }
            await BroadcastAsync(new TurnTakenMessage(LocalName, activity));
            return new TurnResult(true, LocalName, null);
        }

        var host = ReadyPeers().FirstOrDefault();
        if (host == null) return new TurnResult(false, null, "Not connected to the session.");
        var request = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int id;
        lock (_gate)
        {
            id = ++_nextRequestId;
            _pendingTurns[id] = request;
        }
        await SendAsync(host, new TurnRequestMessage(id, activity));
        var done = await Task.WhenAny(request.Task, Task.Delay(TurnRequestTimeout));
        lock (_gate) _pendingTurns.Remove(id);
        return done == request.Task
            ? await request.Task
            : new TurnResult(false, null, "The host did not answer.");
    }

    /// <summary>Sends the state after a change to everyone else.</summary>
    public Task PublishAsync(SessionState state) => BroadcastAsync(new StateMessage(state));

    /// <summary>Done changing the app; the others may go on.</summary>
    public async Task ReleaseTurnAsync()
    {
        if (Role == SessionRole.Host)
        {
            lock (_gate)
            {
                if (_turnHolder != LocalName) return;
                _turnHolder = null;
            }
        }
        await BroadcastAsync(new TurnReleasedMessage(LocalName));
    }

    // ── Leaving ──────────────────────────────────────────────────────────────

    /// <summary>Leaves the session (the host ends it for everyone).</summary>
    public async Task LeaveAsync(string? reason = null)
    {
        reason ??= Role == SessionRole.Host ? $"{LocalName} ended the session." : $"{LocalName} left the session.";
        try { await BroadcastAsync(new ByeMessage(reason)); } catch { /* leaving anyway */ }
        await Task.Delay(150); // let the goodbye go out before the connections close
        Shutdown(reason, raiseEnded: false);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_ended) await LeaveAsync();
    }

    private void Shutdown(string reason, bool raiseEnded)
    {
        List<Peer> peers;
        List<WebRtcPeerLink> pending;
        lock (_gate)
        {
            if (_ended) return;
            _ended = true;
            peers = _peers.ToList();
            _peers.Clear();
            pending = _pendingInvites.Values.ToList();
            _pendingInvites.Clear();
            foreach (var request in _pendingTurns.Values) request.TrySetResult(new TurnResult(false, null, reason));
            _pendingTurns.Clear();
        }
        _welcome.TrySetException(new InvalidOperationException(reason));
        _ = _welcome.Task.Exception;
        foreach (var p in peers) p.Link.Close(reason);
        foreach (var l in pending) l.Close(reason);
        _joinLink?.Close(reason);
        _stop.Cancel();
        _inbox.Writer.TryComplete();
        if (raiseEnded) _callbacks.Ended(reason);
    }

    // ── Message handling (one at a time, on the session's context) ───────────

    private async Task RunLoopAsync()
    {
        try
        {
            await foreach (var (peer, message) in _inbox.Reader.ReadAllAsync(_stop.Token))
            {
                try { await HandleAsync(peer, message); }
                catch (Exception ex) { _callbacks.Notice("Session: " + ex.Message); }
            }
        }
        catch (OperationCanceledException) { /* session over */ }
        catch (ChannelClosedException) { /* session over */ }
    }

    private async Task HandleAsync(Peer peer, SessionMessage message)
    {
        switch (message)
        {
            // Host side
            case HelloMessage hello when Role == SessionRole.Host:
                await WelcomeAsync(peer, hello);
                break;

            case TurnRequestMessage request when Role == SessionRole.Host:
            {
                bool granted;
                string? holder;
                lock (_gate)
                {
                    granted = _turnHolder == null;
                    if (granted) { _turnHolder = peer.Name; _turnActivity = request.Activity; }
                    holder = _turnHolder;
                }
                await SendAsync(peer, new TurnAnswerMessage(request.RequestId, granted, holder));
                if (granted)
                {
                    await BroadcastAsync(new TurnTakenMessage(peer.Name, request.Activity), except: peer);
                    _callbacks.TurnTaken(peer.Name, request.Activity);
                }
                break;
            }

            case StateMessage state when Role == SessionRole.Host:
                await BroadcastAsync(state, except: peer); // the other guests start on it right away
                await _callbacks.ApplyState(state.State);
                break;

            case TurnReleasedMessage released when Role == SessionRole.Host:
            {
                bool wasHolder;
                lock (_gate)
                {
                    wasHolder = _turnHolder == peer.Name;
                    if (wasHolder) _turnHolder = null;
                }
                if (wasHolder)
                {
                    await BroadcastAsync(new TurnReleasedMessage(peer.Name), except: peer);
                    _callbacks.TurnReleased(peer.Name);
                }
                break;
            }

            // Guest side
            case WelcomeMessage welcome when Role == SessionRole.Guest:
                LocalName = welcome.Name;
                HostName = welcome.HostName;
                peer.Name = welcome.HostName;
                peer.Ready = true;
                SetParticipants(welcome.Participants);
                _welcome.TrySetResult(welcome);
                break;

            case ParticipantsMessage participants when Role == SessionRole.Guest:
                SetParticipants(participants.Names);
                break;

            case TurnAnswerMessage answer when Role == SessionRole.Guest:
            {
                TaskCompletionSource<TurnResult>? request;
                lock (_gate) _pendingTurns.TryGetValue(answer.RequestId, out request);
                request?.TrySetResult(answer.Granted
                    ? new TurnResult(true, LocalName, null)
                    : new TurnResult(false, answer.Holder, $"{answer.Holder} is changing the app right now."));
                break;
            }

            case TurnTakenMessage taken when Role == SessionRole.Guest:
                _callbacks.TurnTaken(taken.Holder, taken.Activity);
                break;

            case TurnReleasedMessage released when Role == SessionRole.Guest:
                _callbacks.TurnReleased(released.Holder);
                break;

            case StateMessage state when Role == SessionRole.Guest:
                await _callbacks.ApplyState(state.State);
                break;

            // Both
            case ByeMessage bye:
                if (Role == SessionRole.Guest)
                {
                    _welcome.TrySetException(new InvalidOperationException(bye.Reason));
                    _ = _welcome.Task.Exception;
                    Shutdown(bye.Reason, raiseEnded: true);
                }
                else
                {
                    await PeerGoneAsync(peer, bye.Reason, announce: peer.Ready);
                }
                break;

            case LinkClosed closed:
                if (Role == SessionRole.Guest)
                {
                    if (!_ended)
                    {
                        string who = HostName ?? "the host";
                        Shutdown($"The connection to {who} is gone. ({closed.Reason})", raiseEnded: true);
                    }
                }
                else
                {
                    await PeerGoneAsync(peer, closed.Reason, announce: peer.Ready);
                }
                break;
        }
    }

    private async Task WelcomeAsync(Peer peer, HelloMessage hello)
    {
        string? problem = hello.Protocol != ProtocolVersion
            ? $"{hello.Name} uses a different N.E.O. version ({hello.AppVersion}) — both need the same one."
            : _callbacks.CheckGuest(hello);
        if (problem != null)
        {
            await SendAsync(peer, new ByeMessage(problem));
            await Task.Delay(150);
            lock (_gate) _peers.Remove(peer);
            peer.Link.Close(problem);
            _callbacks.Notice($"{hello.Name} could not join: {problem}");
            return;
        }

        IReadOnlyList<string> names;
        string? holder;
        string activity;
        lock (_gate)
        {
            peer.Name = UniqueName(hello.Name);
            peer.Ready = true;
            names = RebuildParticipants();
            holder = _turnHolder;
            activity = _turnActivity;
        }
        await SendAsync(peer, new WelcomeMessage(peer.Name, LocalName, names));
        if (_callbacks.CaptureState != null)
        {
            var state = await _callbacks.CaptureState();
            await SendAsync(peer, new StateMessage(state with { Kind = StateKinds.Initial }));
        }
        if (holder != null)
            await SendAsync(peer, new TurnTakenMessage(holder, activity));
        await BroadcastAsync(new ParticipantsMessage(names), except: peer);
        _callbacks.ParticipantsChanged(names);
        _callbacks.Notice($"{peer.Name} joined the session.");
    }

    private async Task PeerGoneAsync(Peer peer, string reason, bool announce)
    {
        bool wasHolder;
        IReadOnlyList<string> names;
        lock (_gate)
        {
            if (!_peers.Remove(peer)) return;
            wasHolder = peer.Ready && _turnHolder == peer.Name;
            if (wasHolder) _turnHolder = null;
            names = RebuildParticipants();
        }
        peer.Link.Close(reason);
        if (!announce) return;
        if (wasHolder)
        {
            await BroadcastAsync(new TurnReleasedMessage(peer.Name));
            _callbacks.TurnReleased(peer.Name);
        }
        await BroadcastAsync(new ParticipantsMessage(names));
        _callbacks.ParticipantsChanged(names);
        _callbacks.Notice($"{peer.Name} left the session.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string UniqueName(string wanted)
    {
        var name = string.IsNullOrWhiteSpace(wanted) ? "Guest" : wanted.Trim();
        var taken = _peers.Where(p => p.Ready).Select(p => p.Name).Append(LocalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (int n = 2; ; n++)
            if (!taken.Contains($"{name} ({n})")) return $"{name} ({n})";
    }

    private IReadOnlyList<string> RebuildParticipants()
    {
        _participants = new[] { LocalName }.Concat(_peers.Where(p => p.Ready).Select(p => p.Name)).ToList();
        return _participants;
    }

    private void SetParticipants(IReadOnlyList<string> names)
    {
        lock (_gate) _participants = names;
        _callbacks.ParticipantsChanged(names);
    }

    private List<Peer> ReadyPeers()
    {
        lock (_gate) return _peers.Where(p => p.Ready).ToList();
    }

    private Task BroadcastAsync(SessionMessage message, Peer? except = null)
        => Task.WhenAll(ReadyPeers().Where(p => p != except).Select(p => SendAsync(p, message)));

    private async Task SendAsync(Peer peer, SessionMessage message)
    {
        try { await peer.Link.SendAsync(message.ToBytes()); }
        catch (Exception ex) { peer.Link.Close("Sending failed: " + ex.Message); }
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, cancellationToken);
                List<Peer> peers;
                lock (_gate) peers = _peers.ToList();
                foreach (var peer in peers)
                {
                    if (DateTime.UtcNow - peer.LastHeard > HeartbeatTimeout)
                        peer.Link.Close($"No sign of life for {HeartbeatTimeout.TotalSeconds:0} seconds.");
                    else
                        _ = SendAsync(peer, new PingMessage());
                }
            }
        }
        catch (OperationCanceledException) { /* session over */ }
    }
}
