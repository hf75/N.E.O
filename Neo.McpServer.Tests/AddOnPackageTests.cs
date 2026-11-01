using FluentAssertions;
using Neo.AssemblyForge;
using Neo.McpServer.Services;
using Xunit;

namespace Neo.McpServer.Tests;

/// <summary>
/// An Avalonia add-on depends on Avalonia, and everything behind that is 2.3 GB of native libraries
/// for every platform. The plugin window ships all of it, so only the add-on itself comes from NuGet
/// (real NuGet: the test downloads the add-on).
/// </summary>
public class AddOnPackageTests
{
    private const string App = """
        using Avalonia.Controls;

        public class DynamicUserControl : UserControl
        {
            public DynamicUserControl() { Content = new DataGrid(); }
        }
        """;

    [Fact]
    public async Task An_add_on_brings_itself_and_nothing_the_window_ships()
    {
        var pipeline = new CompilationPipeline();

        var result = await pipeline.CompileAsync(
            [App], new Dictionary<string, string> { ["Avalonia.Controls.DataGrid"] = AvaloniaGuidance.Version }, framework: "avalonia");

        result.Errors.Should().BeEmpty();
        result.DependencyDllPaths.Select(Path.GetFileName).Should().BeEquivalentTo(["Avalonia.Controls.DataGrid.dll"]);
        Directory.GetDirectories(Path.Combine(pipeline.NuGetFolder!, ".nuget_cache")).Select(Path.GetFileName)
            .Should().BeEquivalentTo(["avalonia.controls.datagrid"], "nothing else was downloaded");
    }

    [Fact]
    public async Task What_a_server_that_is_gone_left_behind_is_removed()
    {
        // The pile a server used to leave: packages right in the shared folder, and a folder of a process that is gone.
        var root = Path.Combine(Path.GetTempPath(), "neo_mcp_nuget");
        var leftOver = Directory.CreateDirectory(Path.Combine(root, "left-over-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        File.WriteAllText(Path.Combine(leftOver, "package.dll"), "");

        var pipeline = new CompilationPipeline();
        (await pipeline.CompileAsync([App.Replace("new DataGrid()", "new Button()")], framework: "avalonia")).Errors.Should().BeEmpty();

        Directory.Exists(pipeline.NuGetFolder).Should().BeTrue("the running server keeps its own");
        Path.GetFileName(pipeline.NuGetFolder).Should().Be(Environment.ProcessId.ToString());

        // In the background: a first compile does not wait for it.
        for (int i = 0; i < 100 && Directory.Exists(leftOver); i++)
            await Task.Delay(100);
        Directory.Exists(leftOver).Should().BeFalse();
    }
}
