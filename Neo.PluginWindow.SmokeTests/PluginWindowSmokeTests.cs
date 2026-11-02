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
    public void The_Avalonia_desktop_host_ships_the_app_network_api_next_to_its_child()
    {
        // Neo.App.Avalonia copies only the child's own assemblies into its bin folder. The child
        // publishes every loaded app on the app network with Neo.App.Api, and generated code calls
        // other apps with it — without the DLL next to the child, the head could not handle a
        // single frame and the desktop host would be dead.
        var hostBin = Path.GetDirectoryName(TestEnvironment.HostExe("Neo.App.Avalonia"))!;
        File.Exists(Path.Combine(hostBin, "Neo.PluginWindowAvalonia.exe")).Should().BeTrue();
        File.Exists(Path.Combine(hostBin, "Neo.App.Api.dll")).Should().BeTrue("the host's CopyChildOutputs target copies it");
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
}

[CollectionDefinition("PluginWindowProcesses", DisableParallelization = true)]
public class PluginWindowProcessesCollection : ICollectionFixture<Neo.Tests.ForegroundTurn> { }
