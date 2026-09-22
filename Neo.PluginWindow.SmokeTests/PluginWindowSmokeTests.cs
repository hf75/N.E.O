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
    public async Task Standalone_head_tells_the_host_where_its_window_is_and_keeps_its_place_when_asked()
    {
        // Player mode: the host hides behind the app window and follows it, so the child reports
        // its bounds — and a bounds message with Dock=false must not move it.
        await using var child = await ChildSession.StartAsync(
            TestEnvironment.HeadExe("Neo.PluginWindowAvalonia"), "--standalone --title \"Player Test\"");

        await child.LoadAsync(TestEnvironment.PluginFor("Neo.PluginWindowAvalonia"));
        (await child.WaitForLoadsAsync(1)).Should().BeTrue(Describe(child, "load"));

        // Show the window at a known place, without docking.
        await child.SendControlAsync(IpcTypes.PositionWindow, Json.ToJson(new ParentWindowBoundsMessage(300, 200, 640, 480, true)));
        await Task.Delay(1500);
        child.FrameCount(IpcTypes.ChildWindowBounds).Should().BeGreaterThan(0, "a shown standalone window reports its bounds");
        var reported = Json.FromJson<ParentWindowBoundsMessage>(child.LastPayload(IpcTypes.ChildWindowBounds)!)!;
        reported.X.Should().Be(300);
        reported.Y.Should().Be(200);

        // Host bounds without docking: the window stays where it is.
        await child.SendControlAsync(IpcTypes.ParentWindowBounds, Json.ToJson(new ParentWindowBoundsMessage(1000, 1000, 600, 700, true, Dock: false)));
        await Task.Delay(800);
        var after = Json.FromJson<ParentWindowBoundsMessage>(child.LastPayload(IpcTypes.ChildWindowBounds)!)!;
        after.X.Should().Be(300, "Dock=false leaves the app window alone");
        after.Y.Should().Be(200);

        // Host bounds with docking (the normal mode): the window follows.
        await child.SendControlAsync(IpcTypes.ParentWindowBounds, Json.ToJson(new ParentWindowBoundsMessage(100, 150, 500, 700, true)));
        await Task.Delay(800);
        var docked = Json.FromJson<ParentWindowBoundsMessage>(child.LastPayload(IpcTypes.ChildWindowBounds)!)!;
        docked.X.Should().Be(100 + 500 + 8, "docked to the right of the host");
        docked.Y.Should().Be(150);
        child.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task A_child_that_exits_closes_the_pipe_cleanly()
    {
        // The host tells "the user closed the app window" (clean end) from a crash (broken pipe).
        await using var child = await ChildSession.StartAsync(
            TestEnvironment.HeadExe("Neo.PluginWindowAvalonia"), "--standalone");
        await child.LoadAsync(TestEnvironment.PluginFor("Neo.PluginWindowAvalonia"));
        (await child.WaitForLoadsAsync(1)).Should().BeTrue(Describe(child, "load"));

        child.CloseMainWindow();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15) && !child.PipeClosedCleanly) await Task.Delay(100);

        child.PipeClosedCleanly.Should().BeTrue("the receive loop ends without an exception when the child closes its end");
        child.ChildIsAlive.Should().BeFalse();
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

    [Fact]
    public async Task Avalonia_child_hosts_the_native_web_view_inside_a_plugin()
    {
        // Avalonia.Controls.WebView ships with the child runtime so that generated apps can embed a
        // browser. The plugin lives in a collectible load context and is loaded from bytes — this
        // proves the control still starts its platform browser there and talks back to C#.
        // Needs the platform web view (WebView2 on Windows 10/11).
        var marker = Path.Combine(Path.GetTempPath(), "neo_smoke_webview_" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        var profileNextToExe = TestEnvironment.HeadExe("Neo.PluginWindowAvalonia") + ".WebView2";
        if (Directory.Exists(profileNextToExe))
            Directory.Delete(profileNextToExe, recursive: true);
        try
        {
            await using var child = await ChildSession.StartAsync(
                TestEnvironment.HeadExe("Neo.PluginWindowAvalonia"), "--standalone",
                new Dictionary<string, string> { [TestEnvironment.WebViewMarkerVariable] = marker });

            await child.LoadAsync(TestEnvironment.AvaloniaWebViewPlugin);
            (await child.WaitForLoadsAsync(1)).Should().BeTrue(Describe(child, "load"));

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(marker) && DateTime.UtcNow < deadline && child.ChildIsAlive)
                await Task.Delay(200);

            child.Errors.Should().BeEmpty();
            File.Exists(marker).Should().BeTrue("the page calls invokeCSharpAction(...) and the plugin writes the message to disk");
            File.ReadAllText(marker).Should().Be("hello from the page");

            // WebView2's default profile folder is "<exe>.WebView2". The child redirects it to a
            // per-user folder: the bin folder is not writable once installed, and Neo.App's build
            // copies the child's bin folder wholesale.
            Directory.Exists(profileNextToExe).Should().BeFalse("the child must keep the browser profile out of its bin folder");
        }
        finally
        {
            try { File.Delete(marker); } catch { /* best effort */ }
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
