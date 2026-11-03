using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace Neo.McpServer.Tests;

/// <summary>
/// Exceptions that used to vanish in a preview window, end to end through the real server and
/// real windows: a dropped task, a DispatcherTimer tick, System.Timers.Timer. Without the
/// rewriting (Neo.ErrorSurfacing) none of them reached the window's error handling — no banner,
/// no repair, no runtime_error for Claude — and a DispatcherTimer whose tick threw stopped for good.
/// </summary>
[Collection("ExportedProcesses")]
[Trait("Category", "UiSmoke")]
public class LostExceptionProcessTests
{
    // Each one throws about half a second after the app has started (or after the click).
    private static readonly (string Name, string Start)[] s_patterns =
    [
        ("dispatcher-timer", "var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) }; timer.Tick += (_, _) => { timer.Stop(); Fail(\"DispatcherTimer.Tick\"); }; timer.Start();"),
        ("timer-run-once", "Avalonia.Threading.DispatcherTimer.RunOnce(() => Fail(\"DispatcherTimer.RunOnce\"), TimeSpan.FromMilliseconds(500));"),
        ("timer-run", "Avalonia.Threading.DispatcherTimer.Run(() => { Fail(\"DispatcherTimer.Run\"); return false; }, TimeSpan.FromMilliseconds(500));"),
        ("invoke-async", "Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => Fail(\"Dispatcher.InvokeAsync\"));"),
        ("task-run", "Task.Run(() => { Thread.Sleep(500); Fail(\"Task.Run\"); });"),
        ("async-method", "LoadAsync(); async Task LoadAsync() { await Task.Delay(500); Fail(\"async Task method\"); }"),
        ("continue-with", "Task.Delay(500).ContinueWith(_ => Fail(\"ContinueWith\"));"),
        ("system-timer", "var timer = new System.Timers.Timer(500) { AutoReset = false }; timer.Elapsed += (_, _) => Fail(\"System.Timers.Timer\"); timer.Start();"),
        ("click-handler", "var button = new Button { Name = \"load\", Content = \"Load\" }; button.Click += (_, _) => LoadAsync(); Content = button; async Task LoadAsync() { await Task.Delay(500); Fail(\"a click handler's task\"); }"),
    ];

    private static string App(string start) => $$"""
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Avalonia.Controls;

        public class DynamicUserControl : UserControl
        {
            public DynamicUserControl()
            {
                Content = new TextBlock { Text = "lost exceptions" };
                {{start}}
            }

            private static void Fail(string where) => throw new InvalidOperationException("boom from " + where);
        }
        """;

    [Fact]
    public async Task Every_exception_that_used_to_vanish_is_reported()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var server = StdioJsonRpcProcess.Start("dotnet", $"\"{McpServerProcessTests.ServerDll}\"");
        await server.InitializeAsync("neo-lost-exception-tests");

        using var scope = new AssertionScope();
        foreach (var (name, start) in s_patterns)
        {
            try
            {
                (await server.CallToolAsync("compile_and_preview", new { sourceCode = new[] { App(start) }, windowId = name }, TimeSpan.FromMinutes(3)))
                    .Should().StartWith("SUCCESS", $"{name}: server stderr:\n" + server.StandardError);
                if (name == "click-handler")
                    (await server.CallToolAsync("simulate_input", new { windowId = name, controlName = "load", kind = "click", paramsJson = "{}" }))
                        .Should().StartWith("OK", "the click itself does not throw");

                var errors = "";
                for (var i = 0; i < 40 && !errors.Contains("boom from"); i++)
                {
                    await Task.Delay(250);
                    errors = await server.CallToolAsync("get_runtime_errors", new { windowId = name });
                }
                errors.Should().Contain("boom from", $"{name}: the exception has to reach the window's error handling");
            }
            finally
            {
                await server.CallToolAsync("close_preview", new { windowId = name });
            }
        }
    }

    [Fact]
    public async Task A_DispatcherTimer_whose_tick_throws_is_reported_and_keeps_ticking()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string clock = """
            using System;
            using Avalonia.Controls;
            using Avalonia.Threading;

            public class DynamicUserControl : UserControl
            {
                private int _ticks;

                public DynamicUserControl()
                {
                    var count = new TextBlock { Name = "count", Text = "0" };
                    Content = count;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                    timer.Tick += (_, _) =>
                    {
                        _ticks++;
                        count.Text = _ticks.ToString();
                        if (_ticks == 3) throw new InvalidOperationException("boom at tick 3");
                    };
                    timer.Start();
                }
            }
            """;

        using var server = StdioJsonRpcProcess.Start("dotnet", $"\"{McpServerProcessTests.ServerDll}\"");
        await server.InitializeAsync("neo-lost-exception-tests");
        try
        {
            (await server.CallToolAsync("compile_and_preview", new { sourceCode = new[] { clock } }, TimeSpan.FromMinutes(3)))
                .Should().StartWith("SUCCESS", "server stderr:\n" + server.StandardError);
            await Task.Delay(2500);

            (await server.CallToolAsync("get_runtime_errors", new { })).Should().Contain("boom at tick 3");
            var count = await server.CallToolAsync("read_data", new { target = "count", scope = "value" });
            int.Parse(count.Split('"')[1]).Should().BeGreaterThan(5, "the timer goes on after the error, so a repair can take over");
        }
        finally
        {
            await server.CallToolAsync("close_preview", new { });
        }
    }
}
