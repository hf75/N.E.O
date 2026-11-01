using FluentAssertions;
using Neo.App;
using Xunit;

namespace Neo.App.Core.Tests;

/// <summary>
/// The hosts register their own Avalonia as references. Asking NuGet for it again costs half a
/// minute and 2.3 GB, for files the compiler then does not use.
/// </summary>
public class NuGetPackageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "neo_nuget_service_" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task What_the_host_ships_is_remembered_but_not_downloaded()
    {
        var nuget = Directory.CreateDirectory(Path.Combine(_root, "nuget")).FullName;
        var hostDll = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "host")).FullName, "Avalonia.dll");
        File.Copy(typeof(NuGetPackageServiceTests).Assembly.Location, hostDll);

        var result = await new NuGetPackageService(nuget, null, null).LoadPackagesAsync(
            new Dictionary<string, string> { ["Avalonia"] = "11.3.12", ["Avalonia.BuildServices"] = "11.3.2" }, [hostDll]);

        Directory.GetFileSystemEntries(nuget).Should().BeEmpty("nothing was downloaded");
        result.DllPaths.Should().Equal(hostDll);
        result.PackageVersions.Keys.Should().BeEquivalentTo(["Avalonia", "Avalonia.BuildServices"],
            "the export for another platform restores them from this list");
    }

    [Fact]
    public void An_export_for_another_platform_brings_Avalonia_even_if_nobody_named_it()
    {
        // On this machine the host brings it, so a model may well send an empty list.
        var packages = AppExportService.PackagesForOtherPlatform(new Dictionary<string, string>(), useAvalonia: true);

        packages.Keys.Should().BeEquivalentTo(["Avalonia", "Avalonia.Desktop", "Avalonia.Themes.Fluent"]);
        packages.Values.Should().AllBe(Neo.Shared.NeoBuildInfo.AvaloniaVersion);
    }

    [Fact]
    public void An_export_for_another_platform_keeps_what_was_named()
    {
        var packages = AppExportService.PackagesForOtherPlatform(
            new Dictionary<string, string> { ["avalonia"] = "12.0.0", ["MathNet.Numerics"] = "5.0.0" }, useAvalonia: true);

        packages["Avalonia"].Should().Be("12.0.0");
        packages["MathNet.Numerics"].Should().Be("5.0.0");
        packages.Should().HaveCount(3 + 1);
    }

    [Fact]
    public void A_WPF_export_gets_no_Avalonia()
        => AppExportService.PackagesForOtherPlatform(new Dictionary<string, string> { ["NAudio"] = "2.2.1" }, useAvalonia: false)
            .Keys.Should().BeEquivalentTo(["NAudio"]);

    [Theory]
    [InlineData("low", 1, "low")]
    [InlineData("low", 2, "medium")]
    [InlineData("low", 3, "high")]
    [InlineData("low", 5, "high")]
    [InlineData("medium", 1, "medium")]
    [InlineData("medium", 2, "high")]
    [InlineData("high", 3, "high")]
    [InlineData("xhigh", 2, "xhigh")]
    [InlineData("max", 2, "max")]
    [InlineData("", 3, "")]
    public void Another_try_gets_one_level_more_care(string configured, int attempt, string expected)
        => AppController.EffortForAttempt(configured, attempt).Should().Be(expected);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
