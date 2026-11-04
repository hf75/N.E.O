using FluentAssertions;
using Neo.McpServer.Services;
using Xunit;

namespace Neo.McpServer.Tests;

/// <summary>
/// The Claude bridge as far as it needs no window: it compiles the way start_claude_bridge builds it, with the
/// ForeignApp helper as source whatever the preview window ships, and building it leaves the user's last app alone.
/// With real windows: ClaudeBridgeProcessTests.
/// </summary>
public class ClaudeBridgeTests
{
    [Fact]
    public async Task The_bridge_compiles_with_the_helper_as_source()
    {
        var result = await new CompilationPipeline().CompileAsync(ClaudeBridgeApp.Sources, framework: "avalonia", remember: false);

        result.Errors.Should().BeEmpty();
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Building_the_bridge_leaves_the_last_app_alone()
    {
        const string app = "using Avalonia.Controls; public class DynamicUserControl : UserControl { }";
        var pipeline = new CompilationPipeline();

        (await pipeline.CompileAsync([app], framework: "avalonia")).Success.Should().BeTrue();
        (await pipeline.CompileAsync(ClaudeBridgeApp.Sources, framework: "avalonia", remember: false)).Success.Should().BeTrue();

        pipeline.LastSourceCode.Should().Equal(app);
    }
}
