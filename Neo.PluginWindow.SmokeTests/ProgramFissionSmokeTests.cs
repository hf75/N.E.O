using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Forms;
using FluentAssertions;
using Neo.App;
using Xunit;
using static Neo.PluginWindow.SmokeTests.RealHost;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// A part torn out of another program, with real windows and the real mouse (the model is a stand-in): the program is a
/// stockroom window of this test, the real Avalonia host has written its interface. With Alt held, its booking group stands
/// out in a frame of N.E.O.'s over it; dragged out, it becomes a small program of its own that books through the interface,
/// and the person is asked before it changes anything.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class ProgramFissionSmokeTests : IDisposable
{
    private const string Host = "Neo.App.Avalonia";

    private readonly string _settings = Path.Combine(Path.GetTempPath(), "neo_pfission_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _registry = Path.Combine(Path.GetTempPath(), "neo_pfission_net_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _programs = Path.Combine(Path.GetTempPath(), "neo_pfission_out_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _title = "Stockroom" + Guid.NewGuid().ToString("N")[..8];
    private Stockroom? _program;

    public ProgramFissionSmokeTests()
    {
        Environment.SetEnvironmentVariable("NEO_APP_NETWORK_DIR", _registry);
        Environment.SetEnvironmentVariable(AppController.FissionFolderVariable, _programs);
        Apps.CallerName = "The test";
    }

    private string Interface => $$"""
        using System;
        using System.Threading.Tasks;
        using Avalonia.Controls;
        using Neo.App;

        [AppInfo("Stockroom", "The stockroom program: booking items out.", Program = "{{_title}}")]
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
                _stockroom = ForeignApp.Attach("{{_title}}", size: 260);
            }

            [McpCallable("Books a quantity of an item out.", Changes = true)]
            public async Task<string> BookOut(string item, int quantity)
            {
                await _stockroom.SetAsync("Item no.:", item);
                await _stockroom.SetAsync("Quantity:>Edit", quantity.ToString());
                await _stockroom.PressAsync("Book out");
                var status = await _stockroom.GetAsync("status");
                _log.Text = "LOG " + status;
                return status;
            }
        }
        """;

    // The small program the model writes from the part: it books through the interface, never in the stockroom itself.
    private const string Booking = """
        using System;
        using Avalonia.Controls;
        using Neo.App;

        [AppInfo("Booking", "Books items out of the stockroom; torn out of it.")]
        public class DynamicUserControl : UserControl
        {
            private TextBox _item = null!;
            private TextBox _quantity = null!;
            private TextBlock _answer = null!;

            public DynamicUserControl()
            {
                BuildUi();
            }

            private void BuildUi()
            {
                _item = new TextBox { Text = "4711" };
                _quantity = new TextBox { Text = "3" };
                _answer = new TextBlock { Text = "MINI ready" };
                var book = new Button { Content = "Book out here" };
                book.Click += async (_, _) =>
                {
                    try { _answer.Text = "MINI " + await Apps.CallAsync<string>("Stockroom", "BookOut", _item.Text ?? "", int.Parse(_quantity.Text ?? "0")); }
                    catch (Exception ex) { _answer.Text = "MINI failed: " + ex.Message; }
                };
                var panel = new StackPanel { Margin = new Avalonia.Thickness(12), Spacing = 6 };
                panel.Children.Add(_item);
                panel.Children.Add(_quantity);
                panel.Children.Add(book);
                panel.Children.Add(_answer);
                Content = panel;
            }
        }
        """;

    /// <summary>The other program: the booking fields in a group, the status and the list of bookings outside it.</summary>
    private sealed class Stockroom : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private Form _form = null!;
        private GroupBox _booking = null!;

        public int Stock { get; private set; } = 17; // of item 4711

        public Stockroom(string title, int x, int y)
        {
            _thread = new Thread(() =>
            {
                _form = new Form { Text = title, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(x, y, 520, 380) };
                _booking = new GroupBox { Text = "Booking", Location = new Point(12, 12), Size = new Size(340, 140) };
                var item = new TextBox { AccessibleName = "Item no.:", Location = new Point(100, 25), Width = 100 };
                var quantity = new TextBox { Location = new Point(100, 57), Width = 60 };
                var bookOut = new Button { Text = "Book out", Location = new Point(100, 92), AutoSize = true };
                _booking.Controls.AddRange([new Label { Text = "Item no.:", Location = new Point(12, 28), AutoSize = true }, item,
                    new Label { Text = "Quantity:", Location = new Point(12, 60), AutoSize = true }, quantity, bookOut]);
                var status = new Label { Name = "status", Text = "Ready.", Location = new Point(12, 165), AutoSize = true };
                var bookings = new ListBox { AccessibleName = "Bookings", Location = new Point(12, 195), Size = new Size(480, 120) };
                bookings.Items.AddRange(["4711 Copper pipe 17", "4712 Brass fitting 5", "4713 Valve 40"]);

                bookOut.Click += (_, _) =>
                {
                    if (item.Text == "4711" && int.TryParse(quantity.Text, out var q) && q <= Stock)
                    {
                        Stock -= q;
                        status.Text = $"Booked {q} x 4711 out. Stock now {Stock}.";
                    }
                    else status.Text = "Not booked.";
                };

                _form.Controls.AddRange([_booking, status, bookings]);
                _form.Shown += (_, _) => _ready.Set();
                Application.Run(_form);
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(10));
        }

        public IntPtr Handle => (IntPtr)_form.Invoke(() => _form.Handle);

        /// <summary>The booking group on the screen.</summary>
        public Rectangle Booking => (Rectangle)_form.Invoke(() => _form.RectangleToScreen(_booking.Bounds));

        public void Dispose()
        {
            try { _form.Invoke(_form.Close); } catch { }
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private static AutomationElement Panel(System.Diagnostics.Process host) =>
        System.Diagnostics.Process.GetProcessesByName(ChildProcessName(Host))
            .Where(p => TopLevelWindows.ParentProcessId(p.Id) == host.Id)
            .Select(p => TopLevelWindows.Find(w => w.Pid == p.Id && w.Title != "N.E.O. tear frame" && w.Title != "N.E.O. tear"))
            .First(w => w != null)!;

    // A program the first N.E.O. started: a N.E.O. playing the new project, and the window of its app.
    private static AutomationElement? NewProgram(System.Diagnostics.Process host)
    {
        foreach (var neo in System.Diagnostics.Process.GetProcessesByName(Host).Where(p => TopLevelWindows.ParentProcessId(p.Id) == host.Id))
            foreach (var window in System.Diagnostics.Process.GetProcessesByName(ChildProcessName(Host)).Where(p => TopLevelWindows.ParentProcessId(p.Id) == neo.Id))
                if (TopLevelWindows.Find(w => w.Pid == window.Id) is { } found) return found;
        return null;
    }

    private static AutomationElement? ButtonIn(AutomationElement window, string name) =>
        window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().FirstOrDefault(b => b.Current.Name == name);

    [Fact]
    public async Task A_part_dragged_out_of_another_program_with_Alt_becomes_a_small_program_that_uses_it_through_its_interface()
    {
        using var api = new StandInClaudeApi();
        api.AnswerWithCode(Interface);
        // The interface already has what the booking group does.
        api.AnswerWithText(System.Text.Json.JsonSerializer.Serialize(new { Code = "", Patch = "", NuGetPackages = Array.Empty<string>(), Explanation = "", Chat = "It offers booking out already.", PowerShellScript = "", ConsoleAppCode = "" }));
        api.AnswerWithCode(Booking);
        _program = new Stockroom(_title, 240, 160);

        using var host = Start(Host, api, _settings, new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true });
        try
        {
            await SendPromptAsync(host, $"Give {_title} an interface.");
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "LOG none", 60, "the interface", api);
            (await WaitForAsync(() => Apps.Find("Stockroom"), 20, "the interface on the app network")).Program.Should().Be(_title);
            await Task.Delay(3000); // hot reload arms itself in the background, and the interface follows its program from now on
            MoveWindow((IntPtr)TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O"))!.Current.NativeWindowHandle, 1100, 620, 640, 400, true);

            // Alt held over the booking group of the program: a frame of N.E.O.'s stands over it, and takes the press.
            var stockroom = AutomationElement.FromHandle(_program.Handle);
            var group = _program.Booking;
            int fromX = group.Right - 40, fromY = group.Top + 40;
            TopLevelWindows.Foreground(stockroom);
            AltDown(stockroom);
            try
            {
                TopLevelWindows.Hover(stockroom, fromX, fromY);
                var frame = await WaitForAsync(() => TopLevelWindows.Find(w => w.Title == "N.E.O. tear frame"), 5, "the frame over the group");
                frame.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, $"Drag it out with Alt: a small program that uses {_title}"))
                    .Should().NotBeNull("it says what a drag would do");
                var framed = frame.Current.BoundingRectangle;
                framed.Left.Should().BeApproximately(group.Left, 4, "the frame lies over the group");
                framed.Bottom.Should().BeApproximately(group.Bottom, 4);

                // Dragged out of the program's window, its picture goes along.
                await TopLevelWindows.DragAsync(frame, fromX, fromY, 900, 640, async () =>
                    await WaitForAsync(() => TopLevelWindows.Find(w => w.Title == "N.E.O. tear"), 5, "the picture of the part going along"));
            }
            finally
            {
                AltUp();
            }

            // The program keeps its part; the model saw the group's controls and nothing of the data in the program.
            AutomationElement created;
            try
            {
                created = await WaitForAsync(() => NewProgram(host) is { } w && TextsIn(w).Contains("MINI ready") ? w : null, 90, "the small program of its own");
            }
            catch (TimeoutException ex)
            {
                var neo = TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O"));
                throw new TimeoutException(ex.Message + $" | N.E.O. says: {(neo == null ? "(no window)" : string.Join(" | ", TextsIn(neo).TakeLast(12)))} | {api.Requests.Count} request(s)", ex);
            }
            // (The prompt names the program, so the whole window comes along as well, as for any prompt that does.)
            var toTheInterface = api.Prompts[1];
            toTheInterface.Should().Contain($"tore one part out of the program \"{_title}\"").And.Contain("the Group \"Booking\" at the address \"Booking\"");
            var part = toTheInterface[toTheInterface.IndexOf("Its controls as they are now", StringComparison.Ordinal)..];
            part = part[..part.IndexOf("Change this interface", StringComparison.Ordinal)];
            part.Should().Contain("Edit \"Item no.:\"").And.Contain("Edit \"Quantity:\"").And.Contain("Button \"Book out\"")
                .And.NotContain("Bookings", "the part's own controls, not the rest of the window");
            toTheInterface.Should().NotContain("Copper pipe");
            api.Prompts[2].Should().Contain("Write the program \"Booking\"").And.Contain("calling the interface \"Stockroom\"").And.NotContain("Copper pipe");
            GetWindowRect((IntPtr)created.Current.NativeWindowHandle, out var placed);
            placed.L.Should().BeInRange(780, 960, "it opens where the part was let go");
            Directory.GetFiles(_programs, "*.resx").Should().ContainSingle().Which.Should().EndWith("Booking.resx");

            // It books through the interface, and the person is asked first.
            TopLevelWindows.Click(created, ButtonIn(created, "Book out here")!);
            var panel = await WaitForAsync(() => Panel(host), 10, "the interface");
            var always = await WaitForAsync(() => ButtonIn(panel, "Always for Booking"), 20, "the question in the interface, naming the small program");
            TopLevelWindows.Click(panel, always);
            await WaitForAsync(() => NewProgram(host) is { } w && TextsIn(w).Contains("MINI Booked 3 x 4711 out. Stock now 14.") ? "booked" : null, 20, "the booking done through the interface");
            _program.Stock.Should().Be(14);
        }
        finally
        {
            Stop(host);
        }
    }

    // Alt for the drag, only while the program's window is in front: it must reach no other window.
    private static void AltDown(AutomationElement window)
    {
        TopLevelWindows.Foreground(window);
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        if (pid != window.Current.ProcessId) throw new InvalidOperationException("The program's window is not in front; not pressing Alt.");
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
    }

    private static void AltUp() => keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

    public void Dispose()
    {
        _program?.Dispose();
        Environment.SetEnvironmentVariable("NEO_APP_NETWORK_DIR", null);
        Environment.SetEnvironmentVariable(AppController.FissionFolderVariable, null);
        Apps.CallerName = null;
        foreach (var folder in new[] { _settings, _registry, _programs })
            try { Directory.Delete(folder, recursive: true); } catch { }
    }

    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x2;
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
