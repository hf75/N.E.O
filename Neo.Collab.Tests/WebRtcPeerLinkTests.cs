using FluentAssertions;
using Xunit;

namespace Neo.Collab.Tests;

/// <summary>
/// Real WebRTC between two ends in this process: the codes are exchanged as strings, exactly as
/// people would paste them, and the two connect over this machine's own addresses.
/// </summary>
[Collection("WebRTC")]
public class WebRtcPeerLinkTests
{
    [Fact]
    public async Task Two_ends_connect_with_an_invitation_and_a_reply_and_exchange_messages()
    {
        using var host = await WebRtcPeerLink.CreateInviteAsync();
        var invite = host.Code;
        invite.Should().StartWith("NEO1");

        using var guest = await WebRtcPeerLink.AnswerInviteAsync(invite);
        var reply = guest.Code;
        ConnectionCode.Decode(reply).Kind.Should().Be(ConnectionCodeKind.Reply);

        var atGuest = new List<byte[]>();
        var atHost = new List<byte[]>();
        guest.MessageReceived += m => { lock (atGuest) atGuest.Add(m); };
        host.MessageReceived += m => { lock (atHost) atHost.Add(m); };

        await host.AcceptReplyAsync(reply);
        await guest.WaitOpenAsync(TimeSpan.FromSeconds(20));
        host.IsOpen.Should().BeTrue();
        guest.IsOpen.Should().BeTrue();

        // A message far above the data channel's 256 KB limit, and a small one back.
        var random = new Random(3);
        var big = new byte[1_500_000];
        random.NextBytes(big);
        await host.SendAsync(big);
        await guest.SendAsync("hello host"u8.ToArray());

        await WaitUntil(() => { lock (atGuest) return atGuest.Count == 1; }, "the big message");
        await WaitUntil(() => { lock (atHost) return atHost.Count == 1; }, "the reply");
        atGuest[0].Should().Equal(big);
        System.Text.Encoding.UTF8.GetString(atHost[0]).Should().Be("hello host");
    }

    [Fact]
    public async Task The_reply_may_come_long_after_the_invitation()
    {
        // People copy codes by hand; ICE on its own gives up after 16 seconds.
        using var host = await WebRtcPeerLink.CreateInviteAsync();
        using var guest = await WebRtcPeerLink.AnswerInviteAsync(host.Code);
        await Task.Delay(TimeSpan.FromSeconds(25));

        await host.AcceptReplyAsync(guest.Code);
        await guest.WaitOpenAsync(TimeSpan.FromSeconds(20));
        host.IsOpen.Should().BeTrue();
    }

    [Fact]
    public async Task Wrong_codes_are_refused_with_a_reason()
    {
        using var host = await WebRtcPeerLink.CreateInviteAsync();
        using var otherHost = await WebRtcPeerLink.CreateInviteAsync();
        using var guest = await WebRtcPeerLink.AnswerInviteAsync(otherHost.Code);

        (await Assert.ThrowsAsync<FormatException>(() => host.AcceptReplyAsync(guest.Code))).Message.Should().Contain("different invitation");
        (await Assert.ThrowsAsync<FormatException>(() => host.AcceptReplyAsync(otherHost.Code))).Message.Should().Contain("invitation code");
        (await Assert.ThrowsAsync<FormatException>(() => WebRtcPeerLink.AnswerInviteAsync(guest.Code))).Message.Should().Contain("reply code");
    }

    [Fact]
    public async Task Closing_one_end_is_noticed_by_the_other()
    {
        using var host = await WebRtcPeerLink.CreateInviteAsync();
        using var guest = await WebRtcPeerLink.AnswerInviteAsync(host.Code);
        await host.AcceptReplyAsync(guest.Code);
        await guest.WaitOpenAsync(TimeSpan.FromSeconds(20));

        string? reason = null;
        guest.Closed += r => reason = r;
        host.Close("bye");

        await WaitUntil(() => reason != null, "the guest noticing");
        guest.IsOpen.Should().BeFalse();
    }

    internal static async Task WaitUntil(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        condition().Should().BeTrue("expected within {0} s: {1}", seconds, what);
    }
}
