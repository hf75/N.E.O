using System.Diagnostics;
using FluentAssertions;
using Neo.McpServer.Services;
using Xunit;

namespace Neo.McpServer.Tests;

/// <summary>
/// export_app end to end: <see cref="CompilationPipeline.ExportAsync"/> compiles the user's source
/// together with the generated bootstrap program (a C# source template inside the pipeline), so the
/// only way to know that the template still compiles and boots is to export and run the result.
///
/// <para>The exported program is framework-dependent; these tests need the .NET runtime of the
/// repo's target framework and the built Avalonia plugin window (its bin folder is where the
/// pipeline takes the Avalonia reference assemblies from — see the build-order reference in the
/// csproj).</para>
/// </summary>
[Collection("ExportedProcesses")]
public class ExportIntegrationTests : IDisposable
{
    private const string TodoSource = """
        using System.Collections.ObjectModel;
        using System.ComponentModel;
        using Avalonia;
        using Avalonia.Controls;
        using Neo.App;

        public class DynamicUserControl : UserControl, INotifyPropertyChanged
        {
            public ObservableCollection<string> Items { get; } = new();

            public DynamicUserControl()
            {
                Content = new ListBox { ItemsSource = Items, Margin = new Thickness(16) };
                Items.CollectionChanged += (_, _) =>
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemCount)));
            }

            [McpObservable("Number of items in the list.", Watchable = true)]
            public int ItemCount => Items.Count;

            [McpCallable("Adds an item to the list. Returns the new item count.")]
            public int AddItem(string title)
            {
                Items.Add(title);
                return Items.Count;
            }

            public new event PropertyChangedEventHandler? PropertyChanged;
        }
        """;

    private readonly string _exportRoot =
        Path.Combine(Path.GetTempPath(), "neo_export_tests_" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        // Set NEO_KEEP_EXPORTS=1 to inspect what an export produced.
        if (Environment.GetEnvironmentVariable("NEO_KEEP_EXPORTS") == "1") return;
        try { if (Directory.Exists(_exportRoot)) Directory.Delete(_exportRoot, recursive: true); } catch { }
    }

    private async Task<string> ExportAsync(string appName, bool mcpMode)
    {
        var pipeline = new CompilationPipeline();
        var result = await pipeline.ExportAsync(
            new[] { TodoSource }, appName, _exportRoot, platform: "windows", nugetPackages: null, mcpMode: mcpMode);

        result.Success.Should().BeTrue("export should succeed, errors: " + string.Join(" | ", result.Errors));
        File.Exists(result.ExePath).Should().BeTrue();
        return result.ExePath!;
    }

    // ── no window involved: safe everywhere ──────────────────────────────────

    [Fact]
    public async Task Plain_export_compiles_and_ships_the_avalonia_runtime()
    {
        if (!OperatingSystem.IsWindows()) return;

        var exe = await ExportAsync("smoke-plain", mcpMode: false);
        var dir = Path.GetDirectoryName(exe)!;

        File.Exists(Path.Combine(dir, "Avalonia.Base.dll")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "Neo.App.Mcp.dll")).Should().BeFalse("the MCP runtime only ships with mcpMode=true");
    }

    [Fact]
    public async Task Frozen_export_dumps_its_manifest_with_mcp_help()
    {
        if (!OperatingSystem.IsWindows()) return;

        var exe = await ExportAsync("smoke-frozen", mcpMode: true);
        File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "Neo.App.Mcp.dll")).Should().BeTrue();

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--mcp-help",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        })!;
        var stderr = await process.StandardError.ReadToEndAsync();
        process.WaitForExit(30_000).Should().BeTrue("--mcp-help must exit without starting the GUI");

        process.ExitCode.Should().Be(0, "stderr: " + stderr);
        stderr.Should().Contain("Callables (1)").And.Contain("add_item");
        stderr.Should().Contain("app://ItemCount");
    }

    // ── these open a real window ─────────────────────────────────────────────

    [Fact]
    [Trait("Category", "UiSmoke")]
    public async Task Plain_export_starts_and_stays_up()
    {
        if (!OperatingSystem.IsWindows()) return;

        var exe = await ExportAsync("smoke-plain-run", mcpMode: false);
        using var process = Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = false })!;
        try
        {
            process.WaitForExit(5_000).Should().BeFalse("a healthy GUI app keeps running");
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "UiSmoke")]
    public async Task Frozen_export_serves_its_tools_over_stdio()
    {
        if (!OperatingSystem.IsWindows()) return;

        var exe = await ExportAsync("smoke-frozen-run", mcpMode: true);
        using var app = StdioJsonRpcProcess.Start(exe, "--mcp");

        await app.InitializeAsync("neo-export-tests");

        (await app.ListToolNamesAsync()).Should().Contain(new[] { "add_item", "raise_event", "simulate_input" });

        var call = await app.CallToolAsync("add_item", new { title = "from the export test" });
        call.Should().Be("1", "AddItem returns the new count");
    }
}

[CollectionDefinition("ExportedProcesses", DisableParallelization = true)]
public class ExportedProcessesCollection { }
