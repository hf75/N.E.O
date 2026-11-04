using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Neo.McpServer.Tests;

/// <summary>
/// The Claude bridge end to end through the real server: an app's Ai.Trigger arrives as a typed prompt in a stand-in
/// for the Claude window, which names its controls the way the Claude desktop app does ("Prompt", "Send", "Stop",
/// "{title}, rename session"). The prompt waits while another session is in front, while Claude works and while the
/// box holds the person's draft. The real Claude window is never touched: the bridge docks to the stand-in by its title.
/// </summary>
[Collection("ExportedProcesses")]
[Trait("Category", "UiSmoke")]
public class ClaudeBridgeProcessTests
{
    private const string PreviewTitle = "N.E.O. — Live Preview (MCP)";

    private readonly string _claudeTitle = "FakeClaude" + Guid.NewGuid().ToString("N")[..8];

    private string FakeClaude => $$"""
        using System.Collections.Generic;
        using Avalonia;
        using Avalonia.Automation;
        using Avalonia.Controls;
        using Avalonia.Layout;
        using Neo.App;

        public class DynamicUserControl : UserControl
        {
            private readonly StackPanel _header = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
            private readonly StackPanel _composer = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
            private readonly TextBox _prompt = new() { Width = 320, AcceptsReturn = true };
            private readonly Button _send = new() { Content = "Send" };
            private readonly Button _stop = new() { Content = "Stop" };
            private readonly List<string> _sent = new();

            public DynamicUserControl()
            {
                AutomationProperties.SetName(_prompt, "Prompt");
                _prompt.TextChanged += (_, _) => ShowSend();
                _send.Click += (_, _) => { _sent.Add(_prompt.Text ?? ""); _prompt.Text = ""; };
                Show("Session A");
                _composer.Children.Add(_prompt);
                Content = new StackPanel { Margin = new Thickness(12), Spacing = 8, Children = { _header, _composer } };
                AttachedToVisualTree += (_, _) => { if (TopLevel.GetTopLevel(this) is Window window) window.Title = "{{_claudeTitle}}"; };
            }

            [McpCallable("Puts a session in front.")]
            public void Show(string session)
            {
                var title = new Button { Content = session };
                AutomationProperties.SetName(title, session + ", rename session");
                _header.Children.Clear();
                _header.Children.Add(title);
            }

            [McpCallable("Claude starts or ends working.")]
            public void Busy(bool busy)
            {
                _composer.Children.Remove(_stop);
                if (busy) _composer.Children.Add(_stop);
            }

            [McpCallable("The person types into the prompt box.")]
            public void Draft(string text) => _prompt.Text = text;

            [McpObservable("What was sent, oldest first.")]
            public string[] Sent => _sent.ToArray();

            [McpObservable("What the prompt box holds.")]
            public string Box => _prompt.Text ?? "";

            // As in the Claude window: Send is there only while the box holds something.
            private void ShowSend()
            {
                _composer.Children.Remove(_send);
                if (!string.IsNullOrEmpty(_prompt.Text)) _composer.Children.Add(_send);
            }
        }
        """;

    private const string App = """
        using Avalonia.Controls;
        using Neo.App;

        public class DynamicUserControl : UserControl
        {
            public DynamicUserControl() => Content = new TextBlock { Text = "an app that asks Claude" };

            [McpCallable("Sends a prompt to Claude.")]
            public void Fire(string prompt) => Ai.Trigger(prompt);
        }
        """;

    [Fact]
    public async Task A_prompt_of_Ai_Trigger_is_typed_in_when_its_session_is_in_front_Claude_is_idle_and_the_box_is_empty()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var server = await StartAsync(bringToFront: false);
        try
        {
            // At the lower right of the stand-in, shorter than it: the title bar and its buttons stay free.
            await WaitForAsync(() => Task.FromResult(Windows.DockedAtLowerRight(_claudeTitle, PreviewTitle)), "the bridge to dock to the Claude window");

            // The server talks to the bridge; Claude gets no tools of it.
            (await server.ListToolNamesAsync()).Should().NotContain(name => name.Contains("deliver", StringComparison.OrdinalIgnoreCase));

            // Another session in front: the prompt waits.
            await CallAsync(server, "claude", "Show", "[\"Session B\"]");
            await CallAsync(server, "app", "Fire", "[\"first prompt\"]");
            await WaitForAsync(async () => (await StateAsync(server)).Contains("\"Session A\" to be in front"), "the bridge to wait for its session");
            (await SentAsync(server)).Should().BeEmpty();

            // Its session comes to the front: typed in and sent, saying where it is from.
            await CallAsync(server, "claude", "Show", "[\"Session A\"]");
            await WaitForAsync(async () => (await SentAsync(server)).Length == 1, "the first prompt to be sent");
            (await SentAsync(server))[0].Should().Be("From the N.E.O. app \"app\":\nfirst prompt");

            // Claude works: the next prompt waits until it is done.
            await CallAsync(server, "claude", "Busy", "[true]");
            await CallAsync(server, "app", "Fire", "[\"second prompt\"]");
            await WaitForAsync(async () => (await StateAsync(server)).Contains("Claude is working"), "the bridge to wait while Claude works");
            (await SentAsync(server)).Should().HaveCount(1);
            await CallAsync(server, "claude", "Busy", "[false]");
            await WaitForAsync(async () => (await SentAsync(server)).Length == 2, "the second prompt to be sent");

            // The person writes: the draft stays theirs, the prompt waits.
            await CallAsync(server, "claude", "Draft", "[\"my own words\"]");
            await CallAsync(server, "app", "Fire", "[\"third prompt\"]");
            await WaitForAsync(async () => (await StateAsync(server)).Contains("draft"), "the bridge to wait for the draft");
            (await ObservableAsync(server, "claude", "Box")).Should().Be("\"my own words\"");
            await CallAsync(server, "claude", "Draft", "[\"\"]");
            await WaitForAsync(async () => (await SentAsync(server)).Length == 3, "the third prompt to be sent");
            (await SentAsync(server))[2].Should().Be("From the N.E.O. app \"app\":\nthird prompt");

            // close_all_windows closes the apps, the bridge stays.
            await server.CallToolAsync("close_all_windows", new { }, TimeSpan.FromSeconds(60));
            (await server.CallToolAsync("list_windows", new { })).Should().Contain("claude-bridge: RUNNING");
        }
        finally
        {
            await CloseAsync(server);
        }
    }

    [Fact]
    public async Task A_session_in_the_background_is_brought_to_the_front_and_followed_when_it_was_renamed()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var server = await StartAsync(bringToFront: true);
        try
        {
            // Someone writes in another session: that one stays in front.
            await CallAsync(server, "claude", "Show", "[\"Session B\"]");
            await CallAsync(server, "claude", "Draft", "[\"typing in B\"]");
            await CallAsync(server, "app", "Fire", "[\"first prompt\"]");
            await WaitForAsync(async () => (await StateAsync(server)).Contains("holds a draft"), "the bridge to leave the session in front alone");
            (await OpenedAsync(server)).Should().Be(0);

            // Nobody writes: the bridge opens its session's link (the stand-in only counts it), and the session comes.
            await CallAsync(server, "claude", "Draft", "[\"\"]");
            await WaitForAsync(async () => await OpenedAsync(server) == 1, "the bridge to open the session's link");
            await CallAsync(server, "claude", "Show", "[\"Session A\"]");
            await WaitForAsync(async () => (await SentAsync(server)).Length == 1, "the first prompt to be sent");

            // Meanwhile the session was renamed: it comes to the front under its new title, and the bridge follows it.
            await CallAsync(server, "claude", "Show", "[\"Session C\"]");
            await CallAsync(server, "app", "Fire", "[\"second prompt\"]");
            await WaitForAsync(async () => await OpenedAsync(server) == 2, "the bridge to open the link again");
            await CallAsync(server, "claude", "Show", "[\"Session A, renamed\"]");
            await WaitForAsync(async () => (await SentAsync(server)).Length == 2, "the second prompt to be sent");
            JsonSerializer.Deserialize<string>(await ObservableAsync(server, "claude-bridge", "Session")).Should().Be("Session A, renamed");

            // A link that brings nothing: the prompt waits, and the bridge does not try again at once.
            await CallAsync(server, "claude", "Show", "[\"Session D\"]");
            await CallAsync(server, "app", "Fire", "[\"third prompt\"]");
            await WaitForAsync(async () => (await StateAsync(server)).Contains("the last try did not bring it"), "the bridge to give the link up for a while");
            (await OpenedAsync(server)).Should().Be(3);
            (await SentAsync(server)).Should().HaveCount(2);
        }
        finally
        {
            await CloseAsync(server);
        }
    }

    // The real server with the stand-in for the Claude window, an app that calls Ai.Trigger, and the bridge for "Session A".
    private async Task<StdioJsonRpcProcess> StartAsync(bool bringToFront)
    {
        var server = StdioJsonRpcProcess.Start("dotnet", $"\"{McpServerProcessTests.ServerDll}\"", new Dictionary<string, string>
        {
            ["NEO_CLAUDE_BRIDGE_TARGET"] = _claudeTitle,
            ["NEO_LIVEMCP_MAX_DEPTH"] = "100", // the test drives the stand-in more often than loop protection allows Claude
        });
        try
        {
            await server.InitializeAsync("neo-claude-bridge-tests");
            (await server.CallToolAsync("compile_and_preview", new { sourceCode = new[] { FakeClaude }, framework = "avalonia", windowId = "claude" }, TimeSpan.FromMinutes(3)))
                .Should().StartWith("SUCCESS", "server stderr:\n" + server.StandardError);
            (await server.CallToolAsync("position_window", new { windowId = "claude", x = 100, y = 100, width = 700, height = 560 }))
                .Should().Contain("positioned");
            (await server.CallToolAsync("compile_and_preview", new { sourceCode = new[] { App }, framework = "avalonia", windowId = "app" }, TimeSpan.FromMinutes(3)))
                .Should().StartWith("SUCCESS", "server stderr:\n" + server.StandardError);

            var started = await server.CallToolAsync("start_claude_bridge",
                new { sessionTitle = "Session A", sessionLink = "claude://claude.ai/epitaxy/local_test", bringToFront }, TimeSpan.FromMinutes(3));
            started.Should().Contain("The Claude bridge runs", "server stderr:\n" + server.StandardError);
            return server;
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    private static async Task CloseAsync(StdioJsonRpcProcess server)
    {
        try { await server.CallToolAsync("close_preview", new { windowId = "claude-bridge" }, TimeSpan.FromSeconds(20)); } catch { /* the server is killed anyway */ }
        try { await server.CallToolAsync("close_all_windows", new { }, TimeSpan.FromSeconds(20)); } catch { }
    }

    private static async Task<int> OpenedAsync(StdioJsonRpcProcess server) =>
        JsonSerializer.Deserialize<int>(await ObservableAsync(server, "claude-bridge", "Opened"));

    private static async Task CallAsync(StdioJsonRpcProcess server, string windowId, string method, string argsJson)
    {
        var result = await server.CallToolAsync("invoke_method", new { method, argsJson, windowId });
        result.Should().StartWith("OK", $"{method} on '{windowId}'");
    }

    private static Task<string> ObservableAsync(StdioJsonRpcProcess server, string windowId, string observable) =>
        server.CallToolAsync("read_observable", new { observable, windowId });

    private static async Task<string[]> SentAsync(StdioJsonRpcProcess server) =>
        JsonSerializer.Deserialize<string[]>(await ObservableAsync(server, "claude", "Sent")) ?? [];

    private static async Task<string> StateAsync(StdioJsonRpcProcess server) =>
        JsonSerializer.Deserialize<string>(await ObservableAsync(server, "claude-bridge", "State")) ?? "";

    private static async Task WaitForAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(250);
        }
        throw new TimeoutException($"Waited 20 s for {what}.");
    }

    /// <summary>Where the windows stand, in physical pixels like ForeignApp measures them.</summary>
    private static class Windows
    {
        public static bool DockedAtLowerRight(string targetTitle, string panelTitle)
        {
            var previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); // per monitor: no scaled coordinates
            try
            {
                var target = TopLevel().FirstOrDefault(w => w.Title == targetTitle);
                if (target.Handle == IntPtr.Zero) return false;
                DwmGetWindowAttribute(target.Handle, 9, out var frame, Marshal.SizeOf<Rect>());

                return TopLevel().Where(w => w.Title == panelTitle).Any(w =>
                {
                    GetWindowRect(w.Handle, out var panel);
                    return Math.Abs(panel.Left - frame.Right) <= 2 && Math.Abs(panel.Bottom - frame.Bottom) <= 2
                        && panel.Bottom - panel.Top < frame.Bottom - frame.Top;
                });
            }
            finally
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }

        private static List<(IntPtr Handle, string Title)> TopLevel()
        {
            var windows = new List<(IntPtr, string)>();
            EnumWindows((handle, _) =>
            {
                if (IsWindowVisible(handle))
                {
                    var text = new StringBuilder(256);
                    GetWindowText(handle, text, text.Capacity);
                    windows.Add((handle, text.ToString()));
                }
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
    }
}
