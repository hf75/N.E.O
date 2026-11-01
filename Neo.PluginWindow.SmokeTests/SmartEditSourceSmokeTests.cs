using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;
using FluentAssertions;
using Neo.McpServer.Tests;
using Xunit;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// Smart Edit (Ctrl+K) changes the app inside its window, where the server cannot see it, and whatever
/// Claude does next through the server has to start from that change. The real server, a real window and
/// real keystrokes; only the Anthropic API is a stand-in, so this costs nothing.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class SmartEditSourceSmokeTests : IDisposable
{
    private const string Logic = "_value * 3 + 1";

    private const string AvaloniaApp = """
        using Avalonia;
        using Avalonia.Controls;

        public class DynamicUserControl : UserControl
        {
            private int _value = 1;

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                var label = new TextBlock { Name = "label", Text = "version 1" };
                var go = new Button { Name = "go", Content = "Go" };
                go.Click += (_, _) => { _value = _value * 3 + 1; label.Text = "Value: " + _value; };
                Content = new StackPanel { Margin = new Thickness(16), Children = { go, label } };
            }
        }
        """;

    private const string WpfApp = """
        using System.Windows;
        using System.Windows.Controls;

        public class DynamicUserControl : UserControl
        {
            private int _value = 1;

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                var label = new TextBlock { Name = "label", Text = "version 1" };
                var go = new Button { Name = "go", Content = "Go" };
                go.Click += (_, _) => { _value = _value * 3 + 1; label.Text = "Value: " + _value; };
                var panel = new StackPanel { Margin = new Thickness(16) };
                panel.Children.Add(go);
                panel.Children.Add(label);
                Content = panel;
            }
        }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "neo_smart_edit_" + Guid.NewGuid().ToString("N")[..8]);

    [Theory]
    [InlineData("avalonia")]
    [InlineData("wpf")]
    public async Task What_Smart_Edit_changes_is_what_Claude_works_on_next(string framework)
    {
        var v1 = framework == "wpf" ? WpfApp : AvaloniaApp;
        string Version(int n) => v1.Replace("version 1", "version " + n);

        using var api = new StandInClaudeApi();
        using var server = StdioJsonRpcProcess.Start("dotnet", $"\"{TestEnvironment.ServerDll}\"", new Dictionary<string, string>
        {
            ["ANTHROPIC_API_KEY"] = "not-a-real-key",
            ["NEO_ANTHROPIC_BASE_URL"] = api.BaseUrl,
        });
        await server.InitializeAsync("neo-smart-edit-tests");

        async Task SucceedsAsync(string tool, object arguments)
            => (await server.CallToolAsync(tool, arguments, TimeSpan.FromMinutes(3)))
                .Should().StartWith("SUCCESS", "server stderr:\n" + server.StandardError);

        try
        {
            await SucceedsAsync("compile_and_preview", new { sourceCode = new[] { v1 }, framework });

            // A change in the window: it starts from the real source, and the server hears about the result.
            api.AnswerWithCode(Version(2));
            await SmartEditAsync(server, "Make it version 2");
            await ClaudeGetsAsync(server, Version(2));
            api.Prompts.Should().ContainSingle().Which.Should().Contain("version 1").And.Contain(Logic);

            await SucceedsAsync("save_session", new { name = "app", directory = _folder });
            using (var session = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "app.neo"))))
                session.RootElement.GetProperty("sourceCode")[0].GetString().Should().Be(Version(2));

            // A patch applies to that change, and the window gets the result for the next request.
            await SucceedsAsync("patch_preview", new { patch = ChangeLabel("version 2", "version 3") });
            await ClaudeGetsAsync(server, Version(3));
            await ShowsAsync(server, "version 3");

            api.AnswerWithCode(Version(4));
            await SmartEditAsync(server, "Make it version 4");
            await ClaudeGetsAsync(server, Version(4));
            api.Prompts[^1].Should().Contain("version 3").And.Contain(Logic);
            await ShowsAsync(server, "version 4");

            // The build hot reload knows is not the one running any more, so this is a full reload that says why ...
            var updated = await server.CallToolAsync("update_preview", new { sourceCode = new[] { Version(5) }, framework }, TimeSpan.FromMinutes(3));
            updated.Should().StartWith("SUCCESS").And.Contain("Hot reload not possible — Smart Edit");
            await ShowsAsync(server, "version 5");

            // ... and from then on hot reload works again.
            updated = await server.CallToolAsync("update_preview", new { sourceCode = new[] { Version(6) }, framework }, TimeSpan.FromMinutes(3));
            updated.Should().StartWith("SUCCESS").And.Contain("Hot reload (generation 1)");
            await ShowsAsync(server, "version 6");
            await ClaudeGetsAsync(server, Version(6));

            (await server.CallToolAsync("get_runtime_errors", new { })).Should().Contain("No runtime errors");
        }
        finally
        {
            try { await server.CallToolAsync("close_preview", new { }, TimeSpan.FromSeconds(20)); } catch { }
        }
    }

    // Line 12 of both apps.
    private static string ChangeLabel(string from, string to) => $$"""
        --- a/currentcode.cs
        +++ b/currentcode.cs
        @@ -12,1 +12,1 @@
        -        var label = new TextBlock { Name = "label", Text = "{{from}}" };
        +        var label = new TextBlock { Name = "label", Text = "{{to}}" };
        """;

    /// <summary>Asks in the overlay, as a user would. Opens it if it is not open yet.</summary>
    private static async Task SmartEditAsync(StdioJsonRpcProcess server, string request)
    {
        var window = await WaitForAsync(() => PreviewWindow(server), "the preview window");

        if (OverlayInput(window) == null)
            TopLevelWindows.Keys(window, "^k");
        var input = await WaitForAsync(() => OverlayInput(window) is { Current.IsEnabled: true } enabled ? enabled : null, "the Smart Edit input");

        TopLevelWindows.Click(window, input);
        await Task.Delay(200);
        ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).SetValue(request);
        await Task.Delay(200);
        TopLevelWindows.Keys(window, "{ENTER}");
    }

    private static AutomationElement? PreviewWindow(StdioJsonRpcProcess server)
        => TopLevelWindows.Find(w => w.Title.StartsWith("N.E.O.", StringComparison.Ordinal) && TopLevelWindows.ParentProcessId(w.Pid) == server.ProcessId);

    // The test's apps have no text box of their own.
    private static AutomationElement? OverlayInput(AutomationElement window)
        => window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

    private static async Task ClaudeGetsAsync(StdioJsonRpcProcess server, string expected)
    {
        var code = "";
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            code = await server.CallToolAsync("extract_code", new { });
            if (code == expected) return;
            await Task.Delay(300);
        }

        var said = PreviewWindow(server)?.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        var texts = said == null ? "(no window)" : string.Join(" | ", said.Cast<AutomationElement>().Select(e => e.Current.Name));
        code.Should().Be(expected, "the window says: " + texts);
    }

    private static async Task ShowsAsync(StdioJsonRpcProcess server, string text)
        => (await server.CallToolAsync("inspect_visual_tree", new { }, TimeSpan.FromSeconds(60))).Should().Contain(text);

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, string what) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            try { if (probe() is { } found) return found; } catch { }
            await Task.Delay(300);
        }
        throw new TimeoutException("not found: " + what);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }
}
