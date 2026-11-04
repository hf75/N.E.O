using FluentAssertions;
using Neo.App;
using Xunit;

namespace Neo.App.Api.Tests;

/// <summary>
/// The interface of another program on the app network, in this process over the real pipes: it says which program it
/// is for, the person is asked before a method that changes something runs for another caller, and a call that finds
/// the program changed waits while the interface is repaired, then goes again.
/// </summary>
[Collection("AppNetwork")]
public class ProgramInterfaceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "neo_interface_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<AppNetwork.AppServer> _servers = new();

    public ProgramInterfaceTests()
    {
        Environment.SetEnvironmentVariable(AppRegistry.FolderEnvironmentVariable, _folder);
        Apps.CallerName = "The test";
    }

    /// <summary>What the helper compiled into an app throws when a control of the other program is not there.</summary>
    public sealed class ForeignControlMissingException(string program, string control)
        : Exception($"{program} has no control \"{control}\". The program may have changed.")
    {
        public string Program { get; } = program;
    }

    [AppInfo("Stockroom", "The stock of items, and booking them out.", Program = "stockroom")]
    public class StockroomInterface
    {
        public string Button = "Issue";   // what the program calls the button now
        public string Pressed = "Book out"; // what the interface presses
        public int BookedOut;

        [McpCallable("The stock of an item.")]
        public int Stock(string item) => item == "4711" ? 17 - BookedOut : 0;

        [McpCallable("Books a quantity of an item out.", Changes = true)]
        public string BookOut(string item, int quantity)
        {
            if (Pressed != Button) throw new ForeignControlMissingException("stockroom", Pressed);
            BookedOut += quantity;
            return $"Booked {quantity} x {item} out.";
        }

        [McpCallable("Fails in another way.")]
        public void Broken() => throw new InvalidOperationException("not this way");
    }

    private AppDescriptor Publish(object app)
    {
        var server = new AppNetwork.AppServer();
        _servers.Add(server);
        return server.Offer(app, "Unnamed", f => Task.FromResult(f()));
    }

    [Fact]
    public void An_interface_says_which_program_it_is_for_and_which_methods_change_something()
    {
        var published = Publish(new StockroomInterface());

        published.Program.Should().Be("stockroom");
        published.Methods.Single(m => m.Name == "BookOut").Changes.Should().BeTrue();
        published.Methods.Single(m => m.Name == "Stock").Changes.Should().BeFalse();
        published.RepairSeconds.Should().Be(0, "nobody here can repair it");

        var described = Apps.Find("Stockroom")!.Describe();
        described.Should().Contain("(the interface of the program \"stockroom\")");
        described.Should().Contain("method BookOut(string item, int quantity) → string [changes something: the person is asked first]");
        described.Should().NotContain("method Stock(string item) → int [changes");
    }

    [Fact]
    public void Where_a_repair_can_happen_a_caller_is_told_how_long_it_may_wait()
    {
        AppNetwork.RepairHandler = _ => Task.FromResult(true);

        Publish(new StockroomInterface()).RepairSeconds.Should().Be((int)AppNetwork.RepairAllowance.TotalSeconds);
        Publish(new object()).RepairSeconds.Should().Be(0, "an app that is no interface waits for no program");
    }

    [Fact]
    public async Task A_change_runs_only_when_the_person_allows_it()
    {
        var stockroom = new StockroomInterface { Pressed = "Issue" };
        Publish(stockroom);
        var asked = new List<ChangeRequest>();
        var allow = false;
        AppNetwork.ChangeGate = request => { lock (asked) asked.Add(request); return Task.FromResult(allow); };

        var refused = await Assert.ThrowsAsync<AppCallException>(() => Apps.CallAsync<string>("Stockroom", "BookOut", "4711", 3));
        refused.Message.Should().Contain("did not allow");
        stockroom.BookedOut.Should().Be(0);

        allow = true;
        (await Apps.CallAsync<string>("Stockroom", "BookOut", "4711", 3)).Should().Be("Booked 3 x 4711 out.");
        stockroom.BookedOut.Should().Be(3);

        (await Apps.CallAsync<int>("Stockroom", "Stock", "4711")).Should().Be(14);
        asked.Should().HaveCount(2, "reading asks nobody");
        asked[0].Should().Be(new ChangeRequest("Stockroom", "BookOut", "\"4711\", 3", "The test"));
    }

    [Fact]
    public async Task Without_anyone_to_ask_a_change_is_refused()
    {
        var stockroom = new StockroomInterface { Pressed = "Issue" };
        Publish(stockroom);

        var refused = await Assert.ThrowsAsync<AppCallException>(() => Apps.CallAsync<string>("Stockroom", "BookOut", "4711", 3));

        refused.Message.Should().Contain("cannot ask the person");
        stockroom.BookedOut.Should().Be(0);
    }

    [Fact]
    public async Task A_call_that_finds_the_program_changed_waits_for_the_repair_and_goes_again()
    {
        var stockroom = new StockroomInterface();
        Publish(stockroom);
        AppNetwork.ChangeGate = _ => Task.FromResult(true);
        RepairRequest? repairing = null;
        AppNetwork.RepairHandler = async request =>
        {
            repairing = request;
            await Task.Delay(300);             // the model writes the fix
            stockroom.Pressed = stockroom.Button; // the fix is in the running app
            return true;
        };

        (await Apps.CallJsonAsync("Stockroom", "BookOut", """["4711", 3]""")).Should().Be("\"Booked 3 x 4711 out.\"");

        stockroom.BookedOut.Should().Be(3, "the call went once more, after the repair, and only then");
        repairing.Should().NotBeNull();
        repairing!.App.Should().Be("Stockroom");
        repairing.Method.Should().Be("BookOut");
        repairing.Program.Should().Be("stockroom");
        repairing.ExceptionType.Should().EndWith("ForeignControlMissingException");
        repairing.Message.Should().Contain("no control \"Book out\"");
    }

    [Fact]
    public async Task A_repair_that_does_not_happen_gives_the_caller_the_error()
    {
        Publish(new StockroomInterface());
        AppNetwork.ChangeGate = _ => Task.FromResult(true);
        AppNetwork.RepairHandler = _ => Task.FromResult(false);

        var failed = await Assert.ThrowsAsync<AppCallException>(() => Apps.CallAsync<string>("Stockroom", "BookOut", "4711", 3));

        failed.Message.Should().Contain("ForeignControlMissingException").And.Contain("no control \"Book out\"").And.Contain("could not be repaired");
    }

    [Fact]
    public async Task Only_a_changed_program_is_repaired()
    {
        Publish(new StockroomInterface());
        var repairs = 0;
        AppNetwork.RepairHandler = _ => { Interlocked.Increment(ref repairs); return Task.FromResult(true); };

        var failed = await Assert.ThrowsAsync<AppCallException>(() => Apps.CallAsync("Stockroom", "Broken"));

        failed.Message.Should().Contain("not this way");
        repairs.Should().Be(0);
    }

    [Fact]
    public async Task Arguments_and_results_can_go_as_JSON()
    {
        Publish(new StockroomInterface());

        (await Apps.CallJsonAsync("Stockroom", "Stock", """["4711"]""")).Should().Be("17");
        (await Apps.CallJsonAsync("Stockroom", "Stock", "[\"1\"]")).Should().Be("0");
        await Assert.ThrowsAsync<AppCallException>(() => Apps.CallJsonAsync("Stockroom", "Stock", """{"item":"4711"}"""));
    }

    public void Dispose()
    {
        AppNetwork.ChangeGate = null;
        AppNetwork.RepairHandler = null;
        Apps.CallerName = null;
        foreach (var s in _servers) s.Dispose();
        Environment.SetEnvironmentVariable(AppRegistry.FolderEnvironmentVariable, null);
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }
}
