using System.Runtime.InteropServices;
using System.Windows.Automation;
using FluentAssertions;
using Neo.App;
using Newtonsoft.Json;
using Xunit;
using static Neo.PluginWindow.SmokeTests.RealHost;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// One program dragged onto another, with two real Avalonia hosts and a real drag of the mouse. The model
/// is a stand-in: what it would merge is given, and the test looks at what it was told.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class FusionSmokeTests : IDisposable
{
    private const string Host = "Neo.App.Avalonia";

    private readonly string _settingsA = Path.Combine(Path.GetTempPath(), "neo_fusion_a_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _settingsB = Path.Combine(Path.GetTempPath(), "neo_fusion_b_" + Guid.NewGuid().ToString("N")[..8]);

    private const string Expenses = """
        using System.Collections.Generic;
        using Avalonia.Controls;
        using Neo.App;

        [AppInfo("Expenses")]
        public class DynamicUserControl : UserControl
        {
            private readonly List<string> _rows = new();
            private string _label = "EXPENSES";
            private string _shared = "OWN";

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                var add = new Button { Content = "Add expense" };
                add.Click += (_, _) => { _rows.Add("Hotel"); BuildUi(); };
                var panel = new StackPanel { Margin = new Avalonia.Thickness(20) };
                panel.Children.Add(new TextBlock { Text = _label + " " + _rows.Count });
                panel.Children.Add(add);
                Content = panel;
            }
        }
        """;

    private const string Currency = """
        using Avalonia.Controls;
        using Neo.App;

        [AppInfo("Currency")]
        public class DynamicUserControl : UserControl
        {
            private double _rate = 1;
            private string _label = "RATE";
            private string _shared = "OTHER";

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                var set = new Button { Content = "Set rate" };
                set.Click += (_, _) => { _rate = 2.5; BuildUi(); };
                var panel = new StackPanel { Margin = new Avalonia.Thickness(20) };
                panel.Children.Add(new TextBlock { Text = _label + " " + (int)(_rate * 10) });
                panel.Children.Add(set);
                Content = panel;
            }
        }
        """;

    private const string CurrencyNext = """
        using Avalonia.Controls;
        using Neo.App;

        [AppInfo("Currency")]
        public class DynamicUserControl : UserControl
        {
            private double _rate = 1;
            private string _label = "RATE";
            private string _shared = "OTHER";

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                var set = new Button { Content = "Set rate" };
                set.Click += (_, _) => { _rate = 2.5; BuildUi(); };
                var panel = new StackPanel { Margin = new Avalonia.Thickness(20) };
                panel.Children.Add(new TextBlock { Text = _label + " " + (int)(_rate * 10) });
                panel.Children.Add(new TextBlock { Text = "VERSION 2" });
                panel.Children.Add(set);
                Content = panel;
            }
        }
        """;

    private const string Merged = """
        using System.Collections.Generic;
        using Avalonia.Controls;
        using Neo.App;

        [AppInfo("Expenses")]
        public class DynamicUserControl : UserControl
        {
            private readonly List<string> _rows = new();
            private string _label = "EXPENSES";
            private string _shared = "OWN";
            private double _rate = 1;
            private string currency__label = "not moved";

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                var add = new Button { Content = "Add expense" };
                add.Click += (_, _) => { _rows.Add("Hotel"); BuildUi(); };
                var set = new Button { Content = "Set rate" };
                set.Click += (_, _) => { _rate = 2.5; BuildUi(); };
                var panel = new StackPanel { Margin = new Avalonia.Thickness(20) };
                panel.Children.Add(new TextBlock { Text = _label + " " + _rows.Count });
                panel.Children.Add(new TextBlock { Text = "SHARED " + _shared });
                panel.Children.Add(add);
                panel.Children.Add(new TextBlock { Text = currency__label + " " + (int)(_rate * 10) });
                panel.Children.Add(set);
                Content = panel;
            }
        }
        """;

    // A new interface cannot be patched into the running app: it is replaced, and what it held has to survive that.
    private static readonly string MergedWithAnInterface = Merged
        .Replace("public class DynamicUserControl : UserControl", "public class DynamicUserControl : UserControl, System.IDisposable")
        .Replace("private void BuildUi()", "public void Dispose() { }\n\n    private void BuildUi()");

    private static AutomationElement AppOf(System.Diagnostics.Process host) =>
        System.Diagnostics.Process.GetProcessesByName(ChildProcessName(Host))
            .Where(p => TopLevelWindows.ParentProcessId(p.Id) == host.Id)
            .Select(p => TopLevelWindows.Find(w => w.Pid == p.Id))
            .First(w => w != null)!;

    private static AutomationElement ButtonIn(AutomationElement window, string name) =>
        window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().First(b => b.Current.Name == name);

    private static object Settings => new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true };

    private async Task<(AutomationElement A, AutomationElement B)> TwoAppsSideBySideAsync(System.Diagnostics.Process hostA, System.Diagnostics.Process hostB, StandInClaudeApi apiA, StandInClaudeApi apiB)
    {
        await SendPromptAsync(hostA, "Build it.");
        await WaitForTextAsync(hostA, ChildProcessName(Host), t => t == "EXPENSES 0", 60, "the first app", apiA);
        await SendPromptAsync(hostB, "Build it.");
        await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "RATE 10", 60, "the second app", apiB);
        await Task.Delay(3000); // hot reload arms itself in the background

        // Both N.E.O.s out of the way, or one of them lies over the app someone wants to reach.
        foreach (var host in new[] { hostA, hostB })
            MoveWindow((IntPtr)TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O"))!.Current.NativeWindowHandle, 650, 560, 640, 440, true);
        await Task.Delay(400);

        var a = AppOf(hostA);
        var b = AppOf(hostB);
        MoveWindow((IntPtr)a.Current.NativeWindowHandle, 100, 120, 500, 400, true);
        MoveWindow((IntPtr)b.Current.NativeWindowHandle, 760, 120, 500, 400, true);
        await Task.Delay(400);

        TopLevelWindows.Click(a, ButtonIn(a, "Add expense"));
        await WaitForTextAsync(hostA, ChildProcessName(Host), t => t == "EXPENSES 1", 10, "an expense in the first app", apiA);
        TopLevelWindows.Click(b, ButtonIn(b, "Set rate"));
        await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "RATE 25", 10, "the rate in the second app", apiB);
        TopLevelWindows.Foreground(a);
        TopLevelWindows.Foreground(b);
        return (a, b);
    }

    // Places on a window are asked of the window itself: what UI Automation reports of an Avalonia window is its client area.
    private const int HTCAPTION = 2, HTLEFT = 10;

    private static (int X, int Y) TitleBarOf(AutomationElement window) => Checked(window, HTCAPTION, r => ((r.L + r.R) / 2, r.T + 14));

    private static (int X, int Y) LeftEdgeOf(AutomationElement window) => Checked(window, HTLEFT, r => (r.L + 3, (r.T + r.B) / 2));

    private static (int X, int Y) Checked(AutomationElement window, int expected, Func<Rect, (int X, int Y)> where)
    {
        var handle = (IntPtr)window.Current.NativeWindowHandle;
        GetWindowRect(handle, out var r);
        var point = where(r);
        var hit = SendMessage(handle, 0x0084 /* WM_NCHITTEST */, IntPtr.Zero, (IntPtr)((point.Y << 16) | (point.X & 0xffff)));
        if (hit != expected) throw new InvalidOperationException($"{point} is not where it should be (hit test {hit}, expected {expected}); not dragging.");
        return point;
    }

    private static (int X, int Y) MiddleOf(AutomationElement window)
    {
        var r = window.Current.BoundingRectangle;
        return ((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
    }

    [Fact]
    public async Task A_program_dragged_onto_another_becomes_a_part_of_it_with_its_data()
    {
        using var apiA = new StandInClaudeApi();
        using var apiB = new StandInClaudeApi();
        apiA.AnswerWithCode(Expenses);
        apiA.AnswerWithCode(Merged);
        apiB.AnswerWithCode(Currency);

        using var hostA = Start(Host, apiA, _settingsA, Settings);
        using var hostB = Start(Host, apiB, _settingsB, Settings);
        try
        {
            var (a, b) = await TwoAppsSideBySideAsync(hostA, hostB, apiA, apiB);
            var windowB = (IntPtr)b.Current.NativeWindowHandle;
            var processA = a.Current.ProcessId;
            GetWindowRect((IntPtr)a.Current.NativeWindowHandle, out var before);
            apiA.HoldAnswers();

            string? hint = null;
            var from = TitleBarOf(b);
            var to = MiddleOf(a);
            await TopLevelWindows.DragAsync(b, from.X, from.Y, to.X, to.Y, async () =>
            {
                hint = await WaitForAsync(() => TextsIn(AppOf(hostA)).FirstOrDefault(t => t.StartsWith("Drop to merge")), 5, "the other app saying that a drop would merge");
            });
            hint.Should().Be("Drop to merge Currency into this app");

            await WaitForAsync(() => IsWindowVisible(windowB) ? null : "hidden", 5, "the dropped window going away");

            // While the model writes, the app stays in use, and what is done now is not lost either.
            await WaitForTextAsync(hostA, ChildProcessName(Host), t => t.StartsWith("Merging Currency"), 10, "the line over the app", apiA);
            var inUse = AppOf(hostA);
            TopLevelWindows.Click(inUse, ButtonIn(inUse, "Add expense"));
            await WaitForTextAsync(hostA, ChildProcessName(Host), t => t == "EXPENSES 2", 10, "an expense added during the merge", apiA);
            apiA.ReleaseAnswers();

            // The app it was dropped on has both now, and neither lost anything. It is the same app.
            await WaitForTextAsync(hostA, ChildProcessName(Host), t => t == "RATE 25", 60, "the data of the dropped program", apiA);
            var texts = TextsIn(AppOf(hostA)).ToList();
            texts.Should().Contain("EXPENSES 2", "what this app held stays").And.NotContain(t => t.StartsWith("Drop to merge"));
            texts.Should().Contain("SHARED OWN", "a value of the other program does not take the place of this app's own");
            AppOf(hostA).Current.ProcessId.Should().Be(processA);
            await WaitForTextAsync(hostA, ChildProcessName(Host), t => t.Contains("is a part of this app now"), 5, "the green line over the app", apiA);
            await Task.Delay(300);
            GetWindowRect((IntPtr)AppOf(hostA).Current.NativeWindowHandle, out var after);
            (after.R - after.L).Should().BeGreaterThan(before.R - before.L, "it took the other one's room in");

            // What the model was told.
            apiA.Prompts.Should().HaveCount(2);
            apiA.Prompts[1].Should().Contain("Merge \"Currency\" into this app.");
            apiA.Prompts[1].Should().Contain("private double _rate = 1;", "the source of the other program");
            apiA.Prompts[1].Should().Contain("under its exact name");
            apiA.Requests.Count.Should().Be(2);
            apiB.Requests.Count.Should().Be(1, "the N.E.O. of the dropped program needs no model");

            // Both N.E.O.s say what happened.
            await WaitForTextAsync(hostA, "no such process", t => t.Contains("value(s) of Currency moved over"), 10, "the chat of the receiving N.E.O.", apiA);
            await WaitForTextAsync(hostB, "no such process", t => t.Contains("is a part of the other one now"), 10, "the chat of the other N.E.O.", apiB);
            apiA.Prompts.Should().HaveCount(2);

            // It goes on working as one program.
            var app = AppOf(hostA);
            TopLevelWindows.Click(app, ButtonIn(app, "Add expense"));
            await WaitForTextAsync(hostA, ChildProcessName(Host), t => t == "EXPENSES 3", 10, "the merged app still working", apiA);
            TextsIn(AppOf(hostA)).Should().Contain("RATE 25");

            // The dropped window stays away, also when its N.E.O. comes to the front, which brings a hidden app back otherwise.
            TopLevelWindows.Foreground(TopLevelWindows.Find(w => w.Pid == hostB.Id && w.Title.StartsWith("N.E.O"))!);
            await Task.Delay(1500);
            IsWindowVisible(windowB).Should().BeFalse("it is a part of the other app now");

            // A prompt in its N.E.O. is work on that app again: it comes back as it was.
            apiB.AnswerWithCode(CurrencyNext);
            await SendPromptAsync(hostB, "Go on with it.");
            await WaitForAsync(() => IsWindowVisible(windowB) ? "back" : null, 30, "the window coming back with the next prompt");
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "VERSION 2", 30, "the next version", apiB);
            TextsIn(AppOf(hostB)).Should().Contain("RATE 25");
        }
        finally
        {
            Stop(hostA);
            Stop(hostB);
        }
    }

    [Fact]
    public async Task When_the_merge_does_not_work_out_the_window_comes_back_and_nothing_is_lost()
    {
        using var apiA = new StandInClaudeApi();
        using var apiB = new StandInClaudeApi();
        apiA.AnswerWithCode(Expenses); // and nothing to merge with: the model fails
        apiB.AnswerWithCode(Currency);

        using var hostA = Start(Host, apiA, _settingsA, Settings);
        using var hostB = Start(Host, apiB, _settingsB, Settings);
        try
        {
            var (a, b) = await TwoAppsSideBySideAsync(hostA, hostB, apiA, apiB);
            var windowB = (IntPtr)b.Current.NativeWindowHandle;

            // Let go over nothing: nothing happens.
            var start = TitleBarOf(b);
            await TopLevelWindows.DragAsync(b, start.X, start.Y, start.X + 60, start.Y + 40);
            await Task.Delay(800);
            IsWindowVisible(windowB).Should().BeTrue();
            apiA.Requests.Count.Should().Be(1);

            // Escape while the other app says a drop would merge: the user changed their mind.
            GetWindowRect(windowB, out var beforeEscape);
            var grab = TitleBarOf(b);
            var there = MiddleOf(a);
            await TopLevelWindows.DragAsync(b, grab.X, grab.Y, there.X, there.Y, async () =>
            {
                await WaitForAsync(() => TextsIn(AppOf(hostA)).FirstOrDefault(t => t.StartsWith("Drop to merge")), 5, "the other app saying that a drop would merge");
                await Task.Delay(500); // long enough to count as a drop
                TopLevelWindows.Keys(b, "{ESC}");
            });
            await Task.Delay(800);
            IsWindowVisible(windowB).Should().BeTrue("nothing was dropped");
            GetWindowRect(windowB, out var afterEscape);
            afterEscape.L.Should().Be(beforeEscape.L, "Escape puts it back");
            apiA.Requests.Count.Should().Be(1);
            TextsIn(AppOf(hostA)).Should().NotContain(t => t.StartsWith("Drop to merge"));

            // Another program on the app network, which takes no merges: let go over it, and nothing happens.
            using (OpenWindow("Takes no merges", 100, 560, 400, 300))
            {
                AppNetwork.Publish(new object(), "Takes no merges", work => Task.FromResult(work()));
                try
                {
                    TopLevelWindows.Foreground(TopLevelWindows.Find(w => w.Title == "Takes no merges")!); // just below the dragged window, not behind whatever else is open
                    var title = TitleBarOf(b);
                    await TopLevelWindows.DragAsync(b, title.X, title.Y, 300, 700);
                    await Task.Delay(1200);
                    IsWindowVisible(windowB).Should().BeTrue("it did not go anywhere");
                    TextsIn(AppOf(hostB)).Should().NotContain(t => t.StartsWith("Not merged"));
                }
                finally
                {
                    AppNetwork.Unpublish();
                }
            }
            MoveWindow(windowB, 760, 120, 500, 400, true);
            await Task.Delay(300);

            // Dragging an edge across the other app is resizing.
            var edge = LeftEdgeOf(b);
            var across = MiddleOf(a);
            string? hintWhileResizing = null;
            await TopLevelWindows.DragAsync(b, edge.X, edge.Y, across.X, across.Y, async () =>
            {
                await Task.Delay(800);
                hintWhileResizing = TextsIn(AppOf(hostA)).FirstOrDefault(t => t.StartsWith("Drop to merge"));
            });
            await Task.Delay(800);
            hintWhileResizing.Should().BeNull();
            IsWindowVisible(windowB).Should().BeTrue("resizing is no drop");
            apiA.Requests.Count.Should().Be(1);

            var from = TitleBarOf(b);
            var to = MiddleOf(a);
            await TopLevelWindows.DragAsync(b, from.X, from.Y, to.X, to.Y, async () =>
            {
                await WaitForAsync(() => TextsIn(AppOf(hostA)).FirstOrDefault(t => t.StartsWith("Drop to merge")), 5, "the other app saying that a drop would merge");
            });

            await WaitForAsync(() => apiA.Requests.Count >= 2 ? "asked" : null, 30, "the model being asked");
            await WaitForAsync(() => IsWindowVisible(windowB) ? "back" : null, 60, "the dropped window coming back");

            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t.StartsWith("Not merged:"), 5, "the reason over the window", apiB);
            await WaitForTextAsync(hostA, ChildProcessName(Host), t => t.StartsWith("Could not merge Currency"), 5, "the reason over the other app", apiA);
            TextsIn(AppOf(hostB)).Should().Contain("RATE 25", "its data never left");
            TextsIn(AppOf(hostA)).Should().Contain("EXPENSES 1");

            // Showing it again must not have hurt it: the heartbeat goes on, and it still does what it did.
            await Task.Delay(2500);
            TextsIn(AppOf(hostB)).Should().Contain("RATE 25");
            IsWindowVisible(windowB).Should().BeTrue();
        }
        finally
        {
            Stop(hostA);
            Stop(hostB);
        }
    }

    [Fact]
    public async Task A_change_that_cannot_be_patched_in_replaces_the_app_and_both_keep_their_data()
    {
        using var apiA = new StandInClaudeApi();
        using var apiB = new StandInClaudeApi();
        apiA.AnswerWithCode(Expenses);
        apiA.AnswerWithCode(MergedWithAnInterface);
        apiB.AnswerWithCode(Currency);

        using var hostA = Start(Host, apiA, _settingsA, Settings);
        using var hostB = Start(Host, apiB, _settingsB, Settings);
        try
        {
            var (a, b) = await TwoAppsSideBySideAsync(hostA, hostB, apiA, apiB);

            var from = TitleBarOf(b);
            var to = MiddleOf(a);
            await TopLevelWindows.DragAsync(b, from.X, from.Y, to.X, to.Y, async () =>
            {
                await WaitForAsync(() => TextsIn(AppOf(hostA)).FirstOrDefault(t => t.StartsWith("Drop to merge")), 5, "the other app saying that a drop would merge");
            });

            await WaitForTextAsync(hostA, "no such process", t => t.StartsWith("Hot reload not possible"), 60, "the note that the app is replaced", apiA);
            await WaitForTextAsync(hostA, ChildProcessName(Host), t => t == "RATE 25", 30, "the data of the dropped program", apiA);
            await WaitForTextAsync(hostA, ChildProcessName(Host), t => t == "EXPENSES 1", 10, "what the app itself held", apiA);
            TextsIn(AppOf(hostA)).Should().Contain("SHARED OWN");
        }
        finally
        {
            Stop(hostA);
            Stop(hostB);
        }
    }

    [Fact]
    public async Task When_the_other_app_is_closed_during_the_merge_the_window_comes_back()
    {
        using var apiA = new StandInClaudeApi();
        using var apiB = new StandInClaudeApi();
        apiA.AnswerWithCode(Expenses);
        apiB.AnswerWithCode(Currency);

        using var hostA = Start(Host, apiA, _settingsA, Settings);
        using var hostB = Start(Host, apiB, _settingsB, Settings);
        try
        {
            var (a, b) = await TwoAppsSideBySideAsync(hostA, hostB, apiA, apiB);
            var windowB = (IntPtr)b.Current.NativeWindowHandle;
            apiA.HoldAnswers();

            var from = TitleBarOf(b);
            var to = MiddleOf(a);
            await TopLevelWindows.DragAsync(b, from.X, from.Y, to.X, to.Y, async () =>
            {
                await WaitForAsync(() => TextsIn(AppOf(hostA)).FirstOrDefault(t => t.StartsWith("Drop to merge")), 5, "the other app saying that a drop would merge");
            });
            await WaitForAsync(() => apiA.Requests.Count >= 2 ? "asked" : null, 30, "the model being asked");
            IsWindowVisible(windowB).Should().BeFalse();

            Stop(hostA);

            await WaitForAsync(() => IsWindowVisible(windowB) ? "back" : null, 15, "the dropped window coming back");
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t.StartsWith("Not merged:") && t.Contains("was closed"), 5, "the reason over the window", apiB);
            TextsIn(AppOf(hostB)).Should().Contain("RATE 25", "its data never left");
        }
        finally
        {
            apiA.ReleaseAnswers();
            Stop(hostA);
            Stop(hostB);
        }
    }

    [Fact]
    public async Task Two_living_apps_become_one_and_the_other_program_ends()
    {
        // What a person has after an export: two programs and no N.E.O. in sight. The stand-in has nothing for the first drop.
        Directory.CreateDirectory(_settingsA);
        Directory.CreateDirectory(_settingsB);
        var projectA = Path.Combine(_settingsA, "Expenses.resx");
        var projectB = Path.Combine(_settingsB, "Currency.resx");
        File.WriteAllText(projectA, JsonConvert.SerializeObject(new ResxData { Version = 2, Code = Expenses, Nuget = new(), History = "" }));
        File.WriteAllText(projectB, JsonConvert.SerializeObject(new ResxData { Version = 2, Code = Currency, Nuget = new(), History = "" }));

        using var apiA = new StandInClaudeApi();
        using var apiB = new StandInClaudeApi();
        using var hostA = StartLiving(apiA, _settingsA, projectA);
        using var hostB = StartLiving(apiB, _settingsB, projectB);
        try
        {
            var a = await WaitForAsync(() => AppOf(hostA) is { } w && TextsIn(w).Contains("EXPENSES 0") ? w : null, 90, "the first program");
            var b = await WaitForAsync(() => AppOf(hostB) is { } w && TextsIn(w).Contains("RATE 10") ? w : null, 90, "the second program");
            await Task.Delay(3000); // hot reload arms itself in the background
            var windowA = (IntPtr)a.Current.NativeWindowHandle;
            var windowB = (IntPtr)b.Current.NativeWindowHandle;
            MoveWindow(windowA, 100, 120, 500, 400, true);
            MoveWindow(windowB, 760, 120, 500, 400, true);
            await Task.Delay(400);

            TopLevelWindows.Click(a, ButtonIn(a, "Add expense"));
            await WaitForTextAsync(hostA, ChildProcessName(Host), t => t == "EXPENSES 1", 10, "an expense", apiA);
            TopLevelWindows.Click(b, ButtonIn(b, "Set rate"));
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "RATE 25", 10, "the rate", apiB);

            async Task DropAsync()
            {
                TopLevelWindows.Foreground(a);
                TopLevelWindows.Foreground(b);
                var from = TitleBarOf(b);
                var to = MiddleOf(a);
                await TopLevelWindows.DragAsync(b, from.X, from.Y, to.X, to.Y, async () =>
                {
                    await WaitForAsync(() => TextsIn(AppOf(hostA)).FirstOrDefault(t => t.StartsWith("Drop to merge")), 5, "the other app saying that a drop would merge");
                });
            }

            // It does not work out: the window comes back where it was, not on top of the other one.
            await DropAsync();
            await WaitForAsync(() => IsWindowVisible(windowB) ? null : "hidden", 5, "the dropped window going away");
            await WaitForAsync(() => IsWindowVisible(windowB) ? "back" : null, 60, "the dropped window coming back");
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t.StartsWith("Not merged:"), 5, "the reason over the window", apiB);
            GetWindowRect(windowB, out var cameBack);
            cameBack.L.Should().Be(760, "it goes back to where it was");
            cameBack.T.Should().Be(120);

            // Now it does: one program, grown where it stands, and the other one has ended.
            apiA.AnswerWithCode(Merged);
            GetWindowRect(windowA, out var before);
            await DropAsync();
            await WaitForTextAsync(hostA, ChildProcessName(Host), t => t == "RATE 25", 60, "the data of the dropped program", apiA);
            hostB.WaitForExit(15000).Should().BeTrue("a living app that became a part of another one is done");

            await WaitForTextAsync(hostA, ChildProcessName(Host), t => t.Contains("is a part of this app now"), 5, "the green line over the app", apiA);
            await Task.Delay(300);
            GetWindowRect(windowA, out var after);
            after.L.Should().Be(before.L, "it has room to grow where it stands");
            after.T.Should().Be(before.T);
            (after.R - after.L).Should().BeGreaterThan(before.R - before.L + 300, "it took in the other one's room");
            TextsIn(AppOf(hostA)).Should().Contain("EXPENSES 1");

            // The project on disk is the merged one now, so it still is after a restart. The other project stays as it was.
            await WaitForAsync(() => File.ReadAllText(projectA).Contains("currency__label") ? "saved" : null, 10, "the merged program saved");
            File.ReadAllText(projectB).Should().NotContain("currency__label");
        }
        finally
        {
            Stop(hostA);
            Stop(hostB);
        }
    }

    private static System.Diagnostics.Process StartLiving(StandInClaudeApi api, string folder, string project)
    {
        File.WriteAllText(Path.Combine(folder, "settings.json"), System.Text.Json.JsonSerializer.Serialize(Settings));
        var exe = TestEnvironment.HostExe(Host);
        var startInfo = new System.Diagnostics.ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false, Arguments = $"--play \"{project}\"" };
        startInfo.Environment[SettingsService.FolderEnvironmentVariable] = folder;
        startInfo.Environment["NEO_ANTHROPIC_BASE_URL"] = api.BaseUrl;
        startInfo.Environment["ANTHROPIC_API_KEY"] = "not-a-real-key";
        startInfo.Environment.Remove("OPENAI_API_KEY");
        startInfo.Environment.Remove("GEMINI_API_KEY");
        return System.Diagnostics.Process.Start(startInfo)!;
    }

    // A plain window on its own thread, for a program that is no N.E.O. app. Gone with Dispose.
    private static IDisposable OpenWindow(string title, int x, int y, int width, int height)
    {
        System.Windows.Forms.Form form = null!;
        using var shown = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            form = new System.Windows.Forms.Form { Text = title, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Bounds = new System.Drawing.Rectangle(x, y, width, height), ShowInTaskbar = false };
            form.Shown += (_, _) => shown.Set();
            System.Windows.Forms.Application.Run(form);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        shown.Wait(TimeSpan.FromSeconds(10));
        return new Closing(() => form.Invoke(form.Close));
    }

    private sealed class Closing(Action close) : IDisposable
    {
        public void Dispose() { try { close(); } catch { } }
    }

    public void Dispose()
    {
        try { Directory.Delete(_settingsA, recursive: true); } catch { }
        try { Directory.Delete(_settingsB, recursive: true); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
}
