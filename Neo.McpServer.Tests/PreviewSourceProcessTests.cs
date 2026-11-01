using FluentAssertions;
using Xunit;

namespace Neo.McpServer.Tests;

/// <summary>
/// What Claude gets when it asks for the code of a running app: the real source with the app's logic
/// in it, after every kind of change, and patch_preview starts from that same text. A change made
/// inside the window needs a window driven by hand, see SmartEditSourceSmokeTests.
/// </summary>
[Collection("ExportedProcesses")]
[Trait("Category", "UiSmoke")]
public class PreviewSourceProcessTests
{
    private const string Logic = "_value * 3 + 1";

    private const string AvaloniaApp = """
        using Avalonia;
        using Avalonia.Controls;

        public class DynamicUserControl : UserControl
        {
            private int _value = 1;

            public DynamicUserControl()
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

            public DynamicUserControl()
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

    // Line 10 of both apps.
    private static string ChangeLabel(string from, string to) => $$"""
        --- a/currentcode.cs
        +++ b/currentcode.cs
        @@ -10,1 +10,1 @@
        -        var label = new TextBlock { Name = "label", Text = "{{from}}" };
        +        var label = new TextBlock { Name = "label", Text = "{{to}}" };
        """;

    [Theory]
    [InlineData("avalonia")]
    [InlineData("wpf")]
    public async Task Claude_gets_the_real_source_after_a_load_an_update_and_a_patch(string framework)
    {
        if (!OperatingSystem.IsWindows()) return;

        var v1 = framework == "wpf" ? WpfApp : AvaloniaApp;
        var v2 = v1.Replace("version 1", "version 2");
        var v3 = v1.Replace("version 1", "version 3");

        await WithServerAsync(async server =>
        {
            await SucceedsAsync(server, "compile_and_preview", new { sourceCode = new[] { v1 }, framework });
            (await server.CallToolAsync("extract_code", new { })).Should().Be(v1);

            await SucceedsAsync(server, "update_preview", new { sourceCode = new[] { v2 }, framework });
            (await server.CallToolAsync("extract_code", new { })).Should().Be(v2);

            await SucceedsAsync(server, "patch_preview", new { patch = ChangeLabel("version 2", "version 3") });
            (await server.CallToolAsync("extract_code", new { })).Should().Be(v3);

            // The window has it too: Smart Edit starts from there. WPF shows it, having no live UI to rebuild.
            if (framework == "wpf")
                (await server.CallToolAsync("extract_code", new { fromLiveUi = true })).Should().Be(v3);
        });
    }

    [Fact]
    public async Task The_live_ui_comes_back_on_request_and_says_what_it_is_not()
    {
        if (!OperatingSystem.IsWindows()) return;

        await WithServerAsync(async server =>
        {
            await SucceedsAsync(server, "compile_and_preview", new { sourceCode = new[] { AvaloniaApp }, framework = "avalonia" });
            (await server.CallToolAsync("set_property", new { target = "label", propertyName = "Text", value = "typed live" })).Should().NotContain("not found");

            var live = await server.CallToolAsync("extract_code", new { fromLiveUi = true });
            live.Should().Contain("typed live", "the live UI is what this is for");
            live.Should().Contain("NO event handlers", "whoever reads this must know that the logic is missing");
            live.Should().NotContain(Logic);

            (await server.CallToolAsync("extract_code", new { })).Should().Be(AvaloniaApp, "the source is not changed by what happens in the running app");
        });
    }

    [Fact]
    public async Task Every_window_has_its_own_source()
    {
        if (!OperatingSystem.IsWindows()) return;

        // In this order: Avalonia code does not compile as WPF, while on Windows WPF code does compile as Avalonia.
        var left = AvaloniaApp.Replace("version 1", "left window");
        var right = WpfApp.Replace("version 1", "right window");

        await WithServerAsync(async server =>
        {
            try
            {
                await SucceedsAsync(server, "compile_and_preview", new { sourceCode = new[] { left }, framework = "avalonia", windowId = "left" });
                await SucceedsAsync(server, "compile_and_preview", new { sourceCode = new[] { right }, framework = "wpf", windowId = "right" });

                (await server.CallToolAsync("extract_code", new { windowId = "left" })).Should().Be(left);
                (await server.CallToolAsync("extract_code", new { windowId = "right" })).Should().Be(right);

                // Not the window compiled last, and not its framework either: compiled as WPF, Avalonia would come from NuGet.
                (await server.CallToolAsync("patch_preview", new { patch = ChangeLabel("left window", "left window patched"), windowId = "left" }, TimeSpan.FromMinutes(3)))
                    .Should().StartWith("SUCCESS").And.Contain("Dependencies: 0");
                (await server.CallToolAsync("inspect_visual_tree", new { windowId = "left" }, TimeSpan.FromSeconds(60))).Should().Contain("left window patched");
                (await server.CallToolAsync("extract_code", new { windowId = "left" })).Should().Contain("left window patched").And.Contain(Logic);
                (await server.CallToolAsync("extract_code", new { windowId = "right" })).Should().Be(right);
            }
            finally
            {
                try { await server.CallToolAsync("close_preview", new { windowId = "left" }, TimeSpan.FromSeconds(20)); } catch { }
                try { await server.CallToolAsync("close_preview", new { windowId = "right" }, TimeSpan.FromSeconds(20)); } catch { }
            }
        });
    }

    [Fact]
    public async Task An_app_of_several_files_comes_back_file_by_file()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string helper = """
            using System;

            public static class Helper
            {
                public static string Now() => DateTime.Now.ToString();
            }
            """;

        await WithServerAsync(async server =>
        {
            await SucceedsAsync(server, "compile_and_preview", new { sourceCode = new[] { AvaloniaApp, helper }, framework = "avalonia" });

            var code = await server.CallToolAsync("extract_code", new { });
            code.Should().Be($"// ===== File 1 of 2 =====\n{AvaloniaApp}\n// ===== File 2 of 2 =====\n{helper}");
        });
    }

    private static async Task SucceedsAsync(StdioJsonRpcProcess server, string tool, object arguments)
        => (await server.CallToolAsync(tool, arguments, TimeSpan.FromMinutes(3)))
            .Should().StartWith("SUCCESS", "server stderr:\n" + server.StandardError);

    private static async Task WithServerAsync(Func<StdioJsonRpcProcess, Task> test)
    {
        using var server = StdioJsonRpcProcess.Start("dotnet", $"\"{McpServerProcessTests.ServerDll}\"");
        await server.InitializeAsync("neo-preview-source-tests");
        try
        {
            await test(server);
        }
        finally
        {
            try { await server.CallToolAsync("close_preview", new { }, TimeSpan.FromSeconds(20)); } catch { }
        }
    }
}
