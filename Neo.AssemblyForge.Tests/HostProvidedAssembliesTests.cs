using FluentAssertions;
using Neo.Agents;
using Neo.AssemblyForge.Services;
using Xunit;

namespace Neo.AssemblyForge.Tests;

/// <summary>
/// Generated code runs on the Avalonia the host ships. A project saved before the Avalonia 12
/// move still asks for "Avalonia|11.3.12"; these rules keep that second version away from the
/// compiler.
/// </summary>
public class HostProvidedAssembliesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "neo_hostprovided_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _hostBin;
    private readonly string _nugetDir;

    public HostProvidedAssembliesTests()
    {
        _hostBin = Directory.CreateDirectory(Path.Combine(_root, "hostbin")).FullName;
        _nugetDir = Directory.CreateDirectory(Path.Combine(_root, "nuget")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void A_package_the_host_provides_is_remembered_at_the_hosts_version()
    {
        // A real assembly with a known version stands in for the host's Avalonia.dll.
        var thisAssembly = typeof(HostProvidedAssembliesTests).Assembly;
        var hostCopy = Path.Combine(_hostBin, "Avalonia.dll");
        File.Copy(thisAssembly.Location, hostCopy);
        var v = thisAssembly.GetName().Version!;
        var hostVersion = $"{v.Major}.{v.Minor}.{v.Build}";

        var index = HostProvidedAssemblies.Index([hostCopy]);
        var packages = HostProvidedAssemblies.ProvidedPackages(
            new Dictionary<string, string> { ["Avalonia"] = "11.3.12", ["MathNet.Numerics"] = "5.0.0" }, index);

        packages.Should().ContainSingle("the host does not provide MathNet.Numerics");
        packages["Avalonia"].Should().Be(hostVersion);
    }

    [Fact]
    public void Only_Avalonias_own_packages_move_to_the_version_of_the_hosts_assembly()
    {
        // SkiaSharp 3.119.4 has the assembly version 3.119.0: outside Avalonia the one says nothing about the other.
        var hostCopy = Path.Combine(_hostBin, "SkiaSharp.dll");
        File.Copy(typeof(HostProvidedAssembliesTests).Assembly.Location, hostCopy);

        HostProvidedAssemblies.ProvidedPackages(new Dictionary<string, string> { ["SkiaSharp"] = "3.119.4" }, HostProvidedAssemblies.Index([hostCopy]))
            ["SkiaSharp"].Should().Be("3.119.4");
    }

    [Theory]
    [InlineData("SkiaSharp.NativeAssets.Win32", true)]
    [InlineData("skiasharp.nativeassets.webassembly", true)]
    [InlineData("HarfBuzzSharp.NativeAssets.Linux", true)]
    [InlineData("Avalonia.Angle.Windows.Natives", true)]
    [InlineData("Avalonia.BuildServices", true)]
    [InlineData("Avalonia.Win32", true)]
    [InlineData("Avalonia.Controls.DataGrid", false)]
    [InlineData("LibSomething.NativeAssets.Win32", false)]
    public void Packages_without_an_assembly_count_as_provided_with_the_assembly_they_serve(string packageId, bool provided)
    {
        var index = HostProvidedAssemblies.Index(new[] { "Avalonia.dll", "Avalonia.Win32.dll", "SkiaSharp.dll", "HarfBuzzSharp.dll" }.Select(n => Path.Combine(_hostBin, n)));

        HostProvidedAssemblies.Provides(index, packageId).Should().Be(provided);
    }

    [Fact]
    public void Without_the_framework_in_the_host_nothing_counts_as_provided()
    {
        // The WPF host writing a WPF app: a SkiaSharp the app asks for has to come from NuGet.
        var index = HostProvidedAssemblies.Index([]);

        foreach (var id in new[] { "SkiaSharp", "SkiaSharp.NativeAssets.Win32", "Avalonia", "Avalonia.BuildServices" })
            HostProvidedAssemblies.Provides(index, id).Should().BeFalse(id);
    }

    [Fact]
    public void A_package_the_host_provides_can_be_left_out_entirely()
    {
        var index = HostProvidedAssemblies.Index(
        [
            Path.Combine(_hostBin, "Avalonia.dll"),
            Path.Combine(_hostBin, "Avalonia.Controls.WebView.dll"),
        ]);

        var packages = HostProvidedAssemblies.WithoutProvidedPackages(new Dictionary<string, string>
        {
            ["Avalonia"] = "11.3.12",
            ["avalonia.controls.webview"] = "default",
            ["Avalonia.Controls.DataGrid"] = "12.1.2",
        }, index);

        packages.Keys.Should().BeEquivalentTo(["Avalonia.Controls.DataGrid"],
            "an Avalonia add-on the host does not ship still has to come from NuGet");
    }

    [Fact]
    public void Dependencies_that_shadow_a_host_assembly_are_dropped()
    {
        var index = HostProvidedAssemblies.Index([Path.Combine(_hostBin, "Avalonia.Base.dll")]);
        var resolved = new List<string>
        {
            Path.Combine(_nugetDir, "Avalonia.Base.dll"),              // dragged in by the add-on
            Path.Combine(_nugetDir, "Avalonia.Controls.DataGrid.dll"),
        };

        HostProvidedAssemblies.RemoveShadowing(resolved, index).Should().Be(1);
        resolved.Should().ContainSingle().Which.Should().EndWith("Avalonia.Controls.DataGrid.dll");
    }

    [Fact]
    public void The_hosts_own_path_is_never_dropped()
    {
        var hostCopy = Path.Combine(_hostBin, "Avalonia.Base.dll");
        var index = HostProvidedAssemblies.Index([hostCopy]);
        var resolved = new List<string> { hostCopy };

        HostProvidedAssemblies.RemoveShadowing(resolved, index).Should().Be(0);
        resolved.Should().ContainSingle();
    }

    [Fact]
    public void Only_DLLs_outside_the_NuGet_folder_count_as_host_provided()
    {
        // An earlier download of package X must not stop a later request for a newer X.
        var index = HostProvidedAssemblies.IndexOutside(
        [
            Path.Combine(_hostBin, "Avalonia.dll"),
            Path.Combine(_nugetDir, "MathNet.Numerics.dll"),
            Path.Combine(_nugetDir, "de", "MathNet.Numerics.resources.dll"),
        ], _nugetDir);

        index.Keys.Should().BeEquivalentTo(["Avalonia.dll"]);
        HostProvidedAssemblies.Provides(index, "MathNet.Numerics").Should().BeFalse();
        HostProvidedAssemblies.Provides(index, "avalonia").Should().BeTrue("package ids are case-insensitive");
    }

    [Fact]
    public async Task What_the_host_ships_is_remembered_but_not_downloaded()
    {
        var hostCopies = HostCopies("Avalonia.dll", "SkiaSharp.dll");

        // A project saved earlier names the whole closure, native libraries for every platform included.
        var result = await new NuGetPackageService(_nugetDir, TargetFramework, []).LoadPackagesAsync(new Dictionary<string, string>
        {
            ["Avalonia"] = "11.3.12",
            ["SkiaSharp"] = "3.119.4",
            ["SkiaSharp.NativeAssets.Win32"] = "3.119.4",
        }, hostCopies, CancellationToken.None);

        Directory.GetFileSystemEntries(_nugetDir).Should().BeEmpty("nothing was downloaded");
        result.DllPaths.Should().BeEquivalentTo(hostCopies);
        result.PackageVersions.Keys.Should().BeEquivalentTo(["Avalonia", "SkiaSharp", "SkiaSharp.NativeAssets.Win32"],
            "the export for another platform restores them from this list");
    }

    [Fact]
    public async Task An_add_on_brings_itself_and_not_what_the_host_ships()
    {
        // Real NuGet. The add-on depends on Avalonia, and everything behind that is 2.3 GB.
        var hostCopies = HostCopies("Avalonia.dll", "Avalonia.Base.dll");

        var result = await new NuGetPackageService(_nugetDir, TargetFramework, []).LoadPackagesAsync(
            new Dictionary<string, string> { ["Avalonia.Controls.DataGrid"] = AvaloniaGuidance.Version }, hostCopies, CancellationToken.None);

        result.DllPaths.Should().Contain(p => Path.GetFileName(p) == "Avalonia.Controls.DataGrid.dll");
        result.DllPaths.Should().Contain(hostCopies);
        Directory.GetDirectories(Path.Combine(_nugetDir, ".nuget_cache")).Select(Path.GetFileName)
            .Should().BeEquivalentTo(["avalonia.controls.datagrid"]);
    }

    private static string TargetFramework => $"net{Environment.Version.Major}.0";

    // A real assembly stands in for each of the host's files.
    private List<string> HostCopies(params string[] names)
    {
        var copies = names.Select(n => Path.Combine(_hostBin, n)).ToList();
        foreach (var copy in copies)
            File.Copy(typeof(HostProvidedAssembliesTests).Assembly.Location, copy);
        return copies;
    }

    [Fact]
    public void Nothing_provided_means_nothing_changes()
    {
        var index = HostProvidedAssemblies.Index(null);
        var request = new Dictionary<string, string> { ["Avalonia"] = "11.3.12" };

        HostProvidedAssemblies.ProvidedPackages(request, index).Should().BeEmpty();
        HostProvidedAssemblies.WithoutProvidedPackages(request, index).Should().BeEquivalentTo(request);
    }
}
