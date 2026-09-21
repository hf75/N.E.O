using FluentAssertions;
using Neo.IPC;
using Xunit;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// The four plugin window heads share one code base per UI framework. These tests make sure
/// each head still does its job as a process: connect, load a plugin from bytes, swap it.
/// They run one at a time — every test opens a real window.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class PluginWindowSmokeTests
{
    // head project, extra launch arguments as the respective host passes them
    public static TheoryData<string, string> Heads => new()
    {
        { "Neo.PluginWindowAvalonia", "--standalone" },      // under Neo.App.Avalonia
        { "Neo.PluginWindowAvalonia", "" },                  // embedded under Neo.App (WPF host)
        { "Neo.PluginWindowAvalonia.MCP", "--standalone" },  // under Neo.McpServer
        { "Neo.PluginWindowWPF", "" },                       // embedded under Neo.App (WPF host)
        { "Neo.PluginWindowWPF.MCP", "--standalone" },       // under Neo.McpServer
    };

    [Theory]
    [MemberData(nameof(Heads))]
    public async Task Head_loads_unloads_and_reloads_a_plugin(string head, string launchArgs)
    {
        await using var child = await ChildSession.StartAsync(TestEnvironment.HeadExe(head), launchArgs);
        var plugin = TestEnvironment.PluginFor(head);

        await child.LoadAsync(plugin);
        (await child.WaitForLoadsAsync(1)).Should().BeTrue(Describe(child, "first load"));

        await child.UnloadAsync();
        await Task.Delay(300);
        await child.LoadAsync(plugin);
        (await child.WaitForLoadsAsync(2)).Should().BeTrue(Describe(child, "reload after unload"));

        await Task.Delay(500); // let late errors surface
        child.Errors.Should().BeEmpty();
        child.ChildIsAlive.Should().BeTrue();
    }

    [Fact]
    public async Task Desktop_head_stays_quiet_on_the_pipe()
    {
        // The desktop hosts do not understand Live-MCP or channel frames; their child must not send any.
        await using var child = await ChildSession.StartAsync(
            TestEnvironment.HeadExe("Neo.PluginWindowAvalonia"), "--standalone");

        await child.LoadAsync(TestEnvironment.PluginFor("Neo.PluginWindowAvalonia"));
        (await child.WaitForLoadsAsync(1)).Should().BeTrue(Describe(child, "load"));
        await Task.Delay(500);

        child.FrameCount(IpcTypes.AppManifest).Should().Be(0);
        child.FrameCount(IpcTypes.AppEvent).Should().Be(0);
    }

    [Fact]
    public async Task Mcp_head_announces_the_LiveMcp_manifest_after_load()
    {
        await using var child = await ChildSession.StartAsync(
            TestEnvironment.HeadExe("Neo.PluginWindowAvalonia.MCP"), "--standalone");

        await child.LoadAsync(TestEnvironment.PluginFor("Neo.PluginWindowAvalonia.MCP"));
        (await child.WaitForLoadsAsync(1)).Should().BeTrue(Describe(child, "load"));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (child.FrameCount(IpcTypes.AppManifest) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        child.FrameCount(IpcTypes.AppManifest).Should().Be(1, "Neo.McpServer builds the per-app tools from it");
    }

    [Fact]
    public async Task Avalonia_desktop_head_runs_without_NeoAppApi_next_to_it()
    {
        // Neo.App.Avalonia copies only Neo.PluginWindowAvalonia.* into its bin folder, so the child
        // runs there without Neo.App.Api.dll. The shared window library references Neo.App.Ai for the
        // MCP head; if that reference ever leaks into a code path the desktop head JIT-compiles,
        // the child stays alive but never connects — and the Avalonia desktop host is dead.
        var source = TestEnvironment.HeadBinDir("Neo.PluginWindowAvalonia");
        var deployed = Path.Combine(Path.GetTempPath(), "neo_smoke_deploy_" + Guid.NewGuid().ToString("N")[..8]);
        CopyDirectory(source, deployed, skipFile: name => name.StartsWith("Neo.App.Api.", StringComparison.OrdinalIgnoreCase));

        try
        {
            File.Exists(Path.Combine(deployed, "Neo.App.Api.dll")).Should().BeFalse();

            await using var child = await ChildSession.StartAsync(
                Path.Combine(deployed, "Neo.PluginWindowAvalonia.exe"), "--standalone");
            await child.LoadAsync(TestEnvironment.PluginFor("Neo.PluginWindowAvalonia"));
            (await child.WaitForLoadsAsync(1)).Should().BeTrue(Describe(child, "load"));
            child.Errors.Should().BeEmpty();
        }
        finally
        {
            try { Directory.Delete(deployed, recursive: true); } catch { /* child may still release files */ }
        }
    }

    private static string Describe(ChildSession child, string step)
        => $"{step} should be reported as executed. Child errors: [{string.Join(" | ", child.Errors)}] " +
           $"logs: [{string.Join(" | ", child.Logs)}]";

    private static void CopyDirectory(string source, string target, Func<string, bool> skipFile)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            if (skipFile(Path.GetFileName(file))) continue;
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }
}

[CollectionDefinition("PluginWindowProcesses", DisableParallelization = true)]
public class PluginWindowProcessesCollection { }
