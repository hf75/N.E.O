using System.Collections.Concurrent;
using FluentAssertions;
using Xunit;

namespace Neo.Collab.Tests;

/// <summary>
/// The session rules with connections in memory: who may change the app, who gets which change,
/// what happens when someone joins, leaves or does not fit. One test runs over real WebRTC.
/// </summary>
public class SharedSessionTests
{
    /// <summary>One N.E.O. in the session, as far as the session can see it: what it was told.</summary>
    private sealed class Participant
    {
        public string Name = "";
        public string Framework = "avalonia";
        public string Code = "";
        public readonly ConcurrentQueue<SessionState> Applied = new();
        public readonly ConcurrentQueue<string> Events = new();
        public readonly ConcurrentQueue<TeleportMessage> Teleports = new();
        public IReadOnlyList<string> Participants = [];
        public string? Ended;

        public SessionCallbacks Callbacks => new()
        {
            ApplyState = s => { Applied.Enqueue(s); if (s.Code != null) Code = s.Code; return Task.CompletedTask; },
            CaptureState = () => Task.FromResult(State("capture", Code)),
            CheckGuest = hello => hello.Framework == Framework ? null : $"{hello.Name} works with {hello.Framework}, this session with {Framework}.",
            TurnTaken = (who, what) => Events.Enqueue($"taken:{who}:{what}"),
            TurnReleased = who => Events.Enqueue($"released:{who}"),
            ParticipantsChanged = names => Participants = names,
            Teleport = m => { Teleports.Enqueue(m); return Task.CompletedTask; },
            Notice = line => Events.Enqueue("notice:" + line),
            Ended = reason => Ended = reason,
        };

        public SessionState State(string kind, string code) =>
            new(kind, Name, kind, [new ChatLine("prompt", "do " + kind, false)], code, "history of " + code, new Dictionary<string, string>(), Framework);
    }

    private static Task<(SharedSession Host, Participant HostSide)> StartHost(string name = "Anna", string code = "v1")
    {
        var p = new Participant { Name = name, Code = code };
        return Task.FromResult((SharedSession.Host(name, "1.0", p.Framework, p.Callbacks), p));
    }

    private static async Task<(SharedSession Guest, Participant GuestSide)> Join(SharedSession host, string name, string framework = "avalonia")
    {
        var p = new Participant { Name = name, Framework = framework };
        var guest = SharedSession.Guest(name, "1.0", framework, p.Callbacks);
        var (toGuest, toHost) = InMemoryPeerLink.CreatePair();
        host.AttachGuest(toGuest);
        await guest.AttachHostAsync(toHost);
        await guest.WaitWelcomeAsync(CancellationToken.None);
        return (guest, p);
    }

    private static Task WaitUntil(Func<bool> condition, string what) => WebRtcPeerLinkTests.WaitUntil(condition, what, 10);

    [Fact]
    public async Task Someone_who_joins_gets_the_hosts_app_right_away()
    {
        var (host, anna) = await StartHost(code: "the app so far");
        var (ben, benSide) = await Join(host, "Ben");

        await WaitUntil(() => benSide.Applied.Count == 1, "the host's session");
        var first = benSide.Applied.Single();
        first.Kind.Should().Be(StateKinds.Initial);
        first.Code.Should().Be("the app so far");
        first.History.Should().Be("history of the app so far");

        ben.HostName.Should().Be("Anna");
        await WaitUntil(() => anna.Participants.Count == 2, "the host's list");
        anna.Participants.Should().Equal("Anna", "Ben");
        benSide.Participants.Should().Equal("Anna", "Ben");
        anna.Events.Should().Contain("notice:Ben joined the session.");
    }

    [Fact]
    public async Task One_person_changes_the_app_at_a_time_and_everyone_else_gets_the_result()
    {
        var (host, anna) = await StartHost();
        var (ben, benSide) = await Join(host, "Ben");
        var (cleo, cleoSide) = await Join(host, "Cleo");
        await WaitUntil(() => benSide.Applied.Count == 1 && cleoSide.Applied.Count == 1, "both joined");

        // Ben's turn.
        (await ben.TryTakeTurnAsync("prompt: add a chart")).Granted.Should().BeTrue();
        await WaitUntil(() => anna.Events.Contains("taken:Ben:prompt: add a chart") && cleoSide.Events.Contains("taken:Ben:prompt: add a chart"), "the others know");

        var refusedHost = await host.TryTakeTurnAsync("prompt: something else");
        refusedHost.Granted.Should().BeFalse();
        refusedHost.Holder.Should().Be("Ben");
        (await cleo.TryTakeTurnAsync("edit")).Holder.Should().Be("Ben");

        await ben.PublishAsync(benSide.State(StateKinds.Prompt, "v2 by Ben"));
        await ben.ReleaseTurnAsync();
        await WaitUntil(() => anna.Code == "v2 by Ben" && cleoSide.Code == "v2 by Ben", "the change everywhere");
        await WaitUntil(() => anna.Events.Contains("released:Ben") && cleoSide.Events.Contains("released:Ben"), "the turn freed");
        benSide.Applied.Should().HaveCount(1, "a change is not sent back to its author");

        // Now the host.
        (await host.TryTakeTurnAsync("prompt: dark mode")).Granted.Should().BeTrue();
        await WaitUntil(() => benSide.Events.Contains("taken:Anna:prompt: dark mode"), "guests see the host at work");
        await host.PublishAsync(anna.State(StateKinds.Prompt, "v3 by Anna"));
        await host.ReleaseTurnAsync();
        await WaitUntil(() => benSide.Code == "v3 by Anna" && cleoSide.Code == "v3 by Anna", "the host's change everywhere");
        await WaitUntil(() => cleoSide.Events.Contains("released:Anna"), "the host's turn freed");

        (await cleo.TryTakeTurnAsync("edit")).Granted.Should().BeTrue("nobody holds the turn any more");
        await cleo.ReleaseTurnAsync();
    }

    [Fact]
    public async Task The_turn_is_freed_when_its_holder_drops_out()
    {
        var (host, anna) = await StartHost();
        var (ben, _) = await Join(host, "Ben");
        (await ben.TryTakeTurnAsync("prompt")).Granted.Should().BeTrue();

        await ben.LeaveAsync();
        await WaitUntil(() => anna.Events.Contains("released:Ben"), "the turn freed");
        await WaitUntil(() => anna.Events.Contains("notice:Ben left the session."), "the notice");
        await WaitUntil(() => anna.Participants.SequenceEqual(new[] { "Anna" }), "the list");
        (await host.TryTakeTurnAsync("prompt")).Granted.Should().BeTrue();
    }

    [Fact]
    public async Task A_guest_on_another_framework_is_turned_away_with_the_reason()
    {
        var (host, anna) = await StartHost();
        var p = new Participant { Name = "Ben", Framework = "wpf" };
        var ben = SharedSession.Guest("Ben", "1.0", "wpf", p.Callbacks);
        var (toGuest, toHost) = InMemoryPeerLink.CreatePair();
        host.AttachGuest(toGuest);
        await ben.AttachHostAsync(toHost);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ben.WaitWelcomeAsync(CancellationToken.None));
        refused.Message.Should().Contain("Ben works with wpf, this session with avalonia");
        await WaitUntil(() => anna.Events.Any(e => e.StartsWith("notice:Ben could not join")), "the host's notice");
        host.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task Two_people_with_the_same_name_can_be_told_apart()
    {
        var (host, anna) = await StartHost();
        var (ben1, _) = await Join(host, "Ben");
        var (ben2, _) = await Join(host, "ben");
        ben1.LocalName.Should().Be("Ben");
        ben2.LocalName.Should().Be("ben (2)");
        await WaitUntil(() => anna.Participants.Count == 3, "the list");
        anna.Participants.Should().Equal("Anna", "Ben", "ben (2)");
    }

    [Fact]
    public async Task A_message_about_the_running_app_reaches_only_the_person_it_is_for()
    {
        var (host, anna) = await StartHost();
        var (ben, benSide) = await Join(host, "Ben");
        var (_, caraSide) = await Join(host, "Cara");

        await host.SendTeleportAsync(new TeleportMessage(TeleportPhases.Offer, "1", "Anna", "Ben"));
        await ben.SendTeleportAsync(new TeleportMessage(TeleportPhases.Answer, "1", "Ben", "Anna") { Ok = true });
        await ben.SendTeleportAsync(new TeleportMessage(TeleportPhases.State, "2", "Ben", "Cara") { Image = "the data", CodeHash = "version" });

        await WaitUntil(() => benSide.Teleports.Count == 1 && anna.Teleports.Count == 1 && caraSide.Teleports.Count == 1, "all three messages");
        benSide.Teleports.Single().Phase.Should().Be(TeleportPhases.Offer);
        anna.Teleports.Single().Should().Match<TeleportMessage>(m => m.Phase == TeleportPhases.Answer && m.Ok);
        caraSide.Teleports.Single().Should().Match<TeleportMessage>(m => m.Phase == TeleportPhases.State && m.From == "Ben" && m.Image == "the data" && m.CodeHash == "version");
    }

    [Fact]
    public async Task Someone_who_has_left_cannot_answer_so_the_host_does()
    {
        var (host, anna) = await StartHost();
        var (ben, benSide) = await Join(host, "Ben");
        var (cara, _) = await Join(host, "Cara");
        await cara.LeaveAsync();
        await WaitUntil(() => anna.Participants.Count == 2, "the host's list without Cara");

        await ben.SendTeleportAsync(new TeleportMessage(TeleportPhases.Offer, "1", "Ben", "Cara"));
        await ben.SendTeleportAsync(new TeleportMessage(TeleportPhases.State, "2", "Ben", "Cara") { Image = "the data" });

        await WaitUntil(() => benSide.Teleports.Count == 2, "both answers");
        benSide.Teleports.Should().OnlyContain(m => !m.Ok && m.Reason!.Contains("not in the session") && m.From == "Cara");
        benSide.Teleports.Select(m => m.Phase).Should().Equal(TeleportPhases.Answer, TeleportPhases.Arrived);
        anna.Teleports.Should().BeEmpty();
        await host.Invoking(h => h.SendTeleportAsync(new TeleportMessage(TeleportPhases.Offer, "3", "Anna", "Cara"))).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task When_the_host_ends_the_session_the_guests_are_told()
    {
        var (host, _) = await StartHost();
        var (_, benSide) = await Join(host, "Ben");
        await host.LeaveAsync();
        await WaitUntil(() => benSide.Ended != null, "the guest's end");
        benSide.Ended.Should().Contain("Anna ended the session");
    }

    [Fact]
    [Trait("Category", "WebRTC")]
    public async Task A_whole_session_over_real_WebRTC()
    {
        var (host, anna) = await StartHost(code: "v1");
        var invite = await host.CreateInviteAsync();

        var benSide = new Participant { Name = "Ben" };
        var ben = await SharedSession.JoinAsync(invite, "Ben", "1.0", "avalonia", benSide.Callbacks);
        ben.ReplyCode.Should().StartWith("NEO1");

        var joined = ben.WaitJoinedAsync();
        await host.AcceptReplyAsync(ben.ReplyCode!);
        await joined;

        await WaitUntil(() => benSide.Code == "v1", "the host's app at the guest");
        (await ben.TryTakeTurnAsync("prompt")).Granted.Should().BeTrue();
        await ben.PublishAsync(benSide.State(StateKinds.Prompt, "v2 over the internet"));
        await ben.ReleaseTurnAsync();
        await WaitUntil(() => anna.Code == "v2 over the internet", "the guest's change at the host");

        await host.LeaveAsync();
        await WaitUntil(() => benSide.Ended != null, "the end");
    }
}
