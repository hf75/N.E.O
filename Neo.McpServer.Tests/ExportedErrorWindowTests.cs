using System.Diagnostics;
using FluentAssertions;
using Neo.McpServer.Services;
using Neo.Tests;
using Xunit;

namespace Neo.McpServer.Tests;

/// <summary>
/// An exported app has no N.E.O. behind it to repair it: an exception nobody caught is shown in a window
/// and the app goes on (Neo.ErrorSurfacing, NeoErrorWindow) — one window at a time, an error seen once not
/// again. Real programs from export_app; their windows are counted and closed through Win32, without input.
/// The WPF export: Neo.App.Core.Tests/WpfExportErrorWindowTests.
/// </summary>
[Collection("ExportedProcesses")]
[Trait("Category", "UiSmoke")]
public class ExportedErrorWindowTests : IDisposable
{
    private const string ErrorTitle = "An unexpected error occurred";

    private readonly string _exportRoot =
        Path.Combine(Path.GetTempPath(), "neo_error_window_tests_" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public async Task A_timer_that_throws_on_every_tick_shows_one_window_and_the_app_goes_on()
    {
        if (!OperatingSystem.IsWindows()) return;

        var app = await StartAsync("timer-error", """
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
            """);

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

    [Fact]
    public async Task An_error_in_the_constructor_takes_the_main_windows_place_and_closing_it_ends_the_app()
    {
        if (!OperatingSystem.IsWindows()) return;

        var app = await StartAsync("ctor-error", """
            using System;
            using Avalonia.Controls;

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
    public async Task An_error_on_another_thread_is_shown_before_the_app_ends()
    {
        if (!OperatingSystem.IsWindows()) return;

        var app = await StartAsync("thread-error", """
            using System;
            using System.Threading;
            using Avalonia.Controls;

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

    private async Task<Process> StartAsync(string appName, string source)
    {
        var result = await new CompilationPipeline().ExportAsync(
            new[] { source }, appName, _exportRoot, platform: "windows", nugetPackages: null, mcpMode: false);
        result.Success.Should().BeTrue("export should succeed, errors: " + string.Join(" | ", result.Errors));

        var process = Process.Start(new ProcessStartInfo { FileName = result.ExePath!, UseShellExecute = false })!;
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
