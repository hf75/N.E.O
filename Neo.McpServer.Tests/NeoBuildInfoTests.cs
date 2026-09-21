using FluentAssertions;
using Neo.AssemblyForge;
using Neo.Shared;
using Xunit;

namespace Neo.McpServer.Tests;

/// <summary>What the build stamps into the assemblies, and how the hosts find each other at dev time.</summary>
public class NeoBuildInfoTests
{
    [Fact]
    public void Avalonia_version_is_stamped_and_the_same_for_everyone_who_reads_it()
    {
        NeoBuildInfo.AvaloniaVersion.Should().MatchRegex(@"^\d+\.\d+\.\d+$");

        // Neo.AssemblyForge.Core cannot reference Neo.Shared (it also runs in the browser) and reads
        // the metadata from its own assembly; both must come from the same Directory.Build.props.
        AvaloniaGuidance.Version.Should().Be(NeoBuildInfo.AvaloniaVersion);
    }

    [Theory]
    [InlineData(@"C:\repo\Neo.McpServer\bin\Release\net10.0\", "Release")]
    [InlineData(@"C:\repo\Neo.McpServer\bin\Release\net10.0", "Release")]
    [InlineData(@"C:\repo\Neo.McpServer\bin\Debug\net10.0\", "Debug")]
    [InlineData(@"C:\tools\neo-mcp\", "Debug")] // deployed: no configuration folder, order does not matter
    public void Sibling_outputs_are_looked_up_in_the_own_build_configuration_first(string baseDirectory, string expectedFirst)
    {
        // A Release server that starts a stale Debug child (or compiles against its assemblies)
        // is the kind of mismatch nobody finds quickly.
        var normalized = baseDirectory.Replace('\\', Path.DirectorySeparatorChar);

        NeoBuildInfo.OrderConfigurations(normalized)[0].Should().Be(expectedFirst);
        NeoBuildInfo.OrderConfigurations(normalized).Should().BeEquivalentTo(["Debug", "Release"]);
    }

    [Fact]
    public void This_test_run_prefers_its_own_configuration()
    {
        // <repo>/Neo.McpServer.Tests/bin/<Configuration>/<tfm>/
        var own = Path.GetFileName(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)));

        NeoBuildInfo.DevBuildConfigurations[0].Should().Be(own == "Release" ? "Release" : "Debug");
    }
}
