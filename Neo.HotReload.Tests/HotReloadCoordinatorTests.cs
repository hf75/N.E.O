using FluentAssertions;
using Neo.Agents;
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
    public async Task A_package_the_change_adds_is_patched_in_instead_of_restarting_the_app()
    {
        // A library that plays the new NuGet package.
        var libraryDirectory = Path.Combine(Path.GetTempPath(), "neo_hotreload_tests", "lib_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(libraryDirectory);
        var libraryName = "HotReloadLibrary_" + Guid.NewGuid().ToString("N")[..8];
        var libraryPath = Path.Combine(libraryDirectory, libraryName + ".dll");
        await HotReloadHarness.CompileAsync(
            "namespace Library { public static class Greeter { public static string Greet(string who) => \"Hello \" + who; } }",
            libraryName, libraryPath);

        using var app = await HotReloadHarness.StartAsync(V1);
        using var coordinator = new HotReloadCoordinator();
        (await coordinator.StartAsync(app.DllPath, HotReloadRuntime.Capabilities)).Should().BeNull();
        app.Call("Click");

        var v2 = V1.Replace("\"v1 clicks=\" + Clicks", "Library.Greeter.Greet(\"v2\") + \" clicks=\" + Clicks");
        await HotReloadHarness.CompileAsync(v2, app.AssemblyName, app.DllPath, additionalDlls: [libraryPath]);

        var attempt = await coordinator.TryAsync(app.DllPath, request =>
        {
            app.ProvideDependency(libraryPath); // the hosts stream new dependencies before the request
            return Task.FromResult<HotReloadResultMessage?>(app.ApplyLikeAPluginWindow(request));
        });

        attempt.Applied.Should().BeTrue(attempt.Message);
        attempt.Message.Should().Contain(libraryName + ".dll", "the log names the dependency that came with the update");
        app.Call("Describe").Should().Be("Hello v2 clicks=1", "the patched code runs against the new library, the click survived");
    }

    [Fact]
    public async Task A_type_in_a_new_source_file_is_patched_in_too()
    {
        using var app = await HotReloadHarness.StartAsync(V1);
        using var coordinator = new HotReloadCoordinator();
        (await coordinator.StartAsync(app.DllPath, HotReloadRuntime.Capabilities)).Should().BeNull();
        app.Call("Click");

        var v2 = V1.Replace("\"v1 clicks=\" + Clicks", "Formatter.Format(Clicks)");
        const string newFile = "public static class Formatter { public static string Format(int clicks) => \"v2 clicks=\" + clicks; }";
        await HotReloadHarness.CompileAsync([v2, newFile], app.AssemblyName, app.DllPath);

        var attempt = await coordinator.TryAsync(app.DllPath, request => Task.FromResult<HotReloadResultMessage?>(app.ApplyLikeAPluginWindow(request)));

        attempt.Applied.Should().BeTrue(attempt.Message);
        app.Call("Describe").Should().Be("v2 clicks=1");
    }

    [Fact]
    public async Task A_reference_listed_twice_does_not_keep_hot_reload_from_starting()
    {
        // Field report: the WPF host listed its AI agent DLL twice. Roslyn's workspace rejected the
        // project ("duplicate items"), the session never started, and every change reloaded the
        // app - the todo items the user had entered were gone after the next prompt.
        using var app = await HotReloadHarness.StartAsync(V1);
        var baseline = HotReloadBaseline.TryLoad(app.DllPath)!;
        var twice = baseline with { ReferencePaths = [.. baseline.ReferencePaths, baseline.ReferencePaths[0].ToUpperInvariant()] };

        using var session = await HotReloadSession.StartAsync(twice, HotReloadRuntime.Capabilities ?? "Baseline");
        var update = await session.CreateUpdateAsync(
            [new HotReloadSource(twice.Sources[0].Path, V1.Replace("\"v1 clicks=\"", "\"v2 clicks=\""))],
            twice.ReferencePaths);

        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);
        update.AddedReferences.Should().BeEmpty("the same reference, listed twice, is not a new one");
    }

    [Fact]
    public async Task The_compile_agent_lists_every_reference_once()
    {
        var directory = Path.Combine(Path.GetTempPath(), "neo_hotreload_tests", "dup_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var libraryPath = Path.Combine(directory, "Library.dll");
        await HotReloadHarness.CompileAsync("public static class Library { }", "Library", libraryPath);

        var dllPath = Path.Combine(directory, "App.dll");
        await HotReloadHarness.CompileAsync(V1, "App", dllPath, additionalDlls: [libraryPath, libraryPath]);

        var references = HotReloadBaselineFile.TryRead(dllPath)!.ReferencePaths;
        references.Should().OnlyHaveUniqueItems().And.ContainSingle(p => p.EndsWith("Library.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_app_that_could_not_be_armed_says_why_on_the_next_change()
    {
        // Hot reload must never fail silently: the host reports every attempt while an app is
        // running, and the attempt names the reason the app could not be armed.
        using var app = await HotReloadHarness.StartAsync(V1);
        File.Delete(HotReloadBaselineFile.PathFor(app.DllPath));

        using var coordinator = new HotReloadCoordinator();
        (await coordinator.StartAsync(app.DllPath, HotReloadRuntime.Capabilities)).Should().Be("the DLL was not built for hot reload");
        coordinator.HasRunningApp.Should().BeTrue("the host has to report the next attempt");

        var attempt = await coordinator.TryAsync(app.DllPath, _ => Task.FromResult<HotReloadResultMessage?>(null));

        attempt.Applied.Should().BeFalse();
        attempt.Message.Should().Be("Hot reload not possible — the DLL was not built for hot reload. Reloading the app.");

        await coordinator.ResetAsync();
        coordinator.HasRunningApp.Should().BeFalse("after the app is gone there is nothing to report");
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
