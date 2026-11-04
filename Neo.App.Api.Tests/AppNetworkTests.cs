using FluentAssertions;
using Neo.App;
using Xunit;

namespace Neo.App.Api.Tests;

/// <summary>
/// Two apps on the app network, both in this process with their own servers: one offers methods
/// and values, the other uses them through <see cref="Apps"/> exactly as generated code does —
/// over the real named pipes and the registry folder (redirected to a temp folder for the test).
/// </summary>
[Collection("AppNetwork")]
public class AppNetworkTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "neo_appnet_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<AppNetwork.AppServer> _servers = new();

    public AppNetworkTests()
    {
        Environment.SetEnvironmentVariable(AppRegistry.FolderEnvironmentVariable, _folder);
    }

    // The offering app. Methods run "on the UI thread" — here simply inline.
    [AppInfo("Sales", "Records sales and knows the total.")]
    public class SalesApp
    {
        public readonly List<(string Product, decimal Amount)> Rows = new();
        public int CallsOnUiThread;

        [McpCallable("Adds a sale.")]
        public void AddSale(string product, decimal amount) => Rows.Add((product, amount));

        [McpCallable("Total of all sales.")]
        public decimal Total() => Rows.Sum(r => r.Amount);

        [McpCallable("The products sold, most recent first.")]
        public async Task<List<string>> ProductsAsync(int max = 10)
        {
            await Task.Delay(10);
            return Rows.Select(r => r.Product).Reverse().Take(max).ToList();
        }

        [McpCallable("Fails on purpose.")]
        public void Explode() => throw new InvalidOperationException("no way");

        [McpCallable("Calls itself through the network without end.")]
        public Task<int> Recurse() => Apps.CallAsync<int>("Sales", "Recurse");

        [McpCallable("Takes longer than allowed.", TimeoutSeconds = 1)]
        public Task Slow() => Task.Delay(TimeSpan.FromSeconds(5));

        [McpObservable("Number of sales.")]
        public int Count => Rows.Count;

        [McpObservable("The last product sold.")]
        public string? LastProduct => Rows.LastOrDefault().Product;

        public void NotOffered() { }
    }

    private AppDescriptor Publish(object app, string hostName = "Unnamed")
    {
        var server = new AppNetwork.AppServer();
        _servers.Add(server);
        return server.Offer(app, hostName, f =>
        {
            if (app is SalesApp s) Interlocked.Increment(ref s.CallsOnUiThread);
            return Task.FromResult(f());
        });
    }

    [Fact]
    public void A_published_app_is_listed_with_its_name_methods_and_values()
    {
        var sales = new SalesApp();
        Publish(sales);

        var apps = Apps.Running(includeSelf: true);
        apps.Should().ContainSingle(a => a.Name == "Sales");
        var app = apps.Single(a => a.Name == "Sales");
        app.Description.Should().Be("Records sales and knows the total.");
        app.ProcessId.Should().Be(Environment.ProcessId);
        app.Methods.Select(m => m.Name).Should().BeEquivalentTo("AddSale", "Total", "ProductsAsync", "Explode", "Recurse", "Slow");
        app.Values.Select(v => v.Name).Should().BeEquivalentTo("Count", "LastProduct");

        var described = app.Describe();
        described.Should().Contain("method AddSale(string product, decimal amount) → void — Adds a sale.");
        described.Should().Contain("method ProductsAsync(int max) → List<string>");
        described.Should().Contain("value Count: int — Number of sales.");
        described.Should().NotContain("NotOffered");

        Apps.IsRunning("sales").Should().BeTrue("names are matched without regard to case");
        Apps.IsRunning("Inventory").Should().BeFalse();
    }

    public sealed record ProductTotal(string Product, decimal Sum);

    [AppInfo("Totals", "Sums per product.")]
    public class TotalsApp
    {
        [McpObservable("The sum per product.")]
        public List<ProductTotal> PerProduct => [new("Coffee", 7m)];

        [McpCallable("The sum of one product.")]
        public ProductTotal Of(string product) => new(product, 0m);
    }

    [Fact]
    public async Task A_type_of_the_app_is_described_with_the_names_another_app_reads_it_by()
    {
        Publish(new TotalsApp());

        // A program torn out of an app wrote its own type for this with other names, and showed the sums without the products.
        var described = Apps.Find("Totals")!.Describe();
        described.Should().Contain("value PerProduct: List<ProductTotal { string Product, decimal Sum }> — The sum per product.");
        described.Should().Contain("method Of(string product) → ProductTotal { string Product, decimal Sum }");

        (await Apps.ReadAsync<List<ProductTotal>>("Totals", "PerProduct")).Should().ContainSingle().Which.Should().Be(new ProductTotal("Coffee", 7m));
    }

    [Fact]
    public void An_app_without_AppInfo_gets_the_name_its_host_gives_it()
    {
        Publish(new object(), "Untitled project");
        Apps.Find("Untitled project").Should().NotBeNull();
        Apps.Find("Untitled project")!.Methods.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_app_calls_methods_and_reads_values_over_the_pipe()
    {
        var sales = new SalesApp();
        Publish(sales);

        await Apps.CallAsync("Sales", "AddSale", "Coffee", 3.5m);
        await Apps.CallAsync("Sales", "AddSale", "Cake", 4.25m);

        (await Apps.CallAsync<decimal>("Sales", "Total")).Should().Be(7.75m);
        (await Apps.ReadAsync<int>("Sales", "Count")).Should().Be(2);
        (await Apps.ReadAsync<string>("Sales", "LastProduct")).Should().Be("Cake");
        (await Apps.CallAsync<List<string>>("Sales", "ProductsAsync", 1)).Should().Equal("Cake");
        (await Apps.CallAsync<List<string>>("Sales", "ProductsAsync")).Should().Equal("Cake", "Coffee");
        sales.CallsOnUiThread.Should().BeGreaterThan(0, "methods run through the dispatch the window supplies");
    }

    [Fact]
    public async Task The_NEOs_behind_two_apps_can_talk_past_them()
    {
        var published = Publish(new SalesApp());
        string? kind = null, content = null;
        AppNetwork.SystemHandler = (k, c) => { kind = k; content = c; return Task.FromResult<string?>("taken"); };
        try
        {
            var large = new string('x', 2_000_000);
            var answer = await AppNetwork.SendSystemAsync(published, "offer", large, TimeSpan.FromSeconds(5));

            answer.Should().Be("taken");
            kind.Should().Be("offer");
            content.Should().Be(large, "a whole program with its data goes this way");
            (await AppNetwork.SendSystemAsync(published, "ping", null, TimeSpan.FromSeconds(5))).Should().Be("taken");
            content.Should().BeNull();
        }
        finally
        {
            AppNetwork.SystemHandler = null;
        }
    }

    [Fact]
    public async Task Without_someone_to_answer_the_NEO_behind_an_app_says_so()
    {
        var published = Publish(new SalesApp());

        var refused = await Assert.ThrowsAsync<AppCallException>(() => AppNetwork.SendSystemAsync(published, "offer", "x", TimeSpan.FromSeconds(5)));

        refused.Message.Should().Contain("does not take such messages");
    }

    [Fact]
    public async Task Errors_reach_the_caller_as_exceptions_that_say_what_happened()
    {
        Publish(new SalesApp());

        var missingApp = await Assert.ThrowsAsync<AppNotFoundException>(() => Apps.CallAsync("Inventory", "Anything"));
        missingApp.Message.Should().Contain("Inventory");

        var missingMethod = await Assert.ThrowsAsync<AppCallException>(() => Apps.CallAsync("Sales", "NotOffered"));
        missingMethod.Message.Should().Contain("no method called 'NotOffered'");

        var threw = await Assert.ThrowsAsync<AppCallException>(() => Apps.CallAsync("Sales", "Explode"));
        threw.Message.Should().Contain("InvalidOperationException").And.Contain("no way");

        var slow = await Assert.ThrowsAsync<AppCallException>(() => Apps.CallAsync("Sales", "Slow"));
        slow.Message.Should().Contain("did not finish within 1 s");
    }

    [Fact]
    public async Task An_app_that_keeps_calling_itself_is_stopped_by_the_depth_limit()
    {
        Publish(new SalesApp());
        var ex = await Assert.ThrowsAsync<AppCallException>(() => Apps.CallAsync<int>("Sales", "Recurse"));
        ex.Message.Should().Contain("too deep");
    }

    [Fact]
    public async Task Watching_a_value_reports_the_current_value_and_every_change()
    {
        var sales = new SalesApp();
        Publish(sales);
        var seen = new List<int>();
        using (Apps.Watch<int>("Sales", "Count", n => { lock (seen) seen.Add(n); }, TimeSpan.FromMilliseconds(50)))
        {
            await WaitUntil(() => seen.Count >= 1);
            sales.Rows.Add(("Tea", 2m));
            await WaitUntil(() => seen.Count >= 2);
            sales.Rows.Add(("Milk", 1m));
            await WaitUntil(() => seen.Count >= 3);
        }
        lock (seen) seen.Should().Equal(0, 1, 2);
    }

    [Fact]
    public void A_dead_process_is_dropped_from_the_listing()
    {
        Directory.CreateDirectory(_folder);
        var ghost = new AppDescriptor("ghost", "Ghost", "", 999999, "neo-app-ghost", [], [], DateTimeOffset.Now);
        AppRegistry.Write(ghost);
        File.Exists(AppRegistry.PathFor("ghost")).Should().BeTrue();

        Apps.Running(includeSelf: true).Should().NotContain(a => a.Name == "Ghost");
        File.Exists(AppRegistry.PathFor("ghost")).Should().BeFalse("the entry is cleaned up");
    }

    [Fact]
    public async Task Publishing_again_replaces_the_offer_at_the_same_address()
    {
        var server = new AppNetwork.AppServer();
        _servers.Add(server);
        var first = server.Offer(new object(), "Draft", f => Task.FromResult(f()));
        var second = server.Offer(new SalesApp(), "Draft", f => Task.FromResult(f()));

        second.Id.Should().Be(first.Id);
        second.Pipe.Should().Be(first.Pipe);
        Apps.Running(includeSelf: true).Should().ContainSingle(a => a.Id == first.Id).Which.Name.Should().Be("Sales");
        (await Apps.ReadAsync<int>("Sales", "Count")).Should().Be(0);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        condition().Should().BeTrue("the condition should be met within 5 s");
    }

    public void Dispose()
    {
        foreach (var s in _servers) s.Dispose();
        Environment.SetEnvironmentVariable(AppRegistry.FolderEnvironmentVariable, null);
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }
}
