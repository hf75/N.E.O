using FluentAssertions;
using Xunit;

namespace Neo.McpServer.Tests;

/// <summary>
/// Smart Edit runs inside the window and changes code, but the window only ever receives builds. So
/// the source has to travel along with every load and every live update. The WPF window shows what
/// it knows through extract_code; the Avalonia one rebuilds that from its UI and cannot tell.
/// </summary>
[Collection("ExportedProcesses")]
[Trait("Category", "UiSmoke")]
public class PreviewSourceProcessTests
{
    private const string V1 = """
        using System.Windows;
        using System.Windows.Controls;

        public class DynamicUserControl : UserControl
        {
            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                Content = new TextBlock { Name = "title", Text = "version 1" };
            }
        }
        """;

    private static readonly string V2 = V1.Replace("version 1", "version 2");

    private const string PatchToV3 = """
        --- a/currentcode.cs
        +++ b/currentcode.cs
        @@ -10,1 +10,1 @@
        -        Content = new TextBlock { Name = "title", Text = "version 2" };
        +        Content = new TextBlock { Name = "title", Text = "version 3" };
        """;

    [Fact]
    public async Task The_window_knows_the_source_after_a_load_an_update_and_a_patch()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var server = StdioJsonRpcProcess.Start("dotnet", $"\"{McpServerProcessTests.ServerDll}\"");
        await server.InitializeAsync("neo-preview-source-tests");

        try
        {
            (await server.CallToolAsync("compile_and_preview", new { sourceCode = new[] { V1 }, framework = "wpf" }, TimeSpan.FromMinutes(3)))
                .Should().StartWith("SUCCESS", "server stderr:\n" + server.StandardError);
            (await server.CallToolAsync("extract_code", new { })).Should().Be(V1);

            (await server.CallToolAsync("update_preview", new { sourceCode = new[] { V2 }, framework = "wpf" }, TimeSpan.FromMinutes(3)))
                .Should().StartWith("SUCCESS", "server stderr:\n" + server.StandardError);
            (await server.CallToolAsync("extract_code", new { })).Should().Be(V2);

            (await server.CallToolAsync("patch_preview", new { patch = PatchToV3 }, TimeSpan.FromMinutes(3)))
                .Should().StartWith("SUCCESS", "server stderr:\n" + server.StandardError);
            (await server.CallToolAsync("extract_code", new { })).Should().Contain("\"version 3\"").And.NotContain("\"version 2\"");
        }
        finally
        {
            try { await server.CallToolAsync("close_preview", new { }, TimeSpan.FromSeconds(20)); } catch { }
        }
    }
}
