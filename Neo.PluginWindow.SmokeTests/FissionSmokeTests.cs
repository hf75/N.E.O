using System.Runtime.InteropServices;
using System.Windows.Automation;
using FluentAssertions;
using Neo.App;
using Xunit;
using static Neo.PluginWindow.SmokeTests.RealHost;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// A part torn out of a running program with Alt and the real mouse, with the real Avalonia host (the model is a stand-in):
/// it leaves the program, and a program of its own starts where it was let go and gets its data from the first one.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class FissionSmokeTests : IDisposable
{
    private const string Host = "Neo.App.Avalonia";

    private readonly string _settings = Path.Combine(Path.GetTempPath(), "neo_fission_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _registry = Path.Combine(Path.GetTempPath(), "neo_fission_net_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _programs = Path.Combine(Path.GetTempPath(), "neo_fission_out_" + Guid.NewGuid().ToString("N")[..8]);

    public FissionSmokeTests()
    {
        Environment.SetEnvironmentVariable("NEO_APP_NETWORK_DIR", _registry);
        Environment.SetEnvironmentVariable(AppController.FissionFolderVariable, _programs);
    }

    // The list and its total, side by side, the total in a column without a background as generated apps lay it out:
    // its empty space is where a person points at it. The box in it is what gets torn out. Built the way N.E.O. asks
    // generated apps to be built, so that taking the part out is patched into the running app.
    private static string Expenses(bool withTotal) => $$"""
        using System.Collections.Generic;
        using System.Linq;
        using Avalonia.Controls;
        using Avalonia.Layout;
        using Neo.App;

        [AppInfo("Expenses")]
        public class DynamicUserControl : UserControl
        {
            private readonly List<decimal> _amounts = new();
            private TextBox _amount = null!;
            private TextBlock _rows = null!;
            {{(withTotal ? "private TextBlock _total = null!;" : "")}}

            {{(withTotal ? "" : "[McpObservable(\"The total of all expenses.\")] public decimal Total => _amounts.Sum();")}}

            public DynamicUserControl()
            {
                BuildUi();
            }

            private void BuildUi()
            {
                _amount = new TextBox { Width = 120 };
                _rows = new TextBlock { Text = "ROWS " + _amounts.Count };
                var add = new Button { Content = "Add" };
                add.Click += (_, _) => { if (decimal.TryParse(_amount.Text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var a)) _amounts.Add(a); Show(); };
                var list = new StackPanel { Width = 220, Spacing = 6 };
                list.Children.Add(_amount);
                list.Children.Add(add);
                list.Children.Add(_rows);
                var both = new Grid { Margin = new Avalonia.Thickness(20, 60, 20, 20), ColumnDefinitions = new ColumnDefinitions("250,*") };
                both.Children.Add(list);
                {{(withTotal ? TotalColumn : "")}}
                Content = both;
            }

            private void Show()
            {
                _rows.Text = "ROWS " + _amounts.Count;
                {{(withTotal ? "_total.Text = \"TOTAL \" + _amounts.Sum();" : "")}}
            }
        }
        """;

    private const string TotalColumn = """
        _total = new TextBlock { Text = "TOTAL " + _amounts.Sum(), FontSize = 22 };
        var column = new StackPanel { Name = "totalPanel", Spacing = 8 };
        column.Children.Add(new TextBlock { Text = "Total of all expenses" });
        column.Children.Add(new Border { Name = "totalBox", Width = 180, Height = 80, HorizontalAlignment = HorizontalAlignment.Left, Background = Avalonia.Media.Brushes.Bisque, Child = _total });
        Grid.SetColumn(column, 1);
        both.Children.Add(column);
        """;

    private const string Total = """
        using System;
        using Avalonia.Controls;
        using Neo.App;

        [AppInfo("Total box", "The total of the expenses, torn out of Expenses.")]
        public class DynamicUserControl : UserControl
        {
            private readonly TextBlock _total = new TextBlock { Text = "NEW waiting", FontSize = 22 };
            private IDisposable? _watch;

            public DynamicUserControl()
            {
                Content = new Border { Background = Avalonia.Media.Brushes.Bisque, Child = _total };
                _watch = Apps.Watch<decimal>("Expenses", "Total", total => _total.Text = "NEW TOTAL " + total.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        """;

    private static AutomationElement App(System.Diagnostics.Process host) => AppLinkSmokeTests.AppOf(host);

    // What says, with Alt held, which part a drag would tear out; it lies just above that part.
    private static AutomationElement? Pill(AutomationElement app) =>
        app.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Drag it out with Alt: a program of its own"));

    // A program the first N.E.O. started: a N.E.O. playing the new project, and the window of its app.
    private static AutomationElement? NewProgram(System.Diagnostics.Process host)
    {
        foreach (var neo in System.Diagnostics.Process.GetProcessesByName(Host).Where(p => TopLevelWindows.ParentProcessId(p.Id) == host.Id))
            foreach (var window in System.Diagnostics.Process.GetProcessesByName(ChildProcessName(Host)).Where(p => TopLevelWindows.ParentProcessId(p.Id) == neo.Id))
                if (TopLevelWindows.Find(w => w.Pid == window.Id) is { } found) return found;
        return null;
    }

    [Fact]
    public async Task A_part_dragged_out_with_Alt_becomes_a_program_of_its_own_that_gets_its_data_from_the_first()
    {
        using var api = new StandInClaudeApi();
        api.AnswerWithCode(Expenses(withTotal: true));
        api.AnswerWithCode(Expenses(withTotal: false));
        api.AnswerWithCode(Total);

        using var host = Start(Host, api, _settings, new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true });
        try
        {
            await SendPromptAsync(host, "Build it.");
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "TOTAL 0", 60, "the app", api);
            await Task.Delay(3000); // hot reload arms itself in the background, and a N.E.O. docks its app once more after a prompt
            MoveWindow((IntPtr)TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O"))!.Current.NativeWindowHandle, 900, 600, 640, 400, true);
            var app = App(host);
            MoveWindow((IntPtr)app.Current.NativeWindowHandle, 100, 120, 560, 360, true);
            await Task.Delay(400);

            // Something the app holds: an expense from before the tear. Taking the part out drops the field of its total.
            var amount = app.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>().First();
            ((ValuePattern)amount.GetCurrentPattern(ValuePattern.Pattern)).SetValue("5");
            TopLevelWindows.Click(app, app.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                .Cast<AutomationElement>().First(b => b.Current.Name == "Add"));
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "TOTAL 5", 10, "the expense from before", api);

            // Alt held over the empty space of the column: the column stands out, though nothing there is hit by a click.
            var total = app.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "TOTAL 5")).Cast<AutomationElement>().First();
            var at = total.Current.BoundingRectangle;
            int fromX = (int)(at.X + at.Width / 2), fromY = (int)(at.Y + at.Height / 2);
            TopLevelWindows.Foreground(app);
            AltDown(app);
            try
            {
                TopLevelWindows.Hover(app, (int)at.X + 10, (int)at.Bottom + 60);
                var columnTop = (await WaitForAsync(() => Pill(app), 5, "the column standing out over its empty space")).Current.BoundingRectangle.Y;

                // Over the box the column still stands out, the biggest part; the wheel turned towards you goes to the box.
                TopLevelWindows.Hover(app, fromX, fromY);
                await Task.Delay(300);
                Pill(app)!.Current.BoundingRectangle.Y.Should().BeApproximately(columnTop, 2, "the biggest part under the pointer stands out first");
                TopLevelWindows.Wheel(app, -1);
                await WaitForAsync(() => Pill(app) is { } pill && pill.Current.BoundingRectangle.Y > columnTop + 10 ? pill : null, 5, "the box standing out after the wheel");

                // Dragged out of the window with Alt, its picture goes along.
                await TopLevelWindows.DragAsync(app, fromX, fromY, 900, 250, async () =>
                    await WaitForAsync(() => TopLevelWindows.Find(w => w.Title == "N.E.O. tear"), 5, "the picture of the part going along"));
            }
            finally
            {
                AltUp();
            }

            // It leaves the first program, which goes on without it, patched in place with its expense, and offers its total on the app network.
            await WaitForAsync(() => TextsIn(App(host)).ToList() is var shown && shown.Contains("ROWS 1") && !shown.Contains("TOTAL 5") ? "gone" : null, 60, "the part leaving the first program, and the rest still there");
            // The field of the part stays unused, so the running app keeps its expense. The window can show it before the chat says so.
            await WaitForTextAsync(host, "no such process", t => t.StartsWith("Hot reload (generation") && t.Contains("_total"), 10, "the note that the field of the part stays", api);
            var first = api.Prompts[1];
            first.Should().Contain("tore one part out").And.Contain("the Border named \"totalBox\"").And.Contain("\"TOTAL \"")
                .And.NotContain("TOTAL 5", "what the app holds goes to the model only as its shape");

            // A program of its own, where it was let go, with the first one's data.
            AutomationElement created;
            try
            {
                created = await WaitForAsync(() => NewProgram(host) is { } w && TextsIn(w).Any(t => t.StartsWith("NEW TOTAL ")) ? w : null, 90, "the program of its own");
            }
            catch (TimeoutException ex)
            {
                var neo = TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O"));
                var hosts = System.Diagnostics.Process.GetProcessesByName(Host).Where(p => TopLevelWindows.ParentProcessId(p.Id) == host.Id).Select(p => p.Id).ToList();
                var window = NewProgram(host);
                throw new TimeoutException(ex.Message + $" | N.E.O. says: {(neo == null ? "(no window)" : string.Join(" | ", TextsIn(neo).TakeLast(12)))}" +
                    $" | files: {string.Join(", ", Directory.Exists(_programs) ? Directory.GetFiles(_programs) : [])} | new hosts: {string.Join(", ", hosts)}" +
                    $" | its window: {(window == null ? "none" : string.Join(" | ", TextsIn(window)))} | {api.Requests.Count} request(s)" +
                    $" | the second try was asked: {(api.Prompts.Count > 3 ? api.Prompts[3][Math.Max(0, api.Prompts[3].Length - 2500)..] : "(none)")}", ex);
            }
            TextsIn(created).Should().Contain("NEW TOTAL 5", "it gets the data the first program held before the tear");
            api.Prompts[2].Should().Contain("Write the program \"Total box\"").And.Contain("\"TOTAL \"").And.NotContain("TOTAL 5");
            var system = System.Text.Json.Nodes.JsonNode.Parse(api.Requests[2])!["system"]!.ToJsonString();
            system.Should().Contain("Expenses").And.Contain("Total", "it knows what the first program offers now");
            GetWindowRect((IntPtr)created.Current.NativeWindowHandle, out var placed);
            placed.L.Should().BeInRange(700, 1000, "it opens where the part was let go");
            Directory.GetFiles(_programs, "*.resx").Should().ContainSingle().Which.Should().EndWith("Total box.resx");

            // A change in the first program shows in the second at once.
            var inFirst = App(host);
            var box = inFirst.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>().First();
            ((ValuePattern)box.GetCurrentPattern(ValuePattern.Pattern)).SetValue("12.5");
            TopLevelWindows.Click(inFirst, inFirst.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                .Cast<AutomationElement>().First(b => b.Current.Name == "Add"));
            await WaitForAsync(() => NewProgram(host) is { } w && TextsIn(w).Contains("NEW TOTAL 17.5") ? "shown" : null, 10, "the total in the program of its own");
        }
        finally
        {
            Stop(host);
        }
    }

    // Alt for the drag, only while the app's window is in front: it must reach no other window.
    private static void AltDown(AutomationElement window)
    {
        TopLevelWindows.Foreground(window);
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        if (pid != window.Current.ProcessId) throw new InvalidOperationException("The app's window is not in front; not pressing Alt.");
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
    }

    private static void AltUp() => keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NEO_APP_NETWORK_DIR", null);
        Environment.SetEnvironmentVariable(AppController.FissionFolderVariable, null);
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
