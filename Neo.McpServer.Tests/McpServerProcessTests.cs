using FluentAssertions;
using Neo.AssemblyForge;
using Xunit;

namespace Neo.McpServer.Tests;

/// <summary>
/// Neo.McpServer end to end, the way Claude Code uses it: start the real server process, speak
/// MCP over stdio, let it compile an app and show it in a real preview window, then look into
/// that window through the server. This is the only test that covers the whole chain
/// server → Roslyn → child process → pipe → plugin load → inspection in one go; the MCP client a
/// developer has registered usually runs an older build and cannot tell them whether the
/// current sources still work.
///
/// <para>Needs the built server and the built Avalonia MCP plugin window (build-order references
/// in the csproj). Opens a window, hence UiSmoke.</para>
/// </summary>
[Collection("ExportedProcesses")]
[Trait("Category", "UiSmoke")]
public class McpServerProcessTests
{
    // Uses what only the shipped Avalonia major has (PlaceholderText, IsCheckedChanged), so a
    // server that still compiled against the previous major would fail right here.
    private const string AppSource = """
        using Avalonia;
        using Avalonia.Controls;

        public class DynamicUserControl : UserControl
        {
            public DynamicUserControl()
            {
                var status = new TextBlock { Name = "status", Text = "unchecked" };
                var check = new CheckBox { Name = "agree", Content = "I agree" };
                check.IsCheckedChanged += (_, _) => status.Text = check.IsChecked == true ? "checked" : "unchecked";

                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Spacing = 8,
                    Children =
                    {
                        new TextBox { Name = "search", PlaceholderText = "neo-e2e-placeholder" },
                        check,
                        status,
                    }
                };
            }
        }
        """;

    [Fact]
    public async Task Server_compiles_previews_and_inspects_an_app_over_stdio()
    {
        if (!OperatingSystem.IsWindows()) return;

        File.Exists(ServerDll).Should().BeTrue($"the server has to be built: {ServerDll}");
        using var server = StdioJsonRpcProcess.Start("dotnet", $"\"{ServerDll}\"");

        var init = await server.InitializeAsync("neo-mcp-server-tests");
        init.GetProperty("serverInfo").GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();

        // Every static tool has to arrive at the client, not only the ones this test calls: the
        // server merges them with the per-app tools of running previews into one collection.
        (await server.ListToolNamesAsync()).Should()
            .Contain(["compile_and_preview", "inspect_visual_tree", "get_preview_status", "close_preview"])
            .And.HaveCountGreaterThanOrEqualTo(30);

        // The prompt the server hands to the model names the Avalonia version it really ships.
        var prompt = await server.RequestAsync("prompts/get", new
        {
            name = "create_avalonia_app",
            arguments = new { userRequest = "a check box" },
        });
        var promptText = prompt.GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString();
        promptText.Should().Contain("Avalonia " + AvaloniaGuidance.Version);
        promptText.Should().Contain("NativeWebView");

        try
        {
            var compiled = await server.CallToolAsync("compile_and_preview",
                new { sourceCode = new[] { AppSource }, framework = "avalonia" }, TimeSpan.FromMinutes(3));
            compiled.Should().StartWith("SUCCESS", "server stderr:\n" + server.StandardError);

            var status = await server.CallToolAsync("get_preview_status", new { });
            status.Should().Contain("Avalonia " + AvaloniaGuidance.Version);

            var tree = await server.CallToolAsync("inspect_visual_tree", new { }, TimeSpan.FromSeconds(60));
            tree.Should().Contain("neo-e2e-placeholder", "the visual tree of the running app comes back through the pipe");
            tree.Should().Contain("agree");
        }
        finally
        {
            try { await server.CallToolAsync("close_preview", new { }, TimeSpan.FromSeconds(20)); } catch { /* server is killed anyway */ }
        }
    }

    // <repo>/Neo.McpServer.Tests/bin/<Configuration>/<tfm>/  →  <repo>/Neo.McpServer/bin/<Configuration>/<tfm>/
    private static string ServerDll
    {
        get
        {
            var testBin = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var tfm = Path.GetFileName(testBin);
            var configuration = Path.GetFileName(Path.GetDirectoryName(testBin)!);
            var repoRoot = Path.GetFullPath(Path.Combine(testBin, "..", "..", "..", ".."));
            return Path.Combine(repoRoot, "Neo.McpServer", "bin", configuration, tfm, "Neo.McpServer.dll");
        }
    }
}
