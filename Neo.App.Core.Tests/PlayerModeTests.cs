using System.Runtime.InteropServices;
using FluentAssertions;
using Neo.App;
using Newtonsoft.Json;
using Xunit;

namespace Neo.App.Core.Tests;

/// <summary>
/// A living app is the host itself, exported next to a project and started in player mode.
/// These cover the two ends: what the export writes, and how a host recognises it at start-up.
/// </summary>
public class PlayerModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeoPlayerTest_" + Guid.NewGuid().ToString("N")[..8]);

    public PlayerModeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void No_manifest_and_no_play_argument_means_the_normal_mode()
    {
        PlayerProject.Detect([], _dir).Should().BeNull();
        PlayerProject.Detect(["--other", "x"], _dir).Should().BeNull();
    }

    [Fact]
    public void Play_argument_plays_that_project_and_finds_an_original_next_to_it()
    {
        var project = Path.Combine(_dir, "Notes.resx");
        File.WriteAllText(project, "{\"Code\":\"using System.Windows;\nclass DynamicUserControl {}\"}");

        var player = PlayerProject.Detect(["--play", project], _dir)!;
        player.ProjectFile.Should().Be(project);
        player.Title.Should().Be("Notes");
        player.OriginalFile.Should().BeNull("nothing to reset to");
        player.UsesAvalonia.Should().BeFalse("the code says System.Windows — whatever the shared settings say");

        File.WriteAllText(Path.Combine(_dir, "Notes.original.resx"), "{}");
        PlayerProject.Detect(["--play", project], _dir)!.OriginalFile.Should().EndWith("Notes.original.resx");
    }

    [Fact]
    public void The_manifest_next_to_the_executable_turns_the_host_into_a_player()
    {
        File.WriteAllText(Path.Combine(_dir, "Notes.resx"), "{}");
        File.WriteAllText(Path.Combine(_dir, "Notes.original.resx"), "{}");
        File.WriteAllText(Path.Combine(_dir, PlayerManifest.FileName), JsonConvert.SerializeObject(new PlayerManifest
        {
            Project = "Notes.resx", Original = "Notes.original.resx", Title = "My Notes", Icon = "missing.ico",
        }));

        var player = PlayerProject.Detect([], _dir)!;
        player.Title.Should().Be("My Notes");
        player.ProjectFile.Should().Be(Path.Combine(_dir, "Notes.resx"));
        player.OriginalFile.Should().Be(Path.Combine(_dir, "Notes.original.resx"));
        player.IconFile.Should().BeNull("the icon file is not there");
    }

    [Fact]
    public void Reset_puts_the_original_project_back()
    {
        var project = Path.Combine(_dir, "Notes.resx");
        var original = Path.Combine(_dir, "Notes.original.resx");
        File.WriteAllText(project, "changed");
        File.WriteAllText(original, "pristine");

        new PlayerProject(project, original, "Notes", null).ResetToOriginal().Should().BeTrue();
        File.ReadAllText(project).Should().Be("pristine");
        new PlayerProject(project, null, "Notes", null).ResetToOriginal().Should().BeFalse();
    }

    [Fact]
    public void The_export_is_the_host_trimmed_to_this_platform_renamed_after_the_app_with_the_project_and_its_original()
    {
        // A pretend host folder: executable, libraries, debug symbols, native libraries of every platform.
        var host = Path.Combine(_dir, "host");
        Directory.CreateDirectory(Path.Combine(host, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"));
        Directory.CreateDirectory(Path.Combine(host, "runtimes", "linux-musl-arm", "native"));
        Directory.CreateDirectory(Path.Combine(host, "AppHostTemplates"));
        var exeName = OperatingSystem.IsWindows() ? "Neo.App.Avalonia.exe" : "Neo.App.Avalonia";
        File.WriteAllText(Path.Combine(host, exeName), "exe");
        File.WriteAllText(Path.Combine(host, "Neo.App.Avalonia.dll"), "dll");
        File.WriteAllText(Path.Combine(host, "Neo.App.Avalonia.pdb"), "pdb");
        File.WriteAllText(Path.Combine(host, "Some.xml"), "docs");
        File.WriteAllText(Path.Combine(host, PlayerManifest.FileName), "a manifest of another export must not come along");
        File.WriteAllText(Path.Combine(host, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", "libSkiaSharp.dll"), "native");
        File.WriteAllText(Path.Combine(host, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", "libSkiaSharp.pdb"), "80 MB of symbols");
        File.WriteAllText(Path.Combine(host, "runtimes", "linux-musl-arm", "native", "libSkiaSharp.so"), "native");
        File.WriteAllText(Path.Combine(host, "AppHostTemplates", "apphost-template-windows.exe"), "template");

        var target = Path.Combine(_dir, "out", "Notes");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "Notes.resx"), "{\"Code\":\"v1\"}");
        var icon = Path.Combine(_dir, "notes.ico");
        File.WriteAllText(icon, "ico");

        var exe = LivingAppExporter.Export(target, "Notes", icon, new FakePlatform(), "avalonia", host, Path.Combine(host, exeName));

        exe.Should().Be(Path.Combine(target, OperatingSystem.IsWindows() ? "Notes.exe" : "Notes"));
        File.Exists(exe).Should().BeTrue();
        File.Exists(Path.Combine(target, exeName)).Should().BeFalse("the host executable carries the app's name now");
        File.Exists(Path.Combine(target, "Neo.App.Avalonia.dll")).Should().BeTrue("the apphost finds its dll by name");
        Directory.GetFiles(target, "*.pdb", SearchOption.AllDirectories).Should().BeEmpty();
        File.Exists(Path.Combine(target, "Some.xml")).Should().BeFalse();
        Directory.Exists(Path.Combine(target, "runtimes", RuntimeInformation.RuntimeIdentifier)).Should().BeTrue();
        Directory.Exists(Path.Combine(target, "runtimes", "linux-musl-arm")).Should().BeFalse();
        File.Exists(Path.Combine(target, "AppHostTemplates", "apphost-template-windows.exe")).Should().BeTrue();
        File.ReadAllText(Path.Combine(target, "Notes.original.resx")).Should().Be("{\"Code\":\"v1\"}");
        File.Exists(Path.Combine(target, "Notes.ico")).Should().BeTrue();

        var player = PlayerProject.Detect([], target)!;
        player.Title.Should().Be("Notes");
        player.ProjectFile.Should().Be(Path.Combine(target, "Notes.resx"));
        player.OriginalFile.Should().Be(Path.Combine(target, "Notes.original.resx"));
        player.IconFile.Should().Be(Path.Combine(target, "Notes.ico"));
        player.UsesAvalonia.Should().BeTrue();
    }

    private sealed class FakePlatform : IPlatformServices
    {
        public string GetStartupPath() => "";
        public bool InjectIcon(string exePath, string iconPath) => true;
        public void InstallApplicationPerUser(string basePath, string appName, bool installStartMenu, bool installDesktop, string? displayVersion, string? publisher) { }
        public void UninstallShortcuts(string appName, bool removeFromStartMenu, bool removeFromDesktop, bool throwOnError) { }
    }
}
