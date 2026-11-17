using FluentAssertions;
using Xunit;

namespace Neo.ClaudeAgent.Tests;

public class ClaudeCliTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "neo_claude_cli_tests", Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void An_npm_install_of_the_native_build_starts_bin_claude_exe_with_its_version()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var npm = Npm("2.1.326", native: true);

        var command = ClaudeCli.Find(null, [npm], []);

        command.FileName.Should().Be(Path.Combine(npm, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"));
        command.Arguments.Should().BeEmpty();
        command.Version.Should().Be(new Version(2, 1, 326));
    }

    [Fact]
    public void An_older_npm_install_runs_cli_js_with_node()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var npm = Npm("2.1.111", native: false);
        var node = Touch(npm, "node.exe");

        var command = ClaudeCli.Find(null, [npm], []);

        command.FileName.Should().Be(node);
        command.Arguments.Should().Equal(Path.Combine(npm, "node_modules", "@anthropic-ai", "claude-code", "cli.js"));
        command.Version.Should().Be(new Version(2, 1, 111));
    }

    [Fact]
    public void A_newer_copy_of_the_desktop_app_wins_over_an_old_one_on_the_path()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var npm = Npm("2.1.111", native: true);
        var desktop = Desktop("2.1.321", "2.1.325");

        var command = ClaudeCli.Find(null, [npm], [desktop]);

        command.FileName.Should().Be(Path.Combine(desktop, "2.1.325", "build", "claude.exe"));
        command.Version.Should().Be(new Version(2, 1, 325));
    }

    [Fact]
    public void The_claude_on_the_path_wins_when_it_is_as_new_or_newer()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var npm = Npm("2.1.326", native: true);
        var desktop = Desktop("2.1.325");

        var command = ClaudeCli.Find(null, [npm], [desktop]);

        command.Version.Should().Be(new Version(2, 1, 326));
        command.FileName.Should().StartWith(npm);
    }

    [Fact]
    public void Without_one_on_the_path_the_desktop_app_copy_is_used()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var desktop = Desktop("2.1.300");

        var command = ClaudeCli.Find(null, [Folder("empty")], [desktop]);

        command.FileName.Should().Be(Path.Combine(desktop, "2.1.300", "build", "claude.exe"));
    }

    [Fact]
    public void A_named_file_is_used_as_it_is_and_a_missing_one_named()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var exe = Path.Combine(Desktop("2.1.325"), "2.1.325", "build", "claude.exe");

        var command = ClaudeCli.Find(exe, [], []);
        var missing = () => ClaudeCli.Find(Path.Combine(_folder, "nowhere", "claude.exe"), [], []);

        command.Should().Be(new ClaudeCliCommand(exe, [], new Version(2, 1, 325)));
        missing.Should().Throw<ClaudeCliNotFoundException>().WithMessage("*nowhere*no such file*");
    }

    [Fact]
    public void Without_any_Claude_Code_the_message_says_how_to_get_it()
    {
        var finding = () => ClaudeCli.Find(null, [Folder("empty")], []);

        finding.Should().Throw<ClaudeCliNotFoundException>().WithMessage("*not found*npm install*desktop app*");
    }

    [Fact]
    public void A_session_of_Claude_Code_does_not_reach_the_child_and_the_key_comes_from_the_user()
    {
        var current = new Dictionary<string, string>
        {
            ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_ENTRYPOINT"] = "desktop",
            ["ANTHROPIC_BASE_URL"] = "http://localhost:1234",
            ["CLAUDE_CODE_GIT_BASH_PATH"] = "C:\\from-session\\bash.exe",
            ["PATH"] = "C:\\bin",
        };
        string? Own(string name) => name switch
        {
            "CLAUDE_CODE_GIT_BASH_PATH" => "C:\\Git\\bin\\bash.exe",
            "ANTHROPIC_API_KEY" => "users-key",
            _ => null,
        };

        var environment = ClaudeCli.ChildEnvironment(current, Own);

        environment.Should().NotContainKeys("CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "ANTHROPIC_BASE_URL");
        environment["CLAUDE_CODE_GIT_BASH_PATH"].Should().Be("C:\\Git\\bin\\bash.exe");
        environment["ANTHROPIC_API_KEY"].Should().Be("users-key");
        environment["PATH"].Should().Be("C:\\bin");
    }

    // npm's launcher, as npm 11 writes it for the native build (or for cli.js before it).
    private string Npm(string version, bool native)
    {
        var npm = Folder("npm-" + version + (native ? "-native" : "-node"));
        var package = Path.Combine(npm, "node_modules", "@anthropic-ai", "claude-code");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "package.json"), $$"""{ "name": "@anthropic-ai/claude-code", "version": "{{version}}" }""");
        string target;
        if (native)
        {
            Touch(Directory.CreateDirectory(Path.Combine(package, "bin")).FullName, "claude.exe");
            target = "\"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe\"   %*";
        }
        else
        {
            Touch(package, "cli.js");
            target = "endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & \"%_prog%\"  \"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" %*";
        }
        File.WriteAllText(Path.Combine(npm, "claude.cmd"),
            "@ECHO off\r\nGOTO start\r\n:find_dp0\r\nSET dp0=%~dp0\r\nEXIT /b\r\n:start\r\nSETLOCAL\r\nCALL :find_dp0\r\n" +
            (native ? "" : "IF EXIST \"%dp0%\\node.exe\" (\r\n  SET \"_prog=%dp0%\\node.exe\"\r\n) ELSE (\r\n  SET \"_prog=node\"\r\n)\r\n") +
            target + "\r\n");
        return npm;
    }

    // The desktop app keeps claude-code\<version>\<build>\claude.exe.
    private string Desktop(params string[] versions)
    {
        var root = Folder("desktop", "claude-code");
        foreach (var version in versions)
            Touch(Directory.CreateDirectory(Path.Combine(root, version, "build")).FullName, "claude.exe");
        return root;
    }

    private string Folder(params string[] parts)
        => Directory.CreateDirectory(Path.Combine([_folder, .. parts])).FullName;

    private static string Touch(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, []);
        return path;
    }
}
