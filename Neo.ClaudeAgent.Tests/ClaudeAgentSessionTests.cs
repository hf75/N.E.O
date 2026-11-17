using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Neo.ClaudeAgent.Tests;

/// <summary>The session against a stand-in for Claude Code (<see cref="StandIn"/>): the whole protocol, nothing spent.</summary>
public class ClaudeAgentSessionTests : IDisposable
{
    private readonly StandIn _standIn = new();
    private readonly ConcurrentQueue<ClaudeAgentEvent> _events = new();

    public void Dispose() => _standIn.Dispose();

    // Windows PowerShell plays Claude Code, so every test returns early elsewhere.
    private async Task<ClaudeAgentSession> StartAsync(Func<ClaudeAgentOptions, ClaudeAgentOptions>? adjust = null)
    {
        var options = new ClaudeAgentOptions
        {
            CliPath = _standIn.Script,
            WorkingDirectory = _standIn.Folder,
            Environment = _standIn.Environment,
        };
        var session = await ClaudeAgentSession.StartAsync(adjust?.Invoke(options) ?? options);
        session.EventReceived += _events.Enqueue;
        return session;
    }

    [Fact]
    public async Task A_turn_streams_its_words_and_ends_with_what_it_cost()
    {
        if (!OperatingSystem.IsWindows())
            return;
        await using var session = await StartAsync();

        var first = await session.SendAsync("hello");
        var second = await session.SendAsync("hello");

        first.Succeeded.Should().BeTrue();
        first.Text.Should().Be("Hello");
        first.CostUsd.Should().BeApproximately(0.25, 1e-9);
        first.ApiDuration.Should().Be(TimeSpan.FromMilliseconds(40));
        first.Duration.Should().Be(TimeSpan.FromMilliseconds(30));
        first.Rounds.Should().Be(2);
        first.Usage.Should().Be(new ClaudeAgentUsage(3, 5, 7, 11));
        first.PermissionDenials.Should().Equal("Bash");
        second.CostUsd.Should().BeApproximately(0.25, 1e-9, "Claude Code counts the session; a turn gets the difference");
        second.ApiDuration.Should().Be(TimeSpan.FromMilliseconds(40));
        second.SessionCostUsd.Should().BeApproximately(0.5, 1e-9);
        session.CostUsd.Should().BeApproximately(0.5, 1e-9);
        session.SessionId.Should().Be("session-1");
        session.Ready!.Model.Should().Be("stand-in");
        session.Ready.ClaudeCodeVersion.Should().Be("9.9.9");
        session.Ready.McpServers.Should().Equal(new ClaudeAgentMcpServerStatus("neo", "connected"));
        _events.OfType<ClaudeAgentTextDelta>().Select(delta => delta.Text).Take(2).Should().Equal("Hel", "lo");
        _events.OfType<ClaudeAgentText>().First().Text.Should().Be("Hello");
        _events.OfType<ClaudeAgentTurnEnded>().Should().HaveCount(2);
    }

    [Fact]
    public async Task Claude_Code_is_started_as_the_official_SDKs_start_it()
    {
        if (!OperatingSystem.IsWindows())
            return;
        await using var session = await StartAsync(options => new ClaudeAgentOptions
        {
            CliPath = options.CliPath,
            WorkingDirectory = options.WorkingDirectory,
            Environment = options.Environment,
            Model = "claude-haiku-5-5",
            SystemPrompt = "Kurz.",
            Restricted = true,
            ToolSearch = false,
            PermissionHandler = (_, _) => Task.FromResult(ClaudeAgentPermission.Allow()),
            Tools = [Echo()],
        });
        await session.SendAsync("hello");

        var arguments = File.ReadAllLines(_standIn.ArgumentsFile);
        arguments.Should().ContainInConsecutiveOrder("-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose");
        arguments.Should().ContainInConsecutiveOrder("--model", "claude-haiku-5-5");
        arguments.Should().ContainInConsecutiveOrder("--permission-prompt-tool", "stdio");
        arguments.Should().Contain(["--restricted", "--strict-mcp-config", "--no-session-persistence"]);
        var config = arguments[Array.IndexOf(arguments, "--mcp-config") + 1];
        JsonDocument.Parse(config).RootElement.GetProperty("mcpServers").GetProperty("neo").GetProperty("type").GetString().Should().Be("sdk");
        File.ReadAllText(_standIn.EnvironmentFile).Should().Be("ENABLE_TOOL_SEARCH=false;KEY=test-key");
        session.Initialization!["servers"]!.GetValue<string>().Should().Be("neo", "initialize names our in-process server");
    }

    [Fact]
    public async Task Tools_are_reported_when_they_start_and_when_they_are_done()
    {
        if (!OperatingSystem.IsWindows())
            return;
        await using var session = await StartAsync();

        await session.SendAsync("tool");

        var started = _events.OfType<ClaudeAgentToolStarted>().Single();
        started.Name.Should().Be("Write");
        started.Input.GetProperty("file_path").GetString().Should().Be("a.txt");
        started.ParentToolUseId.Should().BeNull();
        var finished = _events.OfType<ClaudeAgentToolFinished>().Single();
        finished.Should().Match<ClaudeAgentToolFinished>(tool => tool.Id == "tool-1" && tool.Name == "Write" && !tool.IsError && tool.Output == "written");
    }

    [Fact]
    public async Task A_permission_prompt_goes_to_the_handler_and_its_answer_back()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var asked = new List<ClaudeAgentPermissionRequest>();
        var allow = true;
        await using var session = await StartAsync(options => new ClaudeAgentOptions
        {
            CliPath = options.CliPath,
            WorkingDirectory = options.WorkingDirectory,
            Environment = options.Environment,
            PermissionHandler = (request, _) =>
            {
                asked.Add(request);
                return Task.FromResult(allow ? ClaudeAgentPermission.Allow() : ClaudeAgentPermission.Deny("not now"));
            },
        });

        var allowed = await session.SendAsync("permission");
        allow = false;
        var denied = await session.SendAsync("permission");

        allowed.Text.Should().Be("decision: allow||tool-2|a.txt", "the input goes back as it came");
        denied.Text.Should().Be("decision: deny|not now|tool-2|");
        asked.Should().HaveCount(2);
        asked[0].ToolName.Should().Be("Write");
        asked[0].Reason.Should().Be("writes a file");
        asked[0].Input.GetProperty("file_path").GetString().Should().Be("a.txt");
        _events.OfType<ClaudeAgentPermissionAsked>().Select(e => e.Decision.Allowed).Should().Equal(true, false);
    }

    [Fact]
    public async Task Our_own_tools_are_served_over_the_session_pipe()
    {
        if (!OperatingSystem.IsWindows())
            return;
        await using var session = await StartAsync(options => new ClaudeAgentOptions
        {
            CliPath = options.CliPath,
            WorkingDirectory = options.WorkingDirectory,
            Environment = options.Environment,
            Tools = [Echo()],
        });

        var result = await session.SendAsync("mcp");

        result.Text.Should().Be("server: neo|notified: success|tools: echo|call: echo:hi");
    }

    [Fact]
    public async Task Pictures_go_with_the_message()
    {
        if (!OperatingSystem.IsWindows())
            return;
        await using var session = await StartAsync();

        var result = await session.SendAsync("image", [new ClaudeAgentImage([1, 2, 3])]);

        result.Text.Should().Be("images: 1 image/png AQID");
    }

    [Fact]
    public async Task An_interrupt_ends_the_running_turn_and_the_next_one_works()
    {
        if (!OperatingSystem.IsWindows())
            return;
        await using var session = await StartAsync();
        var working = new TaskCompletionSource();
        session.EventReceived += agentEvent => { if (agentEvent is ClaudeAgentTextDelta { Text: "working" }) working.TrySetResult(); };

        var turn = session.SendAsync("slow");
        await working.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var second = () => session.SendAsync("hello");
        await second.Should().ThrowAsync<InvalidOperationException>("one turn at a time");
        await session.InterruptAsync();
        var stopped = await turn.WaitAsync(TimeSpan.FromSeconds(20));
        var next = await session.SendAsync("hello");

        stopped.Succeeded.Should().BeFalse();
        stopped.Subtype.Should().Be("error_during_execution");
        stopped.Text.Should().Be("stopped");
        next.Text.Should().Be("Hello");
        session.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Cancelling_a_turn_interrupts_it_and_leaves_the_session_free()
    {
        if (!OperatingSystem.IsWindows())
            return;
        await using var session = await StartAsync();
        using var cancel = new CancellationTokenSource();
        session.EventReceived += agentEvent => { if (agentEvent is ClaudeAgentTextDelta { Text: "working" }) cancel.Cancel(); };

        var turn = () => session.SendAsync("slow", cancellationToken: cancel.Token);

        await turn.Should().ThrowAsync<OperationCanceledException>();
        session.IsBusy.Should().BeFalse();
        (await session.SendAsync("hello")).Text.Should().Be("Hello");
    }

    [Fact]
    public async Task When_Claude_Code_ends_the_turn_fails_with_what_it_wrote()
    {
        if (!OperatingSystem.IsWindows())
            return;
        await using var session = await StartAsync();

        var crashing = () => session.SendAsync("crash");

        (await crashing.Should().ThrowAsync<ClaudeAgentException>()).Which.Message.Should().Contain("boom");
        await WaitUntil(() => _events.OfType<ClaudeAgentExited>().Any());
        _events.OfType<ClaudeAgentExited>().Single().ExitCode.Should().Be(3);
        session.HasExited.Should().BeTrue();
        var after = () => session.SendAsync("hello");
        await after.Should().ThrowAsync<ClaudeAgentException>();
    }

    [Fact]
    public async Task Without_an_API_key_it_does_not_start()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var environment = _standIn.Environment;
        environment["ANTHROPIC_API_KEY"] = "";

        var starting = () => ClaudeAgentSession.StartAsync(new ClaudeAgentOptions { CliPath = _standIn.Script, Environment = environment });

        await starting.Should().ThrowAsync<ClaudeAgentException>().WithMessage("*API key*ANTHROPIC_API_KEY*");
    }

    private static ClaudeAgentTool Echo() => new("echo", "Gives the text back.",
        JsonDocument.Parse("""{ "type": "object", "properties": { "text": { "type": "string" } } }""").RootElement,
        (input, _) => Task.FromResult(new ClaudeAgentToolResult("echo:" + input.GetProperty("text").GetString())));

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        condition().Should().BeTrue();
    }
}
