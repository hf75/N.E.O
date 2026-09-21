using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--mcp",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        })!;
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        process.BeginErrorReadLine();

        try
        {
            await SendAsync(process, new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
                    clientInfo = new { name = "neo-export-tests", version = "1.0" },
                },
            });
            (await ReadResponseAsync(process, id: 1, stderr)).TryGetProperty("result", out _).Should().BeTrue();

            await SendAsync(process, new { jsonrpc = "2.0", method = "notifications/initialized" });

            await SendAsync(process, new { jsonrpc = "2.0", id = 2, method = "tools/list" });
            var tools = (await ReadResponseAsync(process, id: 2, stderr)).GetProperty("result").GetProperty("tools")
                .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
            tools.Should().Contain(new[] { "add_item", "raise_event", "simulate_input" });

            await SendAsync(process, new
            {
                jsonrpc = "2.0", id = 3, method = "tools/call",
                @params = new { name = "add_item", arguments = new { title = "from the export test" } },
            });
            var call = (await ReadResponseAsync(process, id: 3, stderr)).GetProperty("result");
            call.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("1", "AddItem returns the new count");
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static async Task SendAsync(Process process, object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync();
    }

    /// <summary>Reads newline-delimited JSON-RPC messages until the response with the given id arrives.</summary>
    private static async Task<JsonElement> ReadResponseAsync(Process process, int id, StringBuilder stderr)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true)
        {
            string? line;
            try { line = await process.StandardOutput.ReadLineAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                string err; lock (stderr) err = stderr.ToString();
                throw new TimeoutException($"No JSON-RPC response with id {id}. Child stderr:\n{err}");
            }

            if (line == null)
            {
                string err; lock (stderr) err = stderr.ToString();
                throw new InvalidOperationException($"Exported app closed stdout before answering id {id}. Child stderr:\n{err}");
            }
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("id", out var idProp) &&
                idProp.ValueKind == JsonValueKind.Number && idProp.GetInt32() == id)
                return doc.RootElement.Clone();
        }
    }
}

[CollectionDefinition("ExportedProcesses", DisableParallelization = true)]
public class ExportedProcessesCollection { }
