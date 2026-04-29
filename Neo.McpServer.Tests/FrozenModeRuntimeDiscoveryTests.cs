using FluentAssertions;
using Neo.McpServer.Services;
using Xunit;

namespace Neo.McpServer.Tests;

/// <summary>
/// Phase 4B export pipeline relies on <see cref="CompilationPipeline.DiscoverNeoAppMcpRuntime"/>
/// finding the right DLLs to ship alongside a Frozen-Mode EXE. The test runs from
/// <c>Neo.McpServer.Tests/bin/Debug/net9.0</c>, which transitively pulls Neo.App.Mcp's runtime
/// closure via the project reference chain — so the production discovery code-path (scan the
/// running executable's bin folder) is exercised end-to-end here.
///
/// <para>If a future package upgrade renames or removes one of the closure DLLs, this test will
/// fail loudly before the regression hits a real export.</para>
/// </summary>
public class FrozenModeRuntimeDiscoveryTests
{
    [Fact]
    public void DiscoverNeoAppMcpRuntime_FindsAllExpectedClosureDlls()
    {
        var dlls = CompilationPipeline.DiscoverNeoAppMcpRuntime();
        var names = dlls.Select(Path.GetFileName).ToList();

        // The non-negotiable core: Neo.App.Mcp itself + the MCP SDK + Hosting.
        names.Should().Contain("Neo.App.Mcp.dll");
        names.Should().Contain("Neo.App.Api.dll");
        names.Should().Contain("ModelContextProtocol.dll");
        names.Should().Contain("ModelContextProtocol.Core.dll");
        names.Should().Contain("Microsoft.Extensions.Hosting.dll");
        names.Should().Contain("Microsoft.Extensions.Hosting.Abstractions.dll");
        names.Should().Contain("Microsoft.Extensions.Logging.dll");
        names.Should().Contain("Microsoft.Extensions.DependencyInjection.dll");

        // Sanity: the prefix-allowlist must NOT pull in unrelated McpServer dependencies
        // (Anthropic SDK, OpenAI SDK, AssemblyForge etc.) — they would bloat every Frozen EXE.
        names.Should().NotContain("Neo.AssemblyForge.dll");
        names.Should().NotContain("Neo.IPC.dll");
        names.Should().NotContain("Neo.Agents.Core.dll");
    }

    [Fact]
    public void DiscoverNeoAppMcpRuntime_DoesNotIncludeAvaloniaDlls()
    {
        // Avalonia ships via the existing _avaloniaAdditionalDlls path — this discovery returns
        // the Frozen-Mode-specific delta only. Double-shipping Avalonia would not break correctness
        // (Copy is no-op-on-exists) but it would bloat the closure list and make the boundaries
        // between the two discovery paths fuzzy.
        var dlls = CompilationPipeline.DiscoverNeoAppMcpRuntime();
        var names = dlls.Select(n => Path.GetFileName(n)!).ToList();

        names.Should().NotContain(n => n.StartsWith("Avalonia.", StringComparison.OrdinalIgnoreCase));
    }
}
