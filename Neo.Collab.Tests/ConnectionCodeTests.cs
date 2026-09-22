using System.Net;
using FluentAssertions;
using Xunit;

namespace Neo.Collab.Tests;

public class ConnectionCodeTests
{
    private static ConnectionCode Sample(ConnectionCodeKind kind = ConnectionCodeKind.Invite) => new(
        kind,
        SessionId: 0xDEADBEEF,
        IceUser: "RVJB",
        IcePassword: "TBRENDCZPEAHQZHCYNUMVQFA",
        Fingerprint: Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray(),
        Setup: DtlsSetup.ActPass,
        Candidates:
        [
            new Candidate(CandidateType.ServerReflexive, IPAddress.Parse("109.75.92.211"), 21155),
            new Candidate(CandidateType.Host, IPAddress.Parse("192.168.178.49"), 58522),
            new Candidate(CandidateType.Host, IPAddress.Parse("2a02:5a0:445f:9640:607a:6e24:eb86:cec"), 58522),
        ]);

    [Fact]
    public void A_code_survives_the_round_trip_and_stays_short()
    {
        var code = Sample();
        var text = code.Encode();

        text.Should().StartWith("NEO1");
        text.Length.Should().BeLessThan(200, "people copy it into a chat");

        var back = ConnectionCode.Decode(text);
        back.Kind.Should().Be(ConnectionCodeKind.Invite);
        back.SessionId.Should().Be(0xDEADBEEF);
        back.IceUser.Should().Be("RVJB");
        back.IcePassword.Should().Be("TBRENDCZPEAHQZHCYNUMVQFA");
        back.Fingerprint.Should().Equal(code.Fingerprint);
        back.Setup.Should().Be(DtlsSetup.ActPass);
        back.Candidates.Should().BeEquivalentTo(code.Candidates);
    }

    [Fact]
    public void Line_breaks_and_spaces_from_a_chat_window_do_not_matter()
    {
        var text = Sample().Encode();
        var mangled = "  " + text[..40] + "\r\n" + text[40..90] + " " + text[90..] + "\n";
        ConnectionCode.Decode(mangled).SessionId.Should().Be(0xDEADBEEF);
    }

    [Fact]
    public void A_damaged_or_foreign_code_says_so()
    {
        var text = Sample().Encode();
        var damaged = text[..60] + (text[60] == 'A' ? 'B' : 'A') + text[61..];

        Assert.Throws<FormatException>(() => ConnectionCode.Decode(damaged)).Message.Should().Contain("damaged");
        Assert.Throws<FormatException>(() => ConnectionCode.Decode(text[..50])).Message.Should().MatchRegex("damaged|incomplete");
        Assert.Throws<FormatException>(() => ConnectionCode.Decode("hello")).Message.Should().Contain("NEO1");
        Assert.Throws<FormatException>(() => ConnectionCode.Decode("   ")).Message.Should().Contain("empty");
    }

    [Fact]
    public void The_description_carries_what_the_connection_needs()
    {
        var code = Sample(ConnectionCodeKind.Reply) with { Setup = DtlsSetup.Active };
        var sdp = code.ToSdp();
        sdp.Should().Contain("a=ice-ufrag:RVJB").And.Contain("a=ice-pwd:TBRENDCZPEAHQZHCYNUMVQFA").And.Contain("a=setup:active")
           .And.Contain("m=application 9 UDP/DTLS/SCTP webrtc-datachannel");
        sdp.Should().Contain("a=fingerprint:sha-256 00:07:0E:15");

        var lines = code.CandidateLines().ToList();
        lines.Should().HaveCount(3);
        lines[0].Should().Contain("109.75.92.211 21155 typ srflx");
        lines[1].Should().Contain("192.168.178.49 58522 typ host");

        var parsed = ConnectionCode.FromSdp(ConnectionCodeKind.Reply, 7, sdp, code.Candidates);
        parsed.Fingerprint.Should().Equal(code.Fingerprint);
        parsed.Setup.Should().Be(DtlsSetup.Active);
    }

    [Fact]
    public void Only_addresses_that_help_another_machine_go_into_a_code()
    {
        var picked = WebRtcPeerLink.SelectCandidates(
        [
            new Candidate(CandidateType.Host, IPAddress.Loopback, 1),
            new Candidate(CandidateType.Host, IPAddress.Parse("169.254.10.1"), 2),
            new Candidate(CandidateType.Host, IPAddress.Parse("fe80::1"), 3),
            new Candidate(CandidateType.Host, IPAddress.Parse("fd65:5a0c:15b9::1"), 4),
            new Candidate(CandidateType.Host, IPAddress.Parse("192.168.1.2"), 5),
            new Candidate(CandidateType.Host, IPAddress.Parse("2a02:5a0::1"), 6),
            new Candidate(CandidateType.ServerReflexive, IPAddress.Parse("109.75.92.211"), 7),
            new Candidate(CandidateType.ServerReflexive, IPAddress.Parse("109.75.92.211"), 7),
        ]);

        picked.Select(c => c.Port).Should().Equal(7, 5, 6);
    }
}

public class MessageFramerTests
{
    [Fact]
    public void Large_messages_travel_in_pieces_and_arrive_whole()
    {
        var framer = new MessageFramer();
        var random = new Random(1);
        var text = string.Concat(Enumerable.Range(0, 200_000).Select(i => (char)('a' + random.Next(26))));
        var message = System.Text.Encoding.UTF8.GetBytes(text);

        var pieces = framer.Split(message).ToList();
        pieces.Should().HaveCountGreaterThan(1);
        pieces.Should().OnlyContain(p => p.Length <= MessageFramer.MaxPiece + 8);

        var receiver = new MessageFramer();
        byte[]? whole = null;
        foreach (var p in pieces) whole = receiver.Add(p) ?? whole;
        whole.Should().Equal(message);
    }

    [Fact]
    public void Source_code_shrinks_on_the_way()
    {
        var code = string.Concat(Enumerable.Repeat("public class DynamicUserControl : UserControl { private void BuildUi() { } }\n", 2000));
        var bytes = System.Text.Encoding.UTF8.GetBytes(code);
        var pieces = new MessageFramer().Split(bytes).ToList();
        pieces.Sum(p => p.Length).Should().BeLessThan(bytes.Length / 10);
    }
}
