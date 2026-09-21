using FluentAssertions;
using Neo.IPC;
using Neo.Shared;
using Xunit;

namespace Neo.HotReload.Tests;

/// <summary>
/// The coordinator is what the hosts call. They only have DLL paths: the running build and the
/// build the compile agent just produced at (often) the very same path.
/// </summary>
public class HotReloadCoordinatorTests
{
    private const string V1 = """
        public class DynamicUserControl
        {
            public int Clicks;
            public void Click() { Clicks++; }
            public string Describe() => "v1 clicks=" + Clicks;
        }
        """;

    [Fact]
    public async Task A_host_patches_the_running_app_from_nothing_but_dll_paths()
    {
        using var app = await HotReloadHarness.StartAsync(V1);
        using var coordinator = new HotReloadCoordinator();
        (await coordinator.StartAsync(app.DllPath, HotReloadRuntime.Capabilities)).Should().BeNull();

        app.Call("Click");

        // The desktop hosts compile every version to the same path — over the build that is running.
        await HotReloadHarness.CompileAsync(V1.Replace("\"v1 clicks=\"", "\"v2 clicks=\""), app.AssemblyName, app.DllPath);

        var attempt = await coordinator.TryAsync(app.DllPath, request => Task.FromResult<HotReloadResultMessage?>(app.ApplyLikeAPluginWindow(request)));

        attempt.Applied.Should().BeTrue(attempt.Message);
        attempt.Message.Should().Contain("state is preserved");
        app.Call("Describe").Should().Be("v2 clicks=1");
    }

    [Fact]
    public async Task The_host_can_keep_compiling_to_the_same_path_while_the_app_is_hot_reloaded()
    {
        // The desktop hosts compile every build — prompts, code editor, undo — to the same DLL path.
        // Regression: the hot reload session held that path's PDB open after the first update, so
        // the next compile failed with "file is being used".
        using var app = await HotReloadHarness.StartAsync(V1);
        using var coordinator = new HotReloadCoordinator();
        (await coordinator.StartAsync(app.DllPath, HotReloadRuntime.Capabilities)).Should().BeNull();
        app.Call("Click");

        for (int version = 2; version <= 4; version++)
        {
            var source = V1.Replace("\"v1 clicks=\"", $"\"v{version} clicks=\"");
            await HotReloadHarness.CompileAsync(source, app.AssemblyName, app.DllPath);

            var attempt = await coordinator.TryAsync(app.DllPath, request => Task.FromResult<HotReloadResultMessage?>(app.ApplyLikeAPluginWindow(request)));

            attempt.Applied.Should().BeTrue($"version {version}: {attempt.Message}");
            app.Call("Describe").Should().Be($"v{version} clicks=1");
        }
    }

    [Fact]
    public async Task Lambdas_keep_their_identity_across_builds_compiled_to_the_same_path()
    {
        // Roslyn maps the lambdas of the running code onto the new code with the debug information
        // of the running build. It must read the running build's — not whatever the next compile
        // left at that path. A mismatch shows as a delegate that no longer runs the right body.
        const string ticker = """
            using System;

            public class DynamicUserControl
            {
                public Func<string> Tick;

                public DynamicUserControl()
                {
                    int calls = 0;
                    Tick = () => "tick v1 #" + (++calls);
                }
            }
            """;
        using var app = await HotReloadHarness.StartAsync(ticker);
        using var coordinator = new HotReloadCoordinator();
        (await coordinator.StartAsync(app.DllPath, HotReloadRuntime.Capabilities)).Should().BeNull();
        var tick = app.Get<Func<string>>("Tick");
        tick().Should().Be("tick v1 #1");

        for (int version = 2; version <= 3; version++)
        {
            await HotReloadHarness.CompileAsync(ticker.Replace("\"tick v1 #\"", $"\"tick v{version} #\""), app.AssemblyName, app.DllPath);
            var attempt = await coordinator.TryAsync(app.DllPath, request => Task.FromResult<HotReloadResultMessage?>(app.ApplyLikeAPluginWindow(request)));

            attempt.Applied.Should().BeTrue(attempt.Message);
            tick().Should().Be($"tick v{version} #{version}", "the delegate created by v1 runs the new body and keeps its captured counter");
        }
    }

    [Fact]
    public async Task Whatever_goes_wrong_ends_in_a_full_reload_never_in_an_exception()
    {
        using var app = await HotReloadHarness.StartAsync(V1);
        using var coordinator = new HotReloadCoordinator();

        (await coordinator.TryAsync(app.DllPath, _ => throw new InvalidOperationException("must not be called")))
            .Applied.Should().BeFalse("nothing was started yet");

        (await coordinator.StartAsync(app.DllPath, childCapabilities: null)).Should().Contain("without hot reload support");
        coordinator.IsActive.Should().BeFalse();

        (await coordinator.StartAsync(app.DllPath, HotReloadRuntime.Capabilities)).Should().BeNull();
        await HotReloadHarness.CompileAsync(V1.Replace("\"v1 clicks=\"", "\"v2 clicks=\""), app.AssemblyName, app.DllPath);

        // The plugin window rejects the update (or never answers): the session is gone, the host reloads.
        var rejected = await coordinator.TryAsync(app.DllPath,
            _ => Task.FromResult<HotReloadResultMessage?>(new HotReloadResultMessage(false, Error: "boom")));
        rejected.Applied.Should().BeFalse();
        rejected.Message.Should().Contain("boom").And.Contain("Reloading");
        coordinator.IsActive.Should().BeFalse("running code and session may disagree now");
    }

    [Fact]
    public async Task An_identical_build_leaves_the_running_app_alone()
    {
        using var app = await HotReloadHarness.StartAsync(V1);
        using var coordinator = new HotReloadCoordinator();
        await coordinator.StartAsync(app.DllPath, HotReloadRuntime.Capabilities);

        await HotReloadHarness.CompileAsync(V1 + "\n// a comment does not change the program\n", app.AssemblyName, app.DllPath);
        var attempt = await coordinator.TryAsync(app.DllPath, _ => throw new InvalidOperationException("nothing to send"));

        attempt.Applied.Should().BeTrue(attempt.Message);
        coordinator.IsActive.Should().BeTrue();
    }
}
