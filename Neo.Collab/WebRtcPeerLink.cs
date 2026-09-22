using System.Net;
using System.Net.Sockets;
using SIPSorcery.Net;

namespace Neo.Collab;

/// <summary>
/// A direct, encrypted connection to another N.E.O. over the internet — a WebRTC data channel,
/// set up without any server of our own: the two sides exchange a <see cref="ConnectionCode"/>
/// each (invitation, then reply) and punch through their routers from the inside. Free public
/// STUN servers only tell each side its public address.
///
/// <para>Codes travel by hand, so the reply can come minutes after the invitation. Both sides keep
/// the holes in their routers open meanwhile: the guest's ICE agent keeps sending its checks
/// (the give-up time is raised from 16 seconds to 5 minutes), the host refreshes its public
/// address with the STUN server until the reply is pasted.</para>
/// </summary>
public sealed class WebRtcPeerLink : IPeerLink
{
    /// <summary>Public STUN servers: they only answer "what is my public address?"; no data passes them.</summary>
    public static readonly IReadOnlyList<string> DefaultStunServers = ["stun:stun.l.google.com:19302", "stun:stun.cloudflare.com:3478"];

    /// <summary>How long a connection attempt keeps going while the codes are exchanged.</summary>
    public static readonly TimeSpan ExchangeWindow = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan GatherTimeout = TimeSpan.FromSeconds(5);
    private const int MaxHostCandidatesV4 = 3, MaxHostCandidatesV6 = 2, MaxCandidates = 8;
    private const ulong SendBufferLimit = 1024 * 1024;

    private readonly RTCPeerConnection _pc;
    private readonly IReadOnlyList<string> _stunServers;
    private readonly MessageFramer _framer = new();
    private readonly object _receiveGate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _keepAlive = new();
    private readonly List<RTCIceCandidate> _gathered = new();
    private RTCDataChannel? _channel;
    private int _closed;

    static WebRtcPeerLink()
    {
        // ICE gives up after 16 seconds without a working address pair; exchanging codes by hand
        // takes longer. The agent keeps sending its checks for this long, which also keeps the
        // guest's router open for the host. A connection that stood and then broke is noticed by
        // the session's heartbeat, not by this timeout.
        if (RtpIceChannel.FAILED_TIMEOUT_PERIOD < (int)ExchangeWindow.TotalSeconds)
            RtpIceChannel.FAILED_TIMEOUT_PERIOD = (int)ExchangeWindow.TotalSeconds;
    }

    private WebRtcPeerLink(IReadOnlyList<string>? stunServers)
    {
        _stunServers = stunServers ?? DefaultStunServers;
        var config = new RTCConfiguration
        {
            iceServers = _stunServers.Select(u => new RTCIceServer { urls = u }).ToList(),
        };
        _pc = new RTCPeerConnection(config);
        _pc.onicecandidate += c => { if (c != null) lock (_gathered) _gathered.Add(c); };
        _pc.onconnectionstatechange += state =>
        {
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed && _open.Task.IsCompleted)
                OnClosed(state == RTCPeerConnectionState.failed ? "The connection broke." : "The connection was closed.");
        };
    }

    /// <summary>The code to hand to the other side: the invitation (host) or the reply (guest).</summary>
    public ConnectionCode LocalCode { get; private set; } = null!;

    public string Code => LocalCode.Encode();

    public bool IsOpen => _open.Task.IsCompletedSuccessfully && Volatile.Read(ref _closed) == 0;

    public event Action<byte[]>? MessageReceived;
    public event Action<string>? Closed;

    // ── Host: invitation, then the guest's reply ─────────────────────────────

    /// <summary>Starts a connection and returns it with its invitation code (<see cref="Code"/>).</summary>
    public static async Task<WebRtcPeerLink> CreateInviteAsync(IReadOnlyList<string>? stunServers = null, CancellationToken cancellationToken = default)
    {
        var link = new WebRtcPeerLink(stunServers);
        try
        {
            link.Attach(await link._pc.createDataChannel("neo", null));
            await link._pc.setLocalDescription(link._pc.createOffer(null));
            var candidates = await link.GatherAsync(cancellationToken);
            uint sessionId = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
            link.LocalCode = ConnectionCode.FromSdp(ConnectionCodeKind.Invite, sessionId, link._pc.localDescription.sdp.ToString(), candidates);
            _ = link.KeepPublicAddressAsync(link._keepAlive.Token);
            return link;
        }
        catch
        {
            link.Close("setup failed");
            throw;
        }
    }

    /// <summary>Uses the guest's reply and waits until the connection is open.</summary>
    public async Task AcceptReplyAsync(string replyCode, CancellationToken cancellationToken = default)
    {
        var reply = ConnectionCode.Decode(replyCode);
        if (reply.Kind != ConnectionCodeKind.Reply)
            throw new FormatException("This is an invitation code. Paste the reply your guest sent back.");
        if (reply.SessionId != LocalCode.SessionId)
            throw new FormatException("This reply belongs to a different invitation.");
        ApplyRemote(reply, RTCSdpType.answer);
        await WaitOpenAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }

    // ── Guest: the host's invitation, then a reply ───────────────────────────

    /// <summary>Answers an invitation; the reply to send back is <see cref="Code"/>. Then <see cref="WaitOpenAsync"/>.</summary>
    public static async Task<WebRtcPeerLink> AnswerInviteAsync(string inviteCode, IReadOnlyList<string>? stunServers = null, CancellationToken cancellationToken = default)
    {
        var invite = ConnectionCode.Decode(inviteCode);
        if (invite.Kind != ConnectionCodeKind.Invite)
            throw new FormatException("This is a reply code. Paste the invitation you received.");
        var link = new WebRtcPeerLink(stunServers);
        try
        {
            link._pc.ondatachannel += dc => link.Attach(dc);
            link.ApplyRemote(invite, RTCSdpType.offer);
            await link._pc.setLocalDescription(link._pc.createAnswer(null));
            var candidates = await link.GatherAsync(cancellationToken);
            link.LocalCode = ConnectionCode.FromSdp(ConnectionCodeKind.Reply, invite.SessionId, link._pc.localDescription.sdp.ToString(), candidates);
            return link;
        }
        catch
        {
            link.Close("setup failed");
            throw;
        }
    }

    /// <summary>Waits until the data channel is open; throws <see cref="TimeoutException"/> otherwise.</summary>
    public async Task WaitOpenAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var done = await Task.WhenAny(_open.Task, Task.Delay(timeout, cancellationToken));
        if (done != _open.Task)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("No connection came about. One of the two networks may block direct connections (company, school or mobile networks often do).");
        }
        await _open.Task;
    }

    // ── Sending and receiving ────────────────────────────────────────────────

    public async Task SendAsync(byte[] message)
    {
        var channel = _channel;
        if (!IsOpen || channel == null) throw new InvalidOperationException("The connection is closed.");
        await _sendGate.WaitAsync();
        try
        {
            foreach (var piece in _framer.Split(message))
            {
                // Large messages (a long chat history) go out in pieces; let the transport drain.
                while (channel.bufferedAmount > SendBufferLimit && IsOpen)
                    await Task.Delay(10);
                channel.send(piece, 0, piece.Length);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void Attach(RTCDataChannel channel)
    {
        _channel = channel;
        channel.onopen += () => { _keepAlive.Cancel(); _open.TrySetResult(); };
        channel.onclose += () => OnClosed("The other side left.");
        channel.onmessage += (_, _, data) =>
        {
            byte[]? whole;
            lock (_receiveGate) whole = _framer.Add(data);
            if (whole != null) MessageReceived?.Invoke(whole);
        };
        if (channel.readyState == RTCDataChannelState.open)
        {
            _keepAlive.Cancel();
            _open.TrySetResult();
        }
    }

    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _keepAlive.Cancel();
        try { _channel?.close(); } catch { /* already gone */ }
        try { _pc.close(); } catch { /* already gone */ }
        _open.TrySetException(new InvalidOperationException("The connection was closed: " + reason));
        _ = _open.Task.Exception; // observed
        Task.Run(() => Closed?.Invoke(reason));
    }

    private void OnClosed(string reason) => Close(reason);

    public void Dispose() => Close("closed");

    // ── Setup helpers ────────────────────────────────────────────────────────

    private void ApplyRemote(ConnectionCode code, RTCSdpType type)
    {
        var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = type, sdp = code.ToSdp() });
        if (result != SetDescriptionResultEnum.OK)
            throw new FormatException("The code could not be used (" + result + ").");
        foreach (var line in code.CandidateLines())
            _pc.addIceCandidate(new RTCIceCandidateInit { candidate = line, sdpMid = "0", sdpMLineIndex = 0, usernameFragment = code.IceUser });
    }

    /// <summary>Waits for this side's addresses (the public one comes from STUN) and picks the ones worth a code.</summary>
    private async Task<IReadOnlyList<Candidate>> GatherAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + GatherTimeout;
        while (_pc.iceGatheringState != RTCIceGatheringState.complete && DateTime.UtcNow < deadline)
            await Task.Delay(50, cancellationToken);

        var all = new List<RTCIceCandidate>();
        lock (_gathered) all.AddRange(_gathered);
        try { all.AddRange(_pc.GetRtpChannel()?.Candidates ?? []); } catch { /* not available yet */ }
        return SelectCandidates(all.Select(ToCandidate).OfType<Candidate>());
    }

    private static Candidate? ToCandidate(RTCIceCandidate c)
    {
        if (c.protocol != RTCIceProtocol.udp || !IPAddress.TryParse(c.address, out var address)) return null;
        var type = c.type switch
        {
            RTCIceCandidateType.host => CandidateType.Host,
            RTCIceCandidateType.srflx => CandidateType.ServerReflexive,
            RTCIceCandidateType.prflx => CandidateType.PeerReflexive,
            RTCIceCandidateType.relay => CandidateType.Relay,
            _ => (CandidateType?)null,
        };
        return type == null ? null : new Candidate(type.Value, address, c.port);
    }

    /// <summary>
    /// The public address first (that is what works across the internet), then a few local ones
    /// (same network) and globally routable IPv6 addresses. Loopback, link-local and unique-local
    /// addresses help nobody on another machine and only make the code longer.
    /// </summary>
    internal static IReadOnlyList<Candidate> SelectCandidates(IEnumerable<Candidate> candidates)
    {
        var distinct = candidates
            .Where(c => !IPAddress.IsLoopback(c.Address) && !IsLinkLocal(c.Address) && !IsUniqueLocalV6(c.Address))
            .DistinctBy(c => (c.Type, c.Address.ToString(), c.Port))
            .ToList();
        var result = new List<Candidate>();
        result.AddRange(distinct.Where(c => c.Type != CandidateType.Host));
        result.AddRange(distinct.Where(c => c.Type == CandidateType.Host && c.Address.AddressFamily == AddressFamily.InterNetwork).Take(MaxHostCandidatesV4));
        result.AddRange(distinct.Where(c => c.Type == CandidateType.Host && c.Address.AddressFamily == AddressFamily.InterNetworkV6).Take(MaxHostCandidatesV6));
        return result.Take(MaxCandidates).ToList();
    }

    private static bool IsLinkLocal(IPAddress a) =>
        a.IsIPv6LinkLocal || (a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is [169, 254, ..]);

    private static bool IsUniqueLocalV6(IPAddress a) =>
        a.AddressFamily == AddressFamily.InterNetworkV6 && (a.GetAddressBytes()[0] & 0xFE) == 0xFC;

    /// <summary>
    /// Until the reply is pasted, ask the STUN servers for our address now and then: the answer is
    /// not needed, the traffic keeps the router's mapping for the public address in the invitation.
    /// </summary>
    private async Task KeepPublicAddressAsync(CancellationToken cancellationToken)
    {
        try
        {
            var servers = new List<IPEndPoint>();
            foreach (var uri in _stunServers)
            {
                var hostPort = uri.Replace("stun:", "", StringComparison.OrdinalIgnoreCase);
                var parts = hostPort.Split(':');
                int port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 3478;
                try
                {
                    foreach (var address in await Dns.GetHostAddressesAsync(parts[0], AddressFamily.InterNetwork, cancellationToken))
                        servers.Add(new IPEndPoint(address, port));
                }
                catch (SocketException) { /* this server cannot be resolved; the others may */ }
            }

            var deadline = DateTime.UtcNow + ExchangeWindow;
            while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                var channel = _pc.GetRtpChannel();
                if (channel == null || channel.IsClosed) return;
                foreach (var server in servers)
                {
                    try { channel.Send(RTPChannelSocketsEnum.RTP, server, StunBindingRequest()); }
                    catch { /* best effort */ }
                }
            }
        }
        catch (OperationCanceledException) { /* connected or closed */ }
        catch { /* keep-alive is best effort */ }
    }

    private static byte[] StunBindingRequest()
    {
        var message = new byte[20];
        message[1] = 0x01;                        // Binding Request, no attributes
        message[4] = 0x21; message[5] = 0x12; message[6] = 0xA4; message[7] = 0x42; // magic cookie
        Random.Shared.NextBytes(message.AsSpan(8, 12)); // transaction id
        return message;
    }
}
