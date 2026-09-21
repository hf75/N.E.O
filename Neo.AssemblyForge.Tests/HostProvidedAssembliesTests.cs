using FluentAssertions;
using Neo.Agents;
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
    public void A_package_the_host_provides_is_moved_to_the_hosts_version()
    {
        // A real assembly with a known version stands in for the host's Avalonia.dll.
        var thisAssembly = typeof(HostProvidedAssembliesTests).Assembly;
        var hostCopy = Path.Combine(_hostBin, "Avalonia.dll");
        File.Copy(thisAssembly.Location, hostCopy);
        var v = thisAssembly.GetName().Version!;
        var hostVersion = $"{v.Major}.{v.Minor}.{v.Build}";

        var index = HostProvidedAssemblies.Index([hostCopy]);
        var packages = HostProvidedAssemblies.WithHostVersions(
            new Dictionary<string, string> { ["Avalonia"] = "11.3.12", ["MathNet.Numerics"] = "5.0.0" }, index);

        packages["Avalonia"].Should().Be(hostVersion);
        packages["MathNet.Numerics"].Should().Be("5.0.0", "the host does not provide it");
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
    public void Nothing_provided_means_nothing_changes()
    {
        var index = HostProvidedAssemblies.Index(null);
        var request = new Dictionary<string, string> { ["Avalonia"] = "11.3.12" };

        HostProvidedAssemblies.WithHostVersions(request, index).Should().BeEquivalentTo(request);
        HostProvidedAssemblies.WithoutProvidedPackages(request, index).Should().BeEquivalentTo(request);
    }
}
