using System.Drawing;
using System.Windows.Automation;
using System.Windows.Forms;
using FluentAssertions;
using Neo.App;
using Xunit;
using static Neo.PluginWindow.SmokeTests.RealHost;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// The interface of another program, with real windows: the real Avalonia host builds it (the model is a stand-in),
/// the other program is a stockroom window of this test, and this test calls the interface over the app network the
/// way another app does. Writing waits for the person; when the program gets a new version, the call waits while the
/// interface is repaired and then goes on.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class ProgramInterfaceSmokeTests : IDisposable
{
    private const string Host = "Neo.App.Avalonia";

    private readonly string _settings = Path.Combine(Path.GetTempPath(), "neo_iface_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _registry = Path.Combine(Path.GetTempPath(), "neo_iface_net_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _title = "Stockroom" + Guid.NewGuid().ToString("N")[..8];
    private Stockroom? _program;

    public ProgramInterfaceSmokeTests()
    {
        Environment.SetEnvironmentVariable("NEO_APP_NETWORK_DIR", _registry);
        Apps.CallerName = "The test";
    }

    private string Interface(string quantity, string bookOut, string version) => $$"""
        using System;
        using System.Threading.Tasks;
        using Avalonia.Controls;
        using Neo.App;

        [AppInfo("Stockroom", "The stockroom program: the stock of an item, and booking items out.", Program = "{{_title}}")]
        public class DynamicUserControl : UserControl
        {
            private readonly TextBlock _log = new TextBlock { Text = "LOG none" };
            private readonly ForeignApp _stockroom;

            public DynamicUserControl()
            {
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = "Stockroom interface" });
                panel.Children.Add(_log);
                Content = panel;
                _stockroom = ForeignApp.Attach("{{_title}}", size: 280);
            }

            [McpCallable("The stock of an item.")]
            public async Task<string> Stock(string item)
            {
                await _stockroom.SetAsync("Item no.:", item);
                await _stockroom.PressAsync("Look up");
                var stock = await _stockroom.GetAsync("Stock:>Text");
                _log.Text = "LOG{{version}} stock " + stock;
                return stock;
            }

            [McpCallable("Books a quantity of an item out.", Changes = true)]
            public async Task<string> BookOut(string item, int quantity)
            {
                await _stockroom.SetAsync("Item no.:", item);
                await _stockroom.SetAsync("{{quantity}}", quantity.ToString());
                await _stockroom.PressAsync("{{bookOut}}");
                var status = await _stockroom.GetAsync("status");
                _log.Text = "LOG{{version}} " + status;
                return status;
            }
        }
        """;

    /// <summary>The other program: a stockroom of the kind nobody has the source of, on a thread of its own.</summary>
    private sealed class Stockroom : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private Form _form = null!;
        private Label _quantityLabel = null!;
        private Button _bookOut = null!;

        public int Stock { get; private set; } = 17; // of item 4711

        public Stockroom(string title, int x, int y)
        {
            _thread = new Thread(() =>
            {
                _form = new Form { Text = title, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(x, y, 460, 420) };
                var itemLabel = new Label { Text = "Item no.:", Location = new Point(12, 15), AutoSize = true };
                var item = new TextBox { Name = "itemBox", AccessibleName = "Item no.:", Location = new Point(110, 12), Width = 120 };
                _quantityLabel = new Label { Text = "Quantity:", Location = new Point(12, 45), AutoSize = true };
                var quantity = new TextBox { Location = new Point(110, 42), Width = 60 };
                var lookUp = new Button { Text = "Look up", Location = new Point(250, 10), AutoSize = true };
                _bookOut = new Button { Text = "Book out", Location = new Point(250, 40), AutoSize = true };
                var stockLabel = new Label { Text = "Stock:", Location = new Point(12, 80), AutoSize = true };
                var stockValue = new Label { Text = "", Location = new Point(110, 80), AutoSize = true };
                var status = new Label { Name = "status", Text = "Ready.", Location = new Point(12, 110), AutoSize = true };
                var bookings = new ListBox { AccessibleName = "Bookings", Location = new Point(12, 140), Size = new Size(420, 200) };
                bookings.Items.AddRange(["4711 Copper pipe 17", "4712 Brass fitting 5", "4713 Valve 40"]);

                lookUp.Click += (_, _) => stockValue.Text = item.Text == "4711" ? Stock.ToString() : "0";
                _bookOut.Click += (_, _) =>
                {
                    if (item.Text == "4711" && int.TryParse(quantity.Text, out var q) && q <= Stock)
                    {
                        Stock -= q;
                        status.Text = $"Booked {q} x 4711 out. Stock now {Stock}.";
                    }
                    else status.Text = "Not booked.";
                };

                _form.Controls.AddRange([itemLabel, item, _quantityLabel, quantity, lookUp, _bookOut, stockLabel, stockValue, status, bookings]);
                _form.Shown += (_, _) => _ready.Set();
                Application.Run(_form);
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(10));
        }

        public IntPtr Handle => (IntPtr)_form.Invoke(() => _form.Handle);

        /// <summary>The next version of the program: a label and a button are called differently.</summary>
        public void Update() => _form.Invoke(() =>
        {
            _quantityLabel.Text = "Amount:";
            _bookOut.Text = "Issue";
        });

        public void Dispose()
        {
            try { _form.Invoke(_form.Close); } catch { }
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private static AutomationElement Panel(System.Diagnostics.Process host) =>
        System.Diagnostics.Process.GetProcessesByName(ChildProcessName(Host))
            .Where(p => TopLevelWindows.ParentProcessId(p.Id) == host.Id)
            .Select(p => TopLevelWindows.Find(w => w.Pid == p.Id))
            .First(w => w != null)!;

    private static AutomationElement? ButtonIn(AutomationElement window, string name) =>
        window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().FirstOrDefault(b => b.Current.Name == name);

    /// <summary>The panel asks whether the test may change something; the person answers with this button.</summary>
    private static async Task AnswerAsync(System.Diagnostics.Process host, string button)
    {
        var panel = await WaitForAsync(() => Panel(host), 10, "the panel");
        var answer = await WaitForAsync(() => ButtonIn(panel, button), 20, $"the question in the panel, with '{button}'");
        TopLevelWindows.Click(panel, answer);
    }

    [Fact]
    public async Task Another_app_uses_the_program_through_its_interface_and_a_new_version_is_repaired_while_the_call_waits()
    {
        using var api = new StandInClaudeApi();
        api.AnswerWithCode(Interface("Quantity:>Edit", "Book out", ""));
        api.AnswerWithCode(Interface("Amount:>Edit", "Issue", " v2"));
        _program = new Stockroom(_title, 300, 200);

        using var host = Start(Host, api, _settings, new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true });
        try
        {
            await SendPromptAsync(host, $"Give {_title} an interface.");
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "LOG none", 60, "the interface", api);

            // The model saw the labels and controls of the program, and nothing of its data.
            var first = api.Prompts[0];
            first.Should().Contain("Edit \"Item no.:\"").And.Contain("Button \"Book out\"").And.Contain("Text \"Stock:\"").And.Contain("id=status");
            first.Should().Match(p => p.Contains("Edit \"Quantity:\"") || p.Contains("[Quantity:>Edit]"), "the field after the label is named by it, or addressed through it");
            first.Should().NotContain("Copper pipe").And.Contain("ListItem ~\"9999 Xxxxxx xxxx 99\"");
            var system = System.Text.Json.Nodes.JsonNode.Parse(api.Requests[0])!["system"]!.ToJsonString();
            system.Should().Contain("INTERFACES").And.Contain("Changes = true");

            // On the app network as the interface of the program, and a caller may wait for a repair.
            var offered = await WaitForAsync(() => Apps.Find("Stockroom"), 20, "the interface on the app network");
            offered.Program.Should().Be(_title);
            offered.RepairSeconds.Should().BeGreaterThan(0);
            offered.Methods.Single(m => m.Name == "BookOut").Changes.Should().BeTrue();

            // Reading asks nobody.
            (await Apps.CallAsync<string>("Stockroom", "Stock", "4711")).Should().Be("17");

            // A change waits for the person. No: nothing happens.
            var refused = Apps.CallAsync<string>("Stockroom", "BookOut", "4711", 3);
            await AnswerAsync(host, "No");
            (await Assert.ThrowsAsync<AppCallException>(() => refused)).Message.Should().Contain("did not allow");
            _program.Stock.Should().Be(17);

            // Allowed for this caller from now on.
            var booking = Apps.CallAsync<string>("Stockroom", "BookOut", "4711", 3);
            await AnswerAsync(host, "Always for The test");
            (await booking).Should().Be("Booked 3 x 4711 out. Stock now 14.");

            // The program gets a new version. The call finds it changed, waits while the interface is repaired, and goes on.
            await Task.Delay(2000); // hot reload arms itself in the background
            _program.Update();
            string repaired;
            try
            {
                repaired = (await Apps.CallAsync<string>("Stockroom", "BookOut", "4711", 2))!;
            }
            catch (AppCallException ex)
            {
                var neo = TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O"));
                throw new InvalidOperationException(ex.Message + " N.E.O. says: " + (neo == null ? "(no window)" : string.Join(" | ", TextsIn(neo))) + $" ({api.Requests.Count} request(s))", ex);
            }
            repaired.Should().Be("Booked 2 x 4711 out. Stock now 12.");
            _program.Stock.Should().Be(12);
            await WaitForTextAsync(host, ChildProcessName(Host), t => t.StartsWith("LOG v2 Booked"), 10, "the repaired interface", api);
            await WaitForTextAsync(host, ChildProcessName(Host), t => t.StartsWith("Repaired in place"), 10, "the repair patched into the running interface, not a new one", api);

            // The repair saw how the program looks now, and still nothing of its data.
            api.Prompts.Should().HaveCount(2);
            var repair = api.Prompts[1];
            repair.Should().Contain("no control \"Quantity:>Edit\"").And.Contain("Text \"Amount:\"").And.Contain("Button \"Issue\"");
            repair.Should().NotContain("Copper pipe");
            await WaitForTextAsync(host, ChildProcessName(Host), t => t.Contains("the interface fits again"), 10, "the chat saying what the model saw", api);
        }
        finally
        {
            Stop(host);
        }
    }

    // ── Links with the program: an app dropped on its window, and its window dropped on an app ──

    private readonly string _settingsMeter = Path.Combine(Path.GetTempPath(), "neo_iface_meter_" + Guid.NewGuid().ToString("N")[..8]);

    private static object Settings => new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true };

    // What the model of the N.E.O. dropped on suggests: pressing Bump asks the stockroom for its stock.
    private static string Proposal(string meter, string stockroom) => Newtonsoft.Json.JsonConvert.SerializeObject(new
    {
        blocks = new[] { new { title = "Bump looks up the stock", detail = "Pressing Bump asks the stockroom for item 4711.", code = $"{meter}.OnClick(\"Button:0\", () => {stockroom}.Call(\"Stock\", \"4711\"));" } },
        note = "",
    });

    private async Task<(System.Diagnostics.Process Meter, System.Diagnostics.Process Interface)> MeterAndInterfaceAsync(StandInClaudeApi meterApi, StandInClaudeApi interfaceApi)
    {
        _program = new Stockroom(_title, 760, 120);
        var meter = Start(Host, meterApi, _settingsMeter, Settings);
        var stockroom = Start(Host, interfaceApi, _settings, Settings);
        await SendPromptAsync(meter, "Build it.");
        await WaitForTextAsync(meter, ChildProcessName(Host), t => t.StartsWith("METER 0"), 60, "the meter", meterApi);
        await SendPromptAsync(stockroom, $"Give {_title} an interface.");
        await WaitForTextAsync(stockroom, ChildProcessName(Host), t => t == "LOG none", 60, "the interface", interfaceApi);
        await Task.Delay(3000); // hot reload arms itself in the background, and a N.E.O. docks its app once more after a prompt

        // Both N.E.O.s out of the way; the meter on the left, the program with its panel on the right.
        foreach (var host in new[] { meter, stockroom })
            MoveWindow((IntPtr)TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O"))!.Current.NativeWindowHandle, 650, 600, 640, 400, true);
        MoveWindow((IntPtr)AppLinkSmokeTests.AppOf(meter).Current.NativeWindowHandle, 100, 120, 500, 420, true);
        await Task.Delay(400);
        return (meter, stockroom);
    }

    private AutomationElement ProgramWindow => AutomationElement.FromHandle(_program!.Handle);

    private static AutomationElement? Overlay() => TopLevelWindows.Find(w => w.Title == "N.E.O. drop");

    [Fact]
    public async Task An_app_dropped_on_the_window_of_a_program_links_with_its_interface()
    {
        using var meterApi = new StandInClaudeApi();
        using var interfaceApi = new StandInClaudeApi();
        meterApi.AnswerWithCode(AppLinkSmokeTests.Meter);
        interfaceApi.AnswerWithCode(Interface("Quantity:>Edit", "Book out", ""));
        interfaceApi.AnswerWithText(Proposal("a", "b"));

        var (meter, stockroom) = await MeterAndInterfaceAsync(meterApi, interfaceApi);
        try
        {
            var dragged = AppLinkSmokeTests.AppOf(meter);
            TopLevelWindows.Foreground(ProgramWindow);
            TopLevelWindows.Foreground(dragged);
            await Task.Delay(300);
            var from = AppLinkSmokeTests.TitleBarOf(dragged);
            GetWindowRect(_program!.Handle, out var r);

            // Over the program the halves show, and the right one links.
            await TopLevelWindows.DragAsync(dragged, from.X, from.Y, r.L + (r.R - r.L) * 3 / 4, r.T + 150, async () =>
                await WaitForAsync(() => Overlay() is { } over ? TextsIn(over).FirstOrDefault(t => t == "Drop to link Meter with this program") : null, 5, "the halves over the program"));

            var dialog = await AppLinkSmokeTests.DialogOfAsync(stockroom);
            dialog.Current.Name.Should().Be("Link Meter with Stockroom", "the program is linked through its interface");
            await WaitForAsync(() => Overlay() == null ? "gone" : null, 5, "the halves going after the drop");
            await AppLinkSmokeTests.ConnectAsync(stockroom);

            await WaitForTextAsync(meter, ChildProcessName(Host), t => t == "Linked to Stockroom", 10, "the chip on the meter", meterApi);
            var inMeter = AppLinkSmokeTests.AppOf(meter);
            TopLevelWindows.Click(inMeter, inMeter.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                .Cast<AutomationElement>().First(b => b.Current.Name == "Bump"));
            await WaitForTextAsync(stockroom, ChildProcessName(Host), t => t == "LOG stock 17", 10, "the stockroom looked up through the link", interfaceApi);
        }
        finally
        {
            Stop(meter);
            Stop(stockroom);
        }
    }

    [Fact]
    public async Task The_window_of_a_program_dragged_onto_an_app_links_its_interface_with_it()
    {
        using var meterApi = new StandInClaudeApi();
        using var interfaceApi = new StandInClaudeApi();
        meterApi.AnswerWithCode(AppLinkSmokeTests.Meter);
        meterApi.AnswerWithText(Proposal("b", "a"));
        interfaceApi.AnswerWithCode(Interface("Quantity:>Edit", "Book out", ""));

        var (meter, stockroom) = await MeterAndInterfaceAsync(meterApi, interfaceApi);
        try
        {
            var target = AppLinkSmokeTests.AppOf(meter);
            var program = ProgramWindow;
            GetWindowRect(_program!.Handle, out var before);
            TopLevelWindows.Foreground(target);
            TopLevelWindows.Foreground(program);
            await Task.Delay(300);
            var from = AppLinkSmokeTests.TitleBarOf(program);
            GetWindowRect((IntPtr)target.Current.NativeWindowHandle, out var r);

            // Its code is not here: the app says that a drop links, on either half.
            await TopLevelWindows.DragAsync(program, from.X, from.Y, r.L + (r.R - r.L) / 4, r.T + 150, async () =>
                await WaitForAsync(() => TextsIn(AppLinkSmokeTests.AppOf(meter)).FirstOrDefault(t => t == "Drop to link Stockroom with this app"), 5, "the meter saying that a drop would link"));

            var dialog = await AppLinkSmokeTests.DialogOfAsync(meter);
            dialog.Current.Name.Should().Be("Link Stockroom with Meter");
            await WaitForAsync(() => { GetWindowRect(_program.Handle, out var now); return now.L == before.L && now.T == before.T ? "back" : null; }, 5, "the program back where it was");
            await AppLinkSmokeTests.ConnectAsync(meter);

            await WaitForTextAsync(meter, ChildProcessName(Host), t => t == "Linked to Stockroom", 10, "the chip on the meter", meterApi);
            var inMeter = AppLinkSmokeTests.AppOf(meter);
            TopLevelWindows.Click(inMeter, inMeter.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                .Cast<AutomationElement>().First(b => b.Current.Name == "Bump"));
            await WaitForTextAsync(stockroom, ChildProcessName(Host), t => t == "LOG stock 17", 10, "the stockroom looked up through the link", interfaceApi);
        }
        finally
        {
            Stop(meter);
            Stop(stockroom);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct Rect { public int L, T, R, B; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);

    public void Dispose()
    {
        try { Directory.Delete(_settingsMeter, recursive: true); } catch { }
        _program?.Dispose();
        Apps.CallerName = null;
        Environment.SetEnvironmentVariable("NEO_APP_NETWORK_DIR", null);
        try { Directory.Delete(_settings, recursive: true); } catch { }
        try { Directory.Delete(_registry, recursive: true); } catch { }
    }
}
