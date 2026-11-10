using System.Collections;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Neo.Agents;
using Neo.Agents.Core;
using Xunit;

namespace Neo.App.Core.Tests;

/// <summary>
/// The Claude Code agent. Jobs run against a stand-in for the CLI: a PowerShell script that reads the task, writes
/// events the way Claude Code does, and sleeps or breaks down when the task says so. The real Claude Code is never started.
/// </summary>
public sealed class ClaudeCodeAgentTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "neo-claude-code-tests", Guid.NewGuid().ToString("N"));
    private readonly List<ClaudeCodeJob> _jobs = new();

    public ClaudeCodeAgentTests()
    {
        Directory.CreateDirectory(_folder);
        ClaudeCodeJob.PollInterval = TimeSpan.FromMilliseconds(100);
    }

    public void Dispose()
    {
        foreach (var job in _jobs)
            job.Cancel();
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── Finding Claude Code ─────────────────────────────────────────

    [Fact]
    public void Without_the_switch_Claude_Code_is_off()
    {
        var finding = () => ClaudeCodeSetup.FindCommand(null, []);

        finding.Should().Throw<ClaudeCodeUnavailableException>().WithMessage("*switched off*NEO_CLAUDE_CODE*");
    }

    [Fact]
    public void The_switch_finds_claude_on_the_path()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var bin = Folder("bin");
        var claude = Touch(bin, "claude.exe");

        var command = ClaudeCodeSetup.FindCommand("1", [Folder("empty"), bin]);

        command.FileName.Should().Be(claude);
        command.Arguments.Should().BeEmpty();
    }

    [Fact]
    public void An_npm_install_starts_node_with_cli_js_without_a_command_shell_in_between()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var npm = Folder("npm");
        File.WriteAllText(Path.Combine(npm, "claude.cmd"), NpmLauncher("node_modules\\@anthropic-ai\\claude-code\\cli.js"));
        var node = Touch(npm, "node.exe");
        var cli = Touch(Folder("npm", "node_modules", "@anthropic-ai", "claude-code"), "cli.js");

        var command = ClaudeCodeSetup.FindCommand("1", [npm]);

        command.FileName.Should().Be(node);
        command.Arguments.Should().Equal(cli);
    }

    [Fact]
    public void A_launcher_is_followed_to_where_it_points()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var npm = Folder("npm");
        File.WriteAllText(Path.Combine(npm, "claude.cmd"), NpmLauncher("node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe"));
        var native = Touch(Folder("npm", "node_modules", "@anthropic-ai", "claude-code", "bin"), "claude.exe");

        ClaudeCodeSetup.FindCommand(Path.Combine(npm, "claude.cmd"), []).Should().Be(new ClaudeCodeCommand(native, []));
    }

    [Fact]
    public void A_path_in_the_switch_is_used_and_a_wrong_one_named()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var tools = Folder("tools");
        var node = Touch(tools, "node.exe");
        var cli = Touch(Folder("claude"), "cli.js");

        var command = ClaudeCodeSetup.FindCommand(cli, [tools]);
        var wrong = () => ClaudeCodeSetup.FindCommand(Path.Combine(_folder, "nowhere", "claude.exe"), [tools]);

        command.FileName.Should().Be(node);
        command.Arguments.Should().Equal(cli);
        wrong.Should().Throw<ClaudeCodeUnavailableException>().WithMessage("*nowhere*no such file*");
    }

    [Fact]
    public void Where_there_is_no_claude_the_message_says_how_to_get_it()
    {
        var finding = () => ClaudeCodeSetup.FindCommand("1", [Folder("empty")]);

        finding.Should().Throw<ClaudeCodeUnavailableException>().WithMessage("*not found*npm install*");
    }

    // ── What Claude Code takes along ────────────────────────────────

    [Fact]
    public void Inside_a_Claude_Code_session_its_wiring_stays_behind()
    {
        var current = new Hashtable
        {
            ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_ENTRYPOINT"] = "desktop",
            ["CLAUDE_CODE_GIT_BASH_PATH"] = "from the session",
            ["CLAUDE_AGENT_SDK_VERSION"] = "1",
            ["ANTHROPIC_BASE_URL"] = "http://localhost:1/proxy",
            ["ANTHROPIC_AUTH_TOKEN"] = "session token",
            ["PATH"] = "C:\\bin",
        };
        string? UsersOwn(string name) => name switch
        {
            "CLAUDE_CODE_GIT_BASH_PATH" => "C:\\Git\\bin\\bash.exe",
            "ANTHROPIC_API_KEY" => "the user's key",
            _ => null,
        };

        var environment = ClaudeCodeSetup.ChildEnvironment(current, UsersOwn);

        environment.Keys.Should().NotContain(["CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_AGENT_SDK_VERSION", "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN"]);
        environment["CLAUDE_CODE_GIT_BASH_PATH"].Should().Be("C:\\Git\\bin\\bash.exe", "what the user set for themselves stays");
        environment["PATH"].Should().Be("C:\\bin");
        environment["ANTHROPIC_API_KEY"].Should().Be("the user's key");
    }

    [Fact]
    public void Outside_a_session_the_environment_stays_as_it_is()
    {
        var current = new Hashtable
        {
            ["ANTHROPIC_BASE_URL"] = "https://gateway.example",
            ["CLAUDE_CODE_USE_BEDROCK"] = "1",
            ["ANTHROPIC_API_KEY"] = "own key",
        };

        var environment = ClaudeCodeSetup.ChildEnvironment(current, _ => "another key");

        environment.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["ANTHROPIC_BASE_URL"] = "https://gateway.example",
            ["CLAUDE_CODE_USE_BEDROCK"] = "1",
            ["ANTHROPIC_API_KEY"] = "own key",
        });
    }

    [Fact]
    public void Arguments_reach_the_program_as_they_are()
    {
        if (!OperatingSystem.IsWindows())
            return;
        string[] arguments =
        [
            "-p", "", "two words", "a \"quoted\" word", @"C:\folder\", @"back\\slash""quote", "Bash(dotnet *)",
            "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}}", "& | < > ^ %PATH%", "Grüße\tund\nZeilen",
        ];

        var line = WindowsCommandLine.Join(@"C:\Program Files\node\node.exe", arguments);

        Split(line).Should().Equal([@"C:\Program Files\node\node.exe", .. arguments]);
    }

    [Fact]
    public void The_options_say_what_the_job_may_do()
    {
        var first = ClaudeCodeArguments.For(new ClaudeCodeRequest("task") { Model = "default" }, "session-1", @"C:\work");
        var follow = ClaudeCodeArguments.For(new ClaudeCodeRequest("task")
        {
            ResumeSessionId = "session-1",
            Model = "opus",
            Effort = "high",
            JsonSchema = "{\"type\":\"object\"}",
            MaxBudgetUsd = 1.5,
            AllowedTools = [],
        }, "session-1", @"C:\work");

        first.Should().ContainInOrder("-p", "--output-format", "stream-json", "--verbose");
        first.Should().ContainInOrder("--session-id", "session-1");
        first.Should().ContainInOrder("--permission-mode", "acceptEdits");
        first.Should().ContainInOrder("--allowedTools", "Bash(dotnet *)", "PowerShell(dotnet *)", "--disallowedTools", "AskUserQuestion");
        first.Should().ContainInOrder("--max-budget-usd", "5");
        first.Should().NotContain(["--model", "--resume", "--json-schema", "--effort"]);
        first[first.IndexOf("--append-system-prompt") + 1].Should().Contain(@"C:\work").And.Contain("nobody can answer");

        follow.Should().ContainInOrder("--resume", "session-1").And.NotContain("--session-id");
        follow.Should().ContainInOrder("--model", "opus").And.ContainInOrder("--effort", "high");
        follow.Should().ContainInOrder("--json-schema", "{\"type\":\"object\"}").And.ContainInOrder("--max-budget-usd", "1.5");
        follow.Should().NotContain("--allowedTools");
    }

    // ── What Claude Code writes ─────────────────────────────────────

    [Fact]
    public void The_transcript_tells_what_Claude_Code_does_and_how_it_ended()
    {
        // As the real Claude Code 2.1.111 wrote it for a small task with a schema, shortened.
        string[] lines =
        [
            """{"type":"system","subtype":"init","cwd":"C:\\work","session_id":"fae02a05","model":"claude-haiku-4-5-20251001","permissionMode":"acceptEdits"}""",
            """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"..."}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Write","input":{"file_path":"C:\\work\\hello.txt","content":"hi"}}]}}""",
            """{"type":"user","message":{"content":[{"tool_use_id":"t1","type":"tool_result","content":"File created successfully"}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Done! Created hello.txt.\n\nMississippi has **11 letters**."}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"StructuredOutput","input":{"file":"hello.txt","letters":11}}]}}""",
            """{"type":"result","subtype":"success","is_error":false,"duration_ms":7220,"num_turns":3,"result":"","session_id":"fae02a05","total_cost_usd":0.03324475,"permission_denials":[],"structured_output":{"file":"hello.txt","letters":11}}""",
            "not json at all",
        ];
        var transcript = new ClaudeCodeTranscript();

        foreach (var line in lines)
            transcript.Add(line);

        transcript.Progress.Should().Equal(
            "Claude Code started (claude-haiku-4-5-20251001).",
            "Writes hello.txt",
            "Done! Created hello.txt. Mississippi has **11 letters**.",
            "Done.");
        var result = transcript.Result!;
        result.Succeeded.Should().BeTrue();
        result.Text.Should().Be("Done! Created hello.txt.\n\nMississippi has **11 letters**.", "an answer in a schema leaves 'result' empty");
        JsonDocument.Parse(result.StructuredOutputJson!).RootElement.GetProperty("letters").GetInt32().Should().Be(11);
        result.SessionId.Should().Be("fae02a05");
        result.CostUsd.Should().BeApproximately(0.0332, 0.0001);
        result.Turns.Should().Be(3);
        result.Duration.Should().Be(TimeSpan.FromMilliseconds(7220));
    }

    [Fact]
    public void A_stop_and_a_refused_step_are_told_as_such()
    {
        var transcript = new ClaudeCodeTranscript();

        transcript.Add("""{"type":"assistant","message":{"content":[{"type":"text","text":"Building it now."}]}}""");
        transcript.Add("""{"type":"result","subtype":"error_max_budget_usd","is_error":true,"total_cost_usd":5.01,"permission_denials":[{"tool_name":"Bash","tool_use_id":"x","tool_input":{}}]}""");

        transcript.Result!.Succeeded.Should().BeFalse();
        transcript.Result.Text.Should().StartWith("Claude Code stopped: the budget").And.EndWith("Building it now.");
        transcript.Progress.Should().Contain("Not allowed here: Bash.");
    }

    // ── Jobs ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_job_runs_in_the_background_and_any_program_can_follow_it()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var store = Store();

        var job = Start(store, new ClaudeCodeRequest("Build a thing") { Title = "A thing" });
        var told = new List<string>();
        var result = await job.WaitAsync(new SynchronousProgress(told.Add)).WaitAsync(TimeSpan.FromSeconds(60));

        result.Succeeded.Should().BeTrue(result.Text);
        job.State.Should().Be(ClaudeCodeJobState.Succeeded);
        told.Should().ContainInOrder("Claude Code started (stand-in-model).", "Writes hello.txt", "Write failed: disk full", "All done.", "Done.");
        result.CostUsd.Should().Be(0.25);
        result.SessionId.Should().Be("stand-in-session");

        var report = JsonDocument.Parse(result.Text).RootElement;
        report.GetProperty("task").GetString().Should().Be("Build a thing", "the task arrives on the standard input");
        report.GetProperty("claudecode").GetString().Should().BeEmpty("a session's wiring does not reach the job");
        report.GetProperty("folder").GetString().Should().Be(job.WorkingDirectory);
        var arguments = report.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()).ToList();
        arguments.Should().ContainInOrder("-p", "--output-format", "stream-json").And.ContainInOrder("--session-id", job.Id);
        job.WorkingDirectory.Should().Be(Path.Combine(job.Folder, "work"));

        // Another program that knows only the folder.
        var seen = new ClaudeCodeJobStore(store.Root, () => throw new InvalidOperationException(), () => new Dictionary<string, string>()).Find(job.Id)!;
        seen.Title.Should().Be("A thing");
        seen.State.Should().Be(ClaudeCodeJobState.Succeeded);
        seen.Result!.Text.Should().Be(result.Text);
        seen.Progress.Should().Equal(job.Progress);
    }

    [Fact]
    public async Task Cancelling_stops_Claude_Code_and_whoever_looks_sees_it()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var store = Store();
        var job = Start(store, new ClaudeCodeRequest("SLEEP please"));
        await Until(() => job.Progress.Count > 0, "the stand-in has started");

        job.Cancel();

        job.State.Should().Be(ClaudeCodeJobState.Cancelled);
        job.ProcessRunning.Should().BeFalse();
        job.Result!.Ending.Should().Be("cancelled");
        ClaudeCodeJobStore.Open(job.Folder)!.State.Should().Be(ClaudeCodeJobState.Cancelled);
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_job_it_runs()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var job = Start(Store(), new ClaudeCodeRequest("SLEEP please"));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var running = () => job.RunToEndAsync(cancellationToken: cancel.Token);

        await running.Should().ThrowAsync<OperationCanceledException>();
        job.State.Should().Be(ClaudeCodeJobState.Cancelled);
    }

    [Fact]
    public async Task A_job_that_ends_without_a_result_shows_its_error_output()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var job = Start(Store(), new ClaudeCodeRequest("CRASH please"));

        var result = await job.WaitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        job.State.Should().Be(ClaudeCodeJobState.Failed);
        result.Ending.Should().Be("no result");
        result.Text.Should().Contain("ended without a result").And.Contain("stand-in broke down");
    }

    [Fact]
    public async Task A_job_past_its_time_is_stopped()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var job = Start(Store(), new ClaudeCodeRequest("SLEEP please") { MaxMinutes = 0.03 });

        var result = await job.WaitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        result.Ending.Should().Be("timeout");
        job.State.Should().Be(ClaudeCodeJobState.Failed);
        job.ProcessRunning.Should().BeFalse();
    }

    [Fact]
    public async Task The_watch_tells_once_when_a_job_ends_and_not_about_old_ones()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var store = Store();
        var old = Start(store, new ClaudeCodeRequest("An old task"));
        await old.WaitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        var told = new List<string>();

        using (store.WatchFinished(job => { lock (told) told.Add(job.Id); }, TimeSpan.FromMilliseconds(100)))
        {
            var job = Start(store, new ClaudeCodeRequest("A new task"));
            await Until(() => { lock (told) return told.Count > 0; }, "the watch has told");
            await Task.Delay(500);

            lock (told)
                told.Should().Equal(job.Id);
        }
    }

    [Fact]
    public void A_job_cannot_start_while_Claude_Code_is_off_and_leaves_nothing_behind()
    {
        var store = new ClaudeCodeJobStore(Path.Combine(_folder, "jobs"),
            () => ClaudeCodeSetup.FindCommand(null, []), () => new Dictionary<string, string>());

        var starting = () => store.Start(new ClaudeCodeRequest("task"));

        starting.Should().Throw<ClaudeCodeUnavailableException>();
        Directory.Exists(store.Root).Should().BeFalse();
    }

    // ── The agent ───────────────────────────────────────────────────

    [Fact]
    public async Task The_agent_streams_the_progress_and_ends_with_the_result()
    {
        if (!OperatingSystem.IsWindows())
            return;
        IStreamingAgent agent = new ClaudeCodeAgent { Store = Store() };
        agent.SetInput("Prompt", "Build a thing");
        agent.SetOption("Model", "sonnet");
        var chunks = new List<StreamChunk>();

        await foreach (var chunk in agent.ExecuteStreamingAsync().WithCancellation(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token))
            chunks.Add(chunk);

        chunks.Where(c => c.Name == "Progress").Select(c => (string)c.Value).Should().Contain("Writes hello.txt");
        chunks.Last().Name.Should().Be("Result");
        chunks.Last().IsFinal.Should().BeTrue();
        agent.GetOutput<bool>("Succeeded").Should().BeTrue();
        agent.GetOutput<double>("CostUsd").Should().Be(0.25);
        agent.GetOutput<string>("SessionId").Should().Be("stand-in-session");
        agent.GetOutput<string>("StructuredOutput").Should().Contain("\"size\"");
        var arguments = JsonDocument.Parse(agent.GetOutput<string>("Result")).RootElement.GetProperty("arguments").EnumerateArray().Select(a => a.GetString());
        arguments.Should().ContainInOrder("--model", "sonnet");
        Directory.Exists(agent.GetOutput<string>("WorkingDirectory")).Should().BeTrue();
    }

    [Fact]
    public void Data_comes_back_typed()
    {
        var schema = JsonDocument.Parse(ClaudeCodeData.SchemaFor<List<Row>>()).RootElement;
        var result = new ClaudeCodeJobResult(true, "", """{"result":[{"name":"a","size":1}]}""", null, 0, 0, TimeSpan.Zero, "success");

        schema.GetProperty("type").GetString().Should().Be("object", "a schema for structured output has an object at the top");
        schema.GetProperty("properties").GetProperty("result").GetProperty("type").GetString().Should().Be("array");
        ClaudeCodeData.Read<List<Row>>(result).Should().Equal(new Row("a", 1));
        ClaudeCodeData.Read<List<Row>>(result with { StructuredOutputJson = null }).Should().BeNull();
    }

    public sealed record Row(string Name, int Size);

    // ── In N.E.O. ───────────────────────────────────────────────────

    [Fact]
    public void The_plugin_needs_the_switch_and_brings_its_helper()
    {
        var plugin = new ClaudeCodeAgent();

        plugin.RequiredEnvVar.Should().Be("NEO_CLAUDE_CODE");
        plugin.AgentDllName.Should().Be(typeof(ClaudeCodeAgent).Assembly.GetName().Name + ".dll");
        plugin.HelperTemplateCode.Should().Contain("namespace Neo.App").And.Contain("static class AIClaudeCode");
        plugin.TemplatePlaceholders.Keys.Should().ContainSingle().Which.Should().Match(p => plugin.HelperTemplateCode!.Contains(p));
    }

    private const string AppCode = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Neo.App;

        namespace Neo.Dynamic
        {
            public record Folder(string Name, long Bytes);

            public class Tool
            {
                public async Task<string> Run(CancellationToken cancel)
                {
                    var progress = new Progress<string>(line => Console.WriteLine(line));
                    ClaudeCodeResult built = await AIClaudeCode.RunAsync("Build a tool", progress, null, cancel);
                    ClaudeCodeResult<List<Folder>> data = await AIClaudeCode.RunAsync<List<Folder>>("List the folders", progress);
                    ClaudeCodeResult next = await AIClaudeCode.ContinueAsync(built, "Add a dark mode", progress, cancel);
                    return built.Text + built.Folder + built.JobId + built.SessionId + built.CostUsd + built.Succeeded + data.Data!.Count + next.Text;
                }
            }
        }
        """;

    private static string Helper() => new ClaudeCodeAgent().HelperTemplateCode!.Replace("CLAUDE_CODE_MODEL_PLACEHOLDER", "default");

    [Fact]
    public void The_helper_and_an_app_using_it_compile()
    {
        Errors(Helper(), AppCode).Should().BeEmpty();
    }

    [Fact]
    public void They_also_compile_as_an_exported_program()
    {
        var code = new List<string> { Helper(), AppCode, "namespace Exported { public class App { } }" }
            .Select(source => NamespaceFixer.FixNamespace(source, "Neo"))
            .ToList();
        code = NamespaceFixer.RemoveConflictingUsings(code);

        Errors(code.ToArray()).Should().BeEmpty();
    }

    [Fact]
    public void The_prompt_documents_what_the_helper_really_has()
    {
        var docs = new ClaudeCodeAgent().SystemMessageDocs!;
        var helper = Helper();

        foreach (var declaration in new[]
                 {
                     "record ClaudeCodeResult(bool Succeeded, string Text, string Folder, string JobId, string SessionId, double CostUsd)",
                     "record ClaudeCodeResult<T>(bool Succeeded, T? Data, string Text, string Folder, string JobId, string SessionId, double CostUsd)",
                     "RunAsync(string task, IProgress<string>? progress = null, string? folder = null, CancellationToken cancellationToken = default)",
                     "RunAsync<T>(string task, IProgress<string>? progress = null, string? folder = null, CancellationToken cancellationToken = default)",
                     "ContinueAsync(ClaudeCodeResult previous, string task, IProgress<string>? progress = null, CancellationToken cancellationToken = default)",
                 })
        {
            docs.Should().Contain(declaration);
            helper.Should().Contain(declaration);
        }
    }

    [Fact]
    public async Task The_chat_tells_how_a_job_ended_and_where_its_result_is()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var job = Start(Store(), new ClaudeCodeRequest("Build a thing") { Title = "A thing" });
        await job.WaitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var notice = ClaudeCodeNotices.Describe(job);

        notice.Should().StartWith("Claude Code is done: A thing");
        notice.Should().Contain($"Folder: {job.WorkingDirectory}").And.Contain("Cost: 0.25 USD");
    }

    // ── Tools ───────────────────────────────────────────────────────

    // Stands in for Claude Code: answers in its event format and reports what it was given.
    private const string StandIn = """
        $task = [Console]::In.ReadToEnd()
        function J($text) { return ConvertTo-Json -InputObject "$text" -Compress }
        function Send($line) { [Console]::Out.WriteLine($line); [Console]::Out.Flush() }
        Send '{"type":"system","subtype":"init","session_id":"stand-in-session","model":"stand-in-model"}'
        if ($task -match 'SLEEP') { Start-Sleep -Seconds 120 }
        if ($task -match 'CRASH') { [Console]::Error.WriteLine('stand-in broke down'); exit 3 }
        Send '{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Write","input":{"file_path":"C:\\work\\hello.txt","content":"hi"}}]}}'
        Send '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"disk full"}]}}'
        Send '{"type":"assistant","message":{"content":[{"type":"text","text":"All done."}]}}'
        $report = '{"task":' + (J $task.Trim()) + ',"arguments":' + (ConvertTo-Json -InputObject @($args) -Compress) + ',"claudecode":' + (J $env:CLAUDECODE) + ',"folder":' + (J (Get-Location).Path) + '}'
        Send ('{"type":"result","subtype":"success","is_error":false,"result":' + (J $report) + ',"session_id":"stand-in-session","total_cost_usd":0.25,"num_turns":3,"duration_ms":1500,"structured_output":{"result":[{"name":"a","size":1}]}}')
        """;

    private ClaudeCodeJobStore Store()
    {
        var script = Path.Combine(_folder, "claude-stand-in.ps1");
        File.WriteAllText(script, StandIn);
        var command = ClaudeCodeSetup.FindCommand(script, []);

        // As inside a Claude Code session, which is where these tests often run.
        var current = Environment.GetEnvironmentVariables();
        current["CLAUDECODE"] = "1";
        var environment = ClaudeCodeSetup.ChildEnvironment(current, _ => null);

        return new ClaudeCodeJobStore(Path.Combine(_folder, "jobs"), () => command, () => environment);
    }

    private ClaudeCodeJob Start(ClaudeCodeJobStore store, ClaudeCodeRequest request)
    {
        var job = store.Start(request);
        _jobs.Add(job);
        return job;
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Waited in vain until {what}.");
            await Task.Delay(50);
        }
    }

    private string Folder(params string[] parts)
    {
        var folder = Path.Combine([_folder, .. parts]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Touch(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "");
        return path;
    }

    // What npm writes as claude.cmd, with the target it starts.
    private static string NpmLauncher(string target) => $$"""
        @ECHO off
        GOTO start
        :find_dp0
        SET dp0=%~dp0
        EXIT /b
        :start
        SETLOCAL
        CALL :find_dp0

        IF EXIST "%dp0%\node.exe" (
          SET "_prog=%dp0%\node.exe"
        ) ELSE (
          SET "_prog=node"
          SET PATHEXT=%PATHEXT:;.JS;=;%
        )

        endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & "%_prog%"  "%dp0%\{{target}}" %*
        """;

    private static IEnumerable<Diagnostic> Errors(params string[] sources)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

        return CSharpCompilation.Create("Tool", sources.Select(s => CSharpSyntaxTree.ParseText(s)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error);
    }

    private static string[] Split(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try
        {
            return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
