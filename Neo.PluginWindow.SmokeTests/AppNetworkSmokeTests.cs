using System.Runtime.InteropServices;
using FluentAssertions;
using Neo.Agents;
using Neo.App;
using Neo.HotReload;
using Neo.Shared;
using Xunit;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// Apps that use each other: two real plugin windows, each running an app. One offers methods and
/// values (<c>[McpCallable]</c>, <c>[McpObservable]</c>), the other calls them through
/// <see cref="Apps"/> from its own code, across processes, with every call landing on the right
/// UI thread. This test process is a third participant that watches the network the way the hosts
/// do when they tell the AI which apps are running.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class AppNetworkSmokeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "neo_smoke_appnet_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _registry;

    public AppNetworkSmokeTests()
    {
        // The children and this process share a registry folder of their own, not the user's.
        _registry = Path.Combine(_directory, "registry");
        Environment.SetEnvironmentVariable("NEO_APP_NETWORK_DIR", _registry);
    }

    public static TheoryData<string, string> DesktopHeads => new()
    {
        { "Neo.PluginWindowAvalonia", "--standalone" },
        { "Neo.PluginWindowWPF", "" },
    };

    [Theory]
    [MemberData(nameof(DesktopHeads))]
    public async Task One_app_uses_another_across_processes(string head, string launchArgs)
    {
        bool wpf = head.Contains("WPF", StringComparison.Ordinal);
        var env = new Dictionary<string, string>
        {
            [HotReloadRuntime.EnvironmentVariable] = HotReloadRuntime.EnvironmentValue,
            ["NEO_APP_NETWORK_DIR"] = _registry,
        };

        // A: the app that offers something.
        var salesDll = Path.Combine(_directory, "sales", "DynamicUserControl.dll");
        await CompileAsync(wpf ? WpfSales : AvaloniaSales, salesDll, wpf);
        await using var sales = await ChildSession.StartAsync(TestEnvironment.HeadExe(head), launchArgs + " --app-name \"Ignored: the code names itself\"", env);
        using var hotReload = new HotReloadCoordinator();
        await sales.LoadAsync(salesDll);
        (await sales.WaitForLoadsAsync(1)).Should().BeTrue(string.Join(" | ", sales.Errors));
        (await hotReload.StartAsync(salesDll, sales.HotReloadCapabilities)).Should().BeNull();

        // This process sees it, as the hosts do when they build the prompt.
        var offered = await WaitForAsync(() => Apps.Find("Sales"), "the Sales app on the network");
        offered.Description.Should().Be("Records sales.");
        offered.ProcessId.Should().Be(sales.ProcessId);
        offered.Methods.Select(m => m.Name).Should().BeEquivalentTo("AddSale", "Total");
        offered.Values.Select(v => v.Name).Should().BeEquivalentTo("Count");
        offered.Describe().Should().Contain("method AddSale(string product, decimal amount) → void — Adds a sale.");

        // A call from outside runs on A's UI thread: the UI shows it.
        await Apps.CallAsync("Sales", "AddSale", "Coffee", 3.5m);
        (await Apps.ReadAsync<int>("Sales", "Count")).Should().Be(1);
        Property(await sales.InspectVisualTreeAsync(), "count", "Text").Should().Be("1 sales");

        // B: the app that uses A — from its own code, in its own process.
        var dashboardDll = Path.Combine(_directory, "dashboard", "DynamicUserControl.dll");
        await CompileAsync(wpf ? WpfDashboard : AvaloniaDashboard, dashboardDll, wpf);
        await using var dashboard = await ChildSession.StartAsync(TestEnvironment.HeadExe(head), launchArgs, env);
        await dashboard.LoadAsync(dashboardDll);
        (await dashboard.WaitForLoadsAsync(1)).Should().BeTrue(string.Join(" | ", dashboard.Errors));
        await WaitForAsync(() => Apps.Find("Dashboard"), "the Dashboard app on the network — child logs: " + string.Join(" | ", dashboard.Logs));

        Apps.Running().Select(a => a.Name).Should().BeEquivalentTo("Sales", "Dashboard");

        var summary = await Apps.CallAsync<string>("Dashboard", "Refresh");
        summary.Should().Be("Sales total: 3.5 (1 sales)", "Dashboard asked Sales over the network");
        Property(await dashboard.InspectVisualTreeAsync(), "summary", "Text").Should().Be("Sales total: 3.5 (1 sales)",
            "the answer went into Dashboard's UI on its UI thread");

        // A live update of A changes its offer; the network knows right away.
        var v2 = (wpf ? WpfSales : AvaloniaSales).Replace("// more", """
            [McpCallable("Forgets all sales.")]
            public void Clear() { _rows.Clear(); BuildUi(); }
            """);
        await CompileAsync(v2, salesDll, wpf);
        var attempt = await hotReload.TryAsync(salesDll, sales.HotReloadAsync);
        attempt.Applied.Should().BeTrue(attempt.Message);
        (await WaitForAsync(() => Apps.Find("Sales") is { } a && a.Methods.Any(m => m.Name == "Clear") ? a : null, "the new method on the network"))
            .Methods.Select(m => m.Name).Should().BeEquivalentTo("AddSale", "Total", "Clear");
        await Apps.CallAsync("Sales", "Clear");
        (await Apps.ReadAsync<int>("Sales", "Count")).Should().Be(0);

        // Errors travel back as a reason, not as a crash.
        var wrong = await Assert.ThrowsAsync<AppCallException>(() => Apps.CallAsync("Sales", "AddSale", "x", "not a number"));
        wrong.Message.Should().NotBeNullOrEmpty();
        sales.ChildIsAlive.Should().BeTrue();

        // Unloading takes the app off the network.
        await sales.UnloadAsync();
        await WaitForAsync(() => Apps.IsRunning("Sales") ? null : "gone", "Sales off the network after unload");
        var refused = await Assert.ThrowsAsync<AppCallException>(() => Apps.CallAsync<string>("Dashboard", "Refresh"));
        refused.Message.Should().Contain("No running N.E.O. app is called 'Sales'", "Dashboard reports why it could not refresh");

        sales.Errors.Should().BeEmpty();
        dashboard.Errors.Should().BeEmpty();
    }

    // ── The apps ─────────────────────────────────────────────────────────────

    private const string AvaloniaSales = """
        using System.Collections.Generic;
        using System.Linq;
        using Avalonia;
        using Avalonia.Controls;
        using Neo.App;

        [AppInfo("Sales", "Records sales.")]
        public class DynamicUserControl : UserControl
        {
            private readonly List<(string Product, decimal Amount)> _rows = new();
            private TextBlock _count = new();

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                _count = new TextBlock { Name = "count", Text = _rows.Count + " sales" };
                Content = new StackPanel { Margin = new Thickness(16), Children = { _count } };
            }

            [McpCallable("Adds a sale.")]
            public void AddSale(string product, decimal amount) { _rows.Add((product, amount)); _count.Text = _rows.Count + " sales"; }

            [McpCallable("Total of all sales.")]
            public decimal Total() => _rows.Sum(r => r.Amount);

            [McpObservable("Number of sales.")]
            public int Count => _rows.Count;

            // more
        }
        """;

    private const string WpfSales = """
        using System.Collections.Generic;
        using System.Linq;
        using System.Windows;
        using System.Windows.Controls;
        using Neo.App;

        [AppInfo("Sales", "Records sales.")]
        public class DynamicUserControl : UserControl
        {
            private readonly List<(string Product, decimal Amount)> _rows = new();
            private TextBlock _count = new();

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                _count = new TextBlock { Name = "count", Text = _rows.Count + " sales" };
                var panel = new StackPanel { Margin = new Thickness(16) };
                panel.Children.Add(_count);
                Content = panel;
            }

            [McpCallable("Adds a sale.")]
            public void AddSale(string product, decimal amount) { _rows.Add((product, amount)); _count.Text = _rows.Count + " sales"; }

            [McpCallable("Total of all sales.")]
            public decimal Total() => _rows.Sum(r => r.Amount);

            [McpObservable("Number of sales.")]
            public int Count => _rows.Count;

            // more
        }
        """;

    private const string AvaloniaDashboard = """
        using System.Threading.Tasks;
        using Avalonia;
        using Avalonia.Controls;
        using Neo.App;

        [AppInfo("Dashboard", "Shows what the other apps know.")]
        public class DynamicUserControl : UserControl
        {
            private TextBlock _summary = new();

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                _summary = new TextBlock { Name = "summary", Text = "—" };
                Content = new StackPanel { Margin = new Thickness(16), Children = { _summary } };
            }

            [McpCallable("Asks the Sales app for its total and shows it.")]
            public async Task<string> Refresh()
            {
                var total = await Apps.CallAsync<decimal>("Sales", "Total");
                var count = await Apps.ReadAsync<int>("Sales", "Count");
                _summary.Text = $"Sales total: {total.ToString(System.Globalization.CultureInfo.InvariantCulture)} ({count} sales)";
                return _summary.Text;
            }
        }
        """;

    private const string WpfDashboard = """
        using System.Threading.Tasks;
        using System.Windows;
        using System.Windows.Controls;
        using Neo.App;

        [AppInfo("Dashboard", "Shows what the other apps know.")]
        public class DynamicUserControl : UserControl
        {
            private TextBlock _summary = new();

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                _summary = new TextBlock { Name = "summary", Text = "—" };
                var panel = new StackPanel { Margin = new Thickness(16) };
                panel.Children.Add(_summary);
                Content = panel;
            }

            [McpCallable("Asks the Sales app for its total and shows it.")]
            public async Task<string> Refresh()
            {
                var total = await Apps.CallAsync<decimal>("Sales", "Total");
                var count = await Apps.ReadAsync<int>("Sales", "Count");
                _summary.Text = $"Sales total: {total.ToString(System.Globalization.CultureInfo.InvariantCulture)} ({count} sales)";
                return _summary.Text;
            }
        }
        """;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task CompileAsync(string source, string dllPath, bool wpf)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);
        var coreDirectories = new List<string> { RuntimeEnvironment.GetRuntimeDirectory() };
        var additional = new List<string> { Path.Combine(TestEnvironment.HeadBinDir("Neo.PluginWindowAvalonia"), "Neo.App.Api.dll") };
        if (wpf)
            coreDirectories.Add(Path.GetDirectoryName(typeof(System.Windows.Window).Assembly.Location)!);
        else
            additional.AddRange(Directory.GetFiles(TestEnvironment.HeadBinDir("Neo.PluginWindowAvalonia"), "Avalonia*.dll"));

        var agent = new CSharpDllCompileAgent();
        agent.SetOption("CoreDllPath", coreDirectories);
        agent.SetInput("Code", new List<string> { source });
        agent.SetInput("AssemblyName", "DynamicUserControl");
        agent.SetInput("DllOutputPath", dllPath);
        agent.SetInput("NuGetDlls", new List<string>());
        agent.SetInput("AdditionalDlls", additional);
        try { await agent.ExecuteAsync(); }
        catch (Exception ex) { throw new InvalidOperationException("the app must compile: " + ex.Message, ex); }
        File.Exists(dllPath).Should().BeTrue("the app must compile");
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, string what) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } found) return found;
            await Task.Delay(100);
        }
        throw new TimeoutException("not found within 10 s: " + what);
    }

    private static string? Property(string? treeJson, string controlName, string property)
    {
        if (treeJson == null) return null;
        using var document = System.Text.Json.JsonDocument.Parse(treeJson);
        var pending = new Stack<System.Text.Json.JsonElement>();
        pending.Push(document.RootElement);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node.TryGetProperty("name", out var name) && name.GetString() == controlName &&
                node.TryGetProperty("properties", out var properties) && properties.TryGetProperty(property, out var value))
                return value.GetString();
            if (node.TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray()) pending.Push(child);
        }
        return null;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NEO_APP_NETWORK_DIR", null);
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }
}
