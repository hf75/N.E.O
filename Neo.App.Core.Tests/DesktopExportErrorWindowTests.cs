using System.Diagnostics;
using FluentAssertions;
using Neo.Agents;
using Neo.AssemblyForge.Utils;
using Neo.Tests;
using Xunit;

namespace Neo.App.Core.Tests;

/// <summary>
/// The error window of apps exported from the desktop hosts (Neo.ErrorSurfacing, NeoErrorWindow): an
/// exception nobody caught is shown, and the app goes on — one window at a time, an error seen once not
/// again. Compiled as AppExportService compiles an export — the compile agent with the namespace forced
/// to "Neo" — then run; windows are counted and closed through Win32, without input. The same window in
/// the exports of the MCP server: Neo.McpServer.Tests/ExportedErrorWindowTests.
/// </summary>
[Collection("ExportedProcesses")]
[Trait("Category", "UiSmoke")]
public class DesktopExportErrorWindowTests : IDisposable
{
    private const string ErrorTitle = "An unexpected error occurred";

    private readonly string _exportRoot =
        Path.Combine(Path.GetTempPath(), "neo_desktop_export_tests_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<Process> _started = new();

    public void Dispose()
    {
        foreach (var process in _started)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.WaitForExit(5_000);
            process.Dispose();
        }
        // Set NEO_KEEP_EXPORTS=1 to look at the exported programs afterwards.
        if (Environment.GetEnvironmentVariable("NEO_KEEP_EXPORTS") == "1") return;
        try { if (Directory.Exists(_exportRoot)) Directory.Delete(_exportRoot, recursive: true); } catch { }
    }

    private const string WpfClock = """
        using System;
        using System.Windows;
        using System.Windows.Controls;
        using System.Windows.Threading;

        public class DynamicUserControl : UserControl
        {
            private int _ticks;

            public DynamicUserControl()
            {
                Content = new TextBlock { Text = "clock" };
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += (_, _) =>
                {
                    _ticks++;
                    if (Window.GetWindow(this) is Window window) window.Title = "ticks " + _ticks;
                    if (_ticks >= 3) throw new InvalidOperationException("boom at tick " + _ticks);
                };
                timer.Start();
            }
        }
        """;

    private const string AvaloniaClock = """
        using System;
        using Avalonia.Controls;
        using Avalonia.Threading;

        public class DynamicUserControl : UserControl
        {
            private int _ticks;

            public DynamicUserControl()
            {
                Content = new TextBlock { Text = "clock" };
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += (_, _) =>
                {
                    _ticks++;
                    if (TopLevel.GetTopLevel(this) is Window window) window.Title = "ticks " + _ticks;
                    if (_ticks >= 3) throw new InvalidOperationException("boom at tick " + _ticks);
                };
                timer.Start();
            }
        }
        """;

    [Theory]
    [InlineData("wpf", WpfClock)]
    [InlineData("avalonia", AvaloniaClock)]
    public async Task A_timer_that_throws_on_every_tick_shows_one_window_and_the_app_goes_on(string framework, string source)
    {
        if (!OperatingSystem.IsWindows()) return;

        var app = await StartAsync(framework, "timer-error-" + framework, source);

        var errorWindow = await WaitForErrorWindowAsync(app);
        var ticks = Ticks(app);
        await Task.Delay(1500);
        ErrorWindows(app).Should().ContainSingle("one window at a time, however often the timer throws");
        Ticks(app).Should().BeGreaterThan(ticks + 5, "the timer goes on");

        ProcessWindows.Close(errorWindow);
        await Task.Delay(500);
        ticks = Ticks(app);
        await Task.Delay(1500);
        ErrorWindows(app).Should().BeEmpty("an error seen once is not shown again, though the message names another tick");
        app.HasExited.Should().BeFalse();
        Ticks(app).Should().BeGreaterThan(ticks + 5, "the app goes on");
    }

    [Theory]
    [InlineData("wpf", "System.Windows.Controls")]
    [InlineData("avalonia", "Avalonia.Controls")]
    public async Task An_error_in_the_constructor_takes_the_main_windows_place_and_closing_it_ends_the_app(string framework, string controls)
    {
        if (!OperatingSystem.IsWindows()) return;

        var app = await StartAsync(framework, "ctor-error-" + framework, $$"""
            using System;
            using {{controls}};

            public class DynamicUserControl : UserControl
            {
                public DynamicUserControl()
                {
                    throw new InvalidOperationException("the settings file is missing");
                }
            }
            """);

        var errorWindow = await WaitForErrorWindowAsync(app);
        ProcessWindows.Of(app.Id).Should().ContainSingle("there is no main window besides it");
        await Task.Delay(1000);
        app.HasExited.Should().BeFalse("the app waits until its user has read why it cannot start");

        ProcessWindows.Close(errorWindow);
        app.WaitForExit(10_000).Should().BeTrue("closing the error ends an app that could not start");
    }

    [Fact]
    public async Task Wpf_an_error_on_another_thread_is_shown_before_the_app_ends()
    {
        if (!OperatingSystem.IsWindows()) return;

        var app = await StartAsync("wpf", "thread-error", """
            using System;
            using System.Threading;
            using System.Windows.Controls;

            public class DynamicUserControl : UserControl
            {
                public DynamicUserControl()
                {
                    Content = new TextBlock { Text = "worker" };
                    new Thread(() => { Thread.Sleep(500); throw new InvalidOperationException("the worker failed"); }) { IsBackground = true }.Start();
                }
            }
            """);

        var errorWindow = await WaitForErrorWindowAsync(app);
        await Task.Delay(1000);
        app.HasExited.Should().BeFalse("the app ends once its user has seen why");

        ProcessWindows.Close(errorWindow);
        app.WaitForExit(10_000).Should().BeTrue("an exception nobody caught on another thread ends the app");
    }

    /// <summary>The compile step of AppExportService for the given host (no NuGet packages, no icon), then the exe started.</summary>
    private async Task<Process> StartAsync(string framework, string appName, string userControl)
    {
        var avalonia = framework == "avalonia";
        var core = DotNetRuntimeFinder.GetHighestRuntimePath(DotNetRuntimeType.NetCoreApp, Environment.Version.Major)!;
        var desktop = DotNetRuntimeFinder.GetHighestRuntimePath(DotNetRuntimeType.WindowsDesktopApp, Environment.Version.Major)!;
        var output = Path.Combine(_exportRoot, appName);

        // The Avalonia host compiles against Avalonia's DLLs and puts them next to the exe; here they come from the plugin window.
        var avaloniaDlls = avalonia ? Directory.GetFiles(AvaloniaSourceTemplateTests.PluginWindowBinDir, "*.dll").ToList() : new List<string>();

        var agent = new CSharpCompileAgent();
        agent.SetOption("CoreDllPath", new List<string> { core, desktop });
        agent.SetOption("NugetPackageVersions", new Dictionary<string, string>());
        agent.SetInput("Code", new List<string>
        {
            userControl,
            avalonia ? ExportWindowBaseCode.CreateBaseCodeForExportAvalonia(appName) : ExportWindowBaseCode.CreateBaseCodeForExport(appName),
        });
        agent.SetInput("ForceNamespace", "Neo");
        agent.SetInput("OutputPath", output);
        agent.SetInput("AssemblyName", appName);
        agent.SetInput("NuGetDlls", avaloniaDlls);
        agent.SetInput("CompileType", "WINDOWS");
        agent.SetInput("AdditionalDlls", new List<string>());
        agent.SetInput("MainTypeName", avalonia ? "Neo.Program" : "Neo.App");
        agent.SetInput("ForceNetCoreRuntime", avalonia);
        agent.SetInput("AppHostApp", AppHostTemplates.EnsureExtracted(AssemblyForgeAppHostTemplate.WindowsExe));
        await agent.ExecuteAsync();

        var exe = agent.GetOutput<string>("CompiledPath");
        File.Exists(exe).Should().BeTrue($"the export compiles: {exe}");
        if (avalonia)
        {
            foreach (var file in avaloniaDlls.Concat(Directory.GetFiles(Path.Combine(AvaloniaSourceTemplateTests.PluginWindowBinDir, "runtimes", "win-x64", "native"), "*.dll")))
            {
                var target = Path.Combine(output, Path.GetFileName(file));
                if (!File.Exists(target)) File.Copy(file, target);
            }
        }

        var process = Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = false })!;
        _started.Add(process);
        return process;
    }

    private static List<IntPtr> ErrorWindows(Process app)
        => ProcessWindows.Of(app.Id).Where(w => w.Title == ErrorTitle).Select(w => w.Handle).ToList();

    private static async Task<IntPtr> WaitForErrorWindowAsync(Process app)
    {
        for (var i = 0; i < 100; i++)
        {
            var windows = ErrorWindows(app);
            if (windows.Count > 0)
            {
                windows.Should().ContainSingle("one error window at a time");
                return windows[0];
            }
            app.HasExited.Should().BeFalse("the app shows the error instead of ending");
            await Task.Delay(200);
        }
        throw new TimeoutException("No error window within 20 seconds.");
    }

    // The test app writes its tick count into its window title.
    private static int Ticks(Process app)
        => ProcessWindows.Of(app.Id).Select(w => w.Title).Where(t => t.StartsWith("ticks "))
            .Select(t => int.Parse(t["ticks ".Length..])).DefaultIfEmpty(0).Max();
}

[CollectionDefinition("ExportedProcesses", DisableParallelization = true)]
public class ExportedProcessesCollection : ICollectionFixture<ForegroundTurn> { }
