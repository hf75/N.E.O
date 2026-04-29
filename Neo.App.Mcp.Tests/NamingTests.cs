using FluentAssertions;
using Neo.App.Mcp.Internal;
using Xunit;

namespace Neo.App.Mcp.Tests;

/// <summary>
/// Frozen-Mode tool/resource naming. Convention is intentionally identical to Dev-Mode's
/// <c>LiveMcpToolRegistry</c>'s snake_case mapping minus the per-app prefix — a Frozen EXE is
/// one app, so tools surface as plain <c>add_item</c> instead of <c>app.default.add_item</c>.
/// </summary>
public class NamingTests
{
    [Fact]
    public void ToSnakeCase_MatchesDevModeConvention()
    {
        Naming.ToSnakeCase("AddItem").Should().Be("add_item");
        Naming.ToSnakeCase("CompleteItem").Should().Be("complete_item");
        Naming.ToSnakeCase("RefreshFromAPI").Should().Be("refresh_from_api");
        Naming.ToSnakeCase("HTTP2Client").Should().Be("http2_client");
        Naming.ToSnakeCase("X").Should().Be("x");
        Naming.ToSnakeCase("count").Should().Be("count");
        Naming.ToSnakeCase("").Should().Be("");
    }

    [Fact]
    public void BuildResourceUri_UsesAppScheme_WithObservableNamePreserved()
    {
        Naming.BuildResourceUri("CompletedCount").Should().Be("app://CompletedCount");
        Naming.BuildResourceUri("ItemCount").Should().Be("app://ItemCount");
    }
}
