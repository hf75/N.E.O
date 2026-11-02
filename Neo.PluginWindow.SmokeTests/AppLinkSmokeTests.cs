using System.Runtime.InteropServices;
using System.Windows.Automation;
using FluentAssertions;
using Neo.App;
using Newtonsoft.Json;
using Xunit;
using static Neo.PluginWindow.SmokeTests.RealHost;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// Two running programs linked by a real drag, with two real Avalonia hosts and a real communicator, compiled
/// and run. The model is a stand-in: what it suggests is given, and the test looks at what it was told.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class AppLinkSmokeTests : IDisposable
{
    private const string Host = "Neo.App.Avalonia";

    private readonly string _settingsA = Path.Combine(Path.GetTempPath(), "neo_link_a_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _settingsB = Path.Combine(Path.GetTempPath(), "neo_link_b_" + Guid.NewGuid().ToString("N")[..8]);

    private const string Meter = """
        using Avalonia.Controls;
        using Neo.App;

        [AppInfo("Meter")]
        public class DynamicUserControl : UserControl
        {
            private int _bumps;
            private string _heard = "nothing";
            private readonly TextBlock _shown = new TextBlock();

            public DynamicUserControl()
            {
                var slider = new Slider { Minimum = 0, Maximum = 100, Value = 10 };
                var bump = new Button { Content = "Bump" };
                bump.Click += (_, _) => { _bumps++; Show(); };
                var panel = new StackPanel { Margin = new Avalonia.Thickness(20, 50, 20, 20) };
                panel.Children.Add(_shown);
                panel.Children.Add(slider);
                panel.Children.Add(bump);
                Content = panel;
                Show();
            }

            private void Show() => _shown.Text = "METER " + _bumps + " HEARD " + _heard;
        }
        """;

    private const string Board = """
        using System.Collections.Generic;
        using Avalonia.Controls;
        using Neo.App;

        [AppInfo("Board")]
        public class DynamicUserControl : UserControl
        {
            private readonly List<string> _rows = new();
            private int _count;
            private readonly TextBox _amount = new TextBox();
            private readonly TextBox _note = new TextBox();
            private readonly TextBlock _shown = new TextBlock();
            private readonly TextBlock _amountShown = new TextBlock { Text = "AMOUNT none" };
            private readonly TextBlock _noteShown = new TextBlock { Text = "NOTE none" };

            public DynamicUserControl()
            {
                _amount.TextChanged += (_, _) => _amountShown.Text = "AMOUNT " + _amount.Text;
                _note.TextChanged += (_, _) => _noteShown.Text = "NOTE " + _note.Text;
                var store = new Button { Content = "Store" };
                store.Click += (_, _) => { _rows.Add(_amount.Text ?? ""); _count = _rows.Count; Show(); };
                var panel = new StackPanel { Margin = new Avalonia.Thickness(20, 50, 20, 20) };
                panel.Children.Add(_shown);
                panel.Children.Add(_amountShown);
                panel.Children.Add(_noteShown);
                panel.Children.Add(_amount);
                panel.Children.Add(_note);
                panel.Children.Add(new TextBlock { Text = "AFTER THE BOXES" });
                panel.Children.Add(store);
                Content = panel;
                Show();
            }

            private void Show() => _shown.Text = "ROWS " + _rows.Count + " LAST " + (_rows.Count > 0 ? _rows[_rows.Count - 1] : "none");
        }
        """;

    private const string ValueTitle = "The meter fills in the amount";
    private const string BumpTitle = "Bump also stores the amount";
    private const string HeardTitle = "The meter hears the number of rows";
    private const string UnchosenTitle = "A note nobody wants";

    private static string Proposal(string note = "") => JsonConvert.SerializeObject(new
    {
        blocks = new[]
        {
            new { title = ValueTitle, detail = "The slider of Meter goes into the amount of Board.", code = "a.OnChange<double>(\"Slider:0\", \"Value\", v => b.Set(\"TextBox:0\", \"Text\", v.ToString(\"0\", CultureInfo.InvariantCulture)));" },
            new { title = BumpTitle, detail = "Pressing Bump presses Store.", code = "a.OnClick(\"Button:0\", () => b.Press(\"Button:0\"));" },
            new { title = HeardTitle, detail = "Meter shows how many rows Board has.", code = "b.OnField<int>(\"_count\", n => { a.SetField(\"_heard\", n.ToString(CultureInfo.InvariantCulture)); a.Call(\"Show\"); });" },
            new { title = UnchosenTitle, detail = "Would write into the note.", code = "b.Set(\"TextBox:1\", \"Text\", \"CHOSEN\");" },
        },
        note,
    });

    private static object Settings => new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true };

    private static AutomationElement AppOf(System.Diagnostics.Process host) =>
        System.Diagnostics.Process.GetProcessesByName(ChildProcessName(Host))
            .Where(p => TopLevelWindows.ParentProcessId(p.Id) == host.Id)
            .Select(p => TopLevelWindows.Find(w => w.Pid == p.Id))
            .First(w => w != null)!;

    private static AutomationElement ButtonIn(AutomationElement window, string name) =>
        window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().First(b => b.Current.Name == name);

    private static AutomationElement SliderIn(AutomationElement window) =>
        window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider))
            .Cast<AutomationElement>().First();

    private static void SetSlider(AutomationElement window, double value) =>
        ((RangeValuePattern)SliderIn(window).GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(value);

    private static async Task<AutomationElement> DialogOfAsync(System.Diagnostics.Process host)
    {
        try
        {
            return await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("Link ")), 20, "the dialog of the link");
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(ex.Message + " " + WhatTheProgramsShow(), ex);
        }
    }

    // A drop that went wrong leaves no trace otherwise: it went elsewhere, or it merged, or one of them said why not.
    private static string WhatTheProgramsShow() => string.Join(" // ",
        System.Diagnostics.Process.GetProcessesByName(ChildProcessName(Host)).Select(p => TopLevelWindows.Find(w => w.Pid == p.Id)).Where(w => w != null).Select(w =>
        {
            GetWindowRect((IntPtr)w!.Current.NativeWindowHandle, out var r);
            return $"{r.L},{r.T} to {r.R},{r.B}: {string.Join(" | ", TextsIn(w).TakeLast(8))}";
        }));

    private static (int X, int Y) TitleBarOf(AutomationElement window)
    {
        var handle = (IntPtr)window.Current.NativeWindowHandle;
        GetWindowRect(handle, out var r);
        var point = ((r.L + r.R) / 2, r.T + 14);
        var hit = SendMessage(handle, 0x0084 /* WM_NCHITTEST */, IntPtr.Zero, (IntPtr)((point.Item2 << 16) | (point.Item1 & 0xffff)));
        if (hit != 2) throw new InvalidOperationException($"{point} is not on the title bar (hit test {hit}); not dragging.");
        return point;
    }

    private async Task<(AutomationElement A, AutomationElement B)> TwoProgramsAsync(System.Diagnostics.Process hostA, System.Diagnostics.Process hostB, StandInClaudeApi apiA, StandInClaudeApi apiB, string readyA = "METER 0", string readyB = "ROWS 0")
    {
        await SendPromptAsync(hostA, "Build it.");
        await WaitForTextAsync(hostA, ChildProcessName(Host), t => t.StartsWith(readyA), 60, "the first program", apiA);
        await SendPromptAsync(hostB, "Build it.");
        await WaitForTextAsync(hostB, ChildProcessName(Host), t => t.StartsWith(readyB), 60, "the second program", apiB);
        await Task.Delay(3000); // hot reload arms itself in the background, and a N.E.O. docks its app once more after a prompt

        // Both N.E.O.s out of the way, or one of them lies over the app someone wants to reach.
        foreach (var host in new[] { hostA, hostB })
            MoveWindow((IntPtr)TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O"))!.Current.NativeWindowHandle, 650, 600, 640, 400, true);
        await Task.Delay(400);

        return Placed(AppOf(hostA), AppOf(hostB));
    }

    private static (AutomationElement A, AutomationElement B) Placed(AutomationElement a, AutomationElement b)
    {
        MoveWindow((IntPtr)a.Current.NativeWindowHandle, 100, 120, 500, 420, true);
        MoveWindow((IntPtr)b.Current.NativeWindowHandle, 760, 120, 500, 420, true);
        TopLevelWindows.Foreground(b);
        TopLevelWindows.Foreground(a);
        return (a, b);
    }

    // On the right half of the other program a drop links.
    private static async Task DropOnLinkHalfAsync(AutomationElement dragged, AutomationElement target, System.Diagnostics.Process targetHost)
    {
        TopLevelWindows.Foreground(target);
        TopLevelWindows.Foreground(dragged);
        await Task.Delay(300);
        var from = TitleBarOf(dragged);
        GetWindowRect((IntPtr)target.Current.NativeWindowHandle, out var r);
        await TopLevelWindows.DragAsync(dragged, from.X, from.Y, r.L + (r.R - r.L) * 3 / 4, r.T + 150, async () =>
        {
            await WaitForAsync(() => TextsIn(AppOf(targetHost)).FirstOrDefault(t => t.StartsWith("Drop to link")), 5, "the other program saying that a drop would link");
        });
    }

    private static async Task ConnectAsync(System.Diagnostics.Process host, params string[] leaveOut)
    {
        var dialog = await DialogOfAsync(host);
        await WaitForAsync(() => ButtonIn(dialog, "Connect").Current.IsEnabled ? "ready" : null, 30, "the suggestions");
        foreach (var title in leaveOut)
        {
            var check = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox))
                .Cast<AutomationElement>().First(c => c.Current.Name == title);
            TopLevelWindows.Click(dialog, check);
            await Task.Delay(150);
        }
        TopLevelWindows.Click(dialog, ButtonIn(dialog, "Connect"));
    }

    [Fact]
    public async Task Two_running_programs_are_linked_by_a_drop_and_neither_changes()
    {
        using var apiA = new StandInClaudeApi();
        using var apiB = new StandInClaudeApi();
        apiA.AnswerWithCode(Meter);
        apiB.AnswerWithCode(Board);
        apiB.AnswerWithText(Proposal());

        using var hostA = Start(Host, apiA, _settingsA, Settings);
        using var hostB = Start(Host, apiB, _settingsB, Settings);
        try
        {
            var (a, b) = await TwoProgramsAsync(hostA, hostB, apiA, apiB);
            var windowA = (IntPtr)a.Current.NativeWindowHandle;
            var processA = a.Current.ProcessId;
            var processB = b.Current.ProcessId;
            GetWindowRect(windowA, out var before);

            // What someone entered, and then stored in a list: the model does not get to read that.
            var amountBox = b.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>().First();
            ((ValuePattern)amountBox.GetCurrentPattern(ValuePattern.Pattern)).SetValue("MySecret 7713");
            TopLevelWindows.Click(b, ButtonIn(b, "Store"));
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "ROWS 1 LAST MySecret 7713", 5, "something stored", apiB);

            await DropOnLinkHalfAsync(a, b, hostB);

            // Both stay, each where it was.
            var dialog = await DialogOfAsync(hostB);
            IsWindowVisible(windowA).Should().BeTrue("a link keeps both programs");
            await WaitForAsync(() => { GetWindowRect(windowA, out var now); return now.L == before.L && now.T == before.T ? "back" : null; }, 5, "the dragged window back where it was");

            // The model read both as they are running now.
            await WaitForAsync(() => ButtonIn(dialog, "Connect").Current.IsEnabled ? "ready" : null, 30, "the suggestions");
            var question = apiB.Prompts[^1];
            question.Should().Contain("Slider:0 Value=10 from 0 to 100", "the first program as it runs");
            question.Should().Contain("Button:0 \"Bump\"", "what the program put there, not the parts a slider is made of");
            question.Should().Contain("_count (Int32)", "the second program as it runs");
            question.Should().Contain("TextBlock:3 Text=\"AFTER THE BOXES\"", "a text box is made of text blocks too, and they do not count");
            question.Should().NotContain("MySecret").And.NotContain("7713", "what someone entered stays on this PC");
            question.Should().Contain("TextBlock:0 Text=\"XXXX 9 XXXX XxXxxxxx 9999\"", "of what is not in the source, its shape: the model can read a format from that");
            question.Should().Contain("TextBox:0 Text=\"XxXxxxxx 9999\" beside=\"NOTE none\"");
            question.Should().Contain("private string _heard").And.Contain("private readonly List<string> _rows", "the source of both");
            apiB.Requests[^1].Should().Contain("\"model\":\"stand-in\"", "the model of this N.E.O.");
            apiB.Requests[^1].Should().Contain("small piece of code").And.Contain("\"blocks\"", "what it is told, and how it answers");
            apiA.Requests.Count.Should().Be(1, "the N.E.O. of the dragged program needs no model");
            TextsIn(dialog).Should().Contain(ValueTitle).And.Contain(UnchosenTitle);

            await ConnectAsync(hostB, UnchosenTitle);

            // Both say so, and a value is there at once.
            await WaitForTextAsync(hostA, ChildProcessName(Host), t => t == "Linked to Board", 10, "the chip on the first program", apiA);
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "Linked to Meter", 10, "the chip on the second program", apiB);
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "AMOUNT 10", 10, "the first value", apiB);

            // Data goes over, as if someone had typed it.
            SetSlider(AppOf(hostA), 42);
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "AMOUNT 42", 5, "the new value", apiB);

            // A press goes over, after the value before it. What the other program does in return comes back.
            var inA = AppOf(hostA);
            TopLevelWindows.Click(inA, ButtonIn(inA, "Bump"));
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "ROWS 2 LAST 42", 5, "the press in the other program", apiB);
            await WaitForTextAsync(hostA, ChildProcessName(Host), t => t == "METER 1 HEARD 2", 5, "the answer of the other program", apiA);

            // What was not chosen does not happen.
            TextsIn(AppOf(hostB)).Should().Contain("NOTE none");

            // Neither program changed: same processes, no model asked again, nothing patched.
            AppOf(hostA).Current.ProcessId.Should().Be(processA);
            AppOf(hostB).Current.ProcessId.Should().Be(processB);
            apiA.Requests.Count.Should().Be(1);
            apiB.Requests.Count.Should().Be(2);
            await WaitForTextAsync(hostB, "no such process", t => t.StartsWith("Linked to Meter:"), 5, "the chat of the N.E.O. it was dropped on", apiB);
            TextsIn(TopLevelWindows.Find(w => w.Pid == hostB.Id && w.Title.StartsWith("N.E.O"))!).Should().NotContain(t => t.Contains("Hot reload (generation 2)"));

            // A cross on either side ends it on both.
            var cross = AppOf(hostA);
            TopLevelWindows.Click(cross, ButtonIn(cross, "✕"));
            await WaitForAsync(() => TextsIn(AppOf(hostA)).Contains("Linked to Board") ? null : "gone", 5, "the chip on the first program going");
            await WaitForAsync(() => TextsIn(AppOf(hostB)).Contains("Linked to Meter") ? null : "gone", 5, "the chip on the second program going");

            SetSlider(AppOf(hostA), 77);
            await Task.Delay(500);
            TextsIn(AppOf(hostB)).Should().Contain("AMOUNT 42", "nothing goes over any more");
        }
        finally
        {
            Stop(hostA);
            Stop(hostB);
        }
    }

    [Fact]
    public async Task A_wish_in_the_persons_words_goes_to_the_model_and_a_proposal_can_be_declined()
    {
        using var apiA = new StandInClaudeApi();
        using var apiB = new StandInClaudeApi();
        apiA.AnswerWithCode(Meter);
        apiB.AnswerWithCode(Board);
        apiB.AnswerWithText("""{"blocks":[],"note":"These two have nothing to give each other."}""");
        apiB.AnswerWithText(JsonConvert.SerializeObject(new { blocks = new[] { new { title = "Only the wish", detail = "As asked.", code = "b.Set(\"TextBox:1\", \"Text\", \"WISHED\");" } }, note = "" }));

        using var hostA = Start(Host, apiA, _settingsA, Settings);
        using var hostB = Start(Host, apiB, _settingsB, Settings);
        try
        {
            var (a, b) = await TwoProgramsAsync(hostA, hostB, apiA, apiB);
            await DropOnLinkHalfAsync(a, b, hostB);

            // Nothing to connect, and it says why.
            var dialog = await DialogOfAsync(hostB);
            await WaitForAsync(() => TextsIn(dialog).Contains("These two have nothing to give each other.") ? "said" : null, 30, "the note of the model");
            ButtonIn(dialog, "Connect").Current.IsEnabled.Should().BeFalse("there is nothing to connect");

            // One link being set up is enough.
            await DropOnLinkHalfAsync(AppOf(hostA), AppOf(hostB), hostB);
            await WaitForTextAsync(hostA, ChildProcessName(Host), t => t.StartsWith("Not linked:") && t.Contains("setting up a link"), 10, "the other program saying it is busy with that", apiA);
            TopLevelWindows.Foreground(dialog);

            // The person says what they want.
            var wish = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                .Cast<AutomationElement>().First();
            ((ValuePattern)wish.GetCurrentPattern(ValuePattern.Pattern)).SetValue("Write WISHED into the note.");
            await WaitForAsync(() => ButtonIn(dialog, "Ask again").Current.IsEnabled ? "ready" : null, 5, "the button to ask again");
            TopLevelWindows.Click(dialog, ButtonIn(dialog, "Ask again"));
            await WaitForAsync(() => TextsIn(dialog).Contains("Only the wish") ? "there" : null, 30, "the new suggestion");
            apiB.Prompts[^1].Should().Contain("Write WISHED into the note.");
            apiB.Prompts[^1].Should().Contain("These two have nothing to give each other.", "what was said before");

            // Declined: no link, and the programs never heard of it.
            TopLevelWindows.Click(dialog, ButtonIn(dialog, "Cancel"));
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "Not linked.", 5, "the line over the program", apiB);
            TextsIn(AppOf(hostB)).Should().Contain("NOTE none").And.NotContain("Linked to Meter");
            TextsIn(AppOf(hostA)).Should().NotContain("Linked to Board");
        }
        finally
        {
            Stop(hostA);
            Stop(hostB);
        }
    }

    [Fact]
    public async Task Code_that_does_not_compile_goes_back_to_the_model_and_what_stays_wrong_stays_out()
    {
        using var apiA = new StandInClaudeApi();
        using var apiB = new StandInClaudeApi();
        apiA.AnswerWithCode(Meter);
        apiB.AnswerWithCode(Board);
        var good = new { title = "This one works", detail = "It compiles.", code = "b.Set(\"TextBox:1\", \"Text\", \"GOOD\");" };
        var bad = new { title = "This one never compiles", detail = "The communicator cannot touch the UI itself.", code = "var button = new Avalonia.Controls.Button();" };
        apiB.AnswerWithText(JsonConvert.SerializeObject(new { blocks = new[] { good, bad }, note = "" }));
        apiB.AnswerWithText(JsonConvert.SerializeObject(new { blocks = new[] { good, bad }, note = "" }));

        using var hostA = Start(Host, apiA, _settingsA, Settings);
        using var hostB = Start(Host, apiB, _settingsB, Settings);
        try
        {
            var (a, b) = await TwoProgramsAsync(hostA, hostB, apiA, apiB);
            await DropOnLinkHalfAsync(a, b, hostB);

            var dialog = await DialogOfAsync(hostB);
            await WaitForAsync(() => ButtonIn(dialog, "Connect").Current.IsEnabled ? "ready" : null, 30, "the suggestions");
            apiB.Prompts[^1].Should().Contain("The code does not compile", "the model got its second try").And.Contain("Avalonia.Controls.Button");
            TextsIn(dialog).Should().Contain("This one works").And.NotContain("This one never compiles");
            TextsIn(dialog).Should().Contain(t => t.Contains("could not be compiled"));

            await ConnectAsync(hostB);
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "NOTE GOOD", 10, "the block that compiled", apiB);

            // It is not kept anywhere.
            Directory.GetFiles(Path.GetTempPath(), "neo_link_*.dll").Should().BeEmpty("the communicator lives in memory");
        }
        finally
        {
            Stop(hostA);
            Stop(hostB);
        }
    }

    [Fact]
    public async Task What_a_block_gets_wrong_at_runtime_is_said_and_the_rest_works()
    {
        using var apiA = new StandInClaudeApi();
        using var apiB = new StandInClaudeApi();
        apiA.AnswerWithCode(Meter);
        apiB.AnswerWithCode(Board);
        apiB.AnswerWithText(JsonConvert.SerializeObject(new
        {
            blocks = new[]
            {
                new { title = "No such slider", detail = "", code = "a.OnChange<double>(\"Slider:9\", \"Value\", v => b.Press(\"Button:0\"));" },
                new { title = "No such button here", detail = "", code = "b.OnClick(\"Button:9\", () => { });" },
                new { title = "This one works", detail = "", code = "a.OnChange<double>(\"Slider:0\", \"Value\", v => b.Set(\"TextBox:0\", \"Text\", v.ToString(\"0\", CultureInfo.InvariantCulture)));" },
            },
            note = "",
        }));

        using var hostA = Start(Host, apiA, _settingsA, Settings);
        using var hostB = Start(Host, apiB, _settingsB, Settings);
        try
        {
            var (a, b) = await TwoProgramsAsync(hostA, hostB, apiA, apiB);
            await DropOnLinkHalfAsync(a, b, hostB);
            await ConnectAsync(hostB);

            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "AMOUNT 10", 10, "the block that works", apiB);
            await WaitForTextAsync(hostB, "no such process", t => t.Contains("There is no control Slider:9"), 5, "what is wrong with the other program", apiB);
            await WaitForTextAsync(hostB, "no such process", t => t.Contains("There is no control Button:9"), 5, "what is wrong with this program", apiB);
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t.StartsWith("Linked to Meter, but:"), 5, "the line over the program", apiB);
        }
        finally
        {
            Stop(hostA);
            Stop(hostB);
        }
    }

    // Twice the same little program: each a slider, a field that a button shows, and a button nobody can press.
    private static string Twin(string name) => $$"""
        using Avalonia.Controls;
        using Neo.App;

        [AppInfo("{{name}}")]
        public class DynamicUserControl : UserControl
        {
            private int _level = 1;
            private int _pressed;
            private readonly TextBlock _shown = new TextBlock();

            public DynamicUserControl()
            {
                var slider = new Slider { Minimum = 0, Maximum = 100, Value = 10 };
                slider.ValueChanged += (_, e) => _shown.Text = "{{name}} SLIDER " + (int)e.NewValue + " LEVEL " + _level + " PRESSED " + _pressed;
                var show = new Button { Content = "Show" };
                show.Click += (_, _) => { _pressed++; Show(); };
                var locked = new Button { Content = "Locked", IsEnabled = false };
                locked.Click += (_, _) => { _pressed += 100; Show(); };
                var panel = new StackPanel { Margin = new Avalonia.Thickness(20, 50, 20, 20) };
                panel.Children.Add(_shown);
                panel.Children.Add(slider);
                panel.Children.Add(show);
                panel.Children.Add(locked);
                Content = panel;
                _shown.Text = "{{name}} READY";
            }

            private void Show() => _shown.Text = "{{name}} LEVEL " + _level + " PRESSED " + _pressed;
        }
        """;

    [Fact]
    public async Task Two_programs_answering_each_other_do_not_go_round_in_circles()
    {
        using var apiA = new StandInClaudeApi();
        using var apiB = new StandInClaudeApi();
        apiA.AnswerWithCode(Twin("LEFT"));
        apiB.AnswerWithCode(Twin("RIGHT"));
        // Each answer is new to the other: only that the link does not hear itself ends this.
        apiB.AnswerWithText(JsonConvert.SerializeObject(new
        {
            blocks = new[]
            {
                new { title = "Sliders one apart", detail = "", code = "a.OnChange<double>(\"Slider:0\", \"Value\", v => b.Set(\"Slider:0\", \"Value\", v + 1)); b.OnChange<double>(\"Slider:0\", \"Value\", v => a.Set(\"Slider:0\", \"Value\", v + 1));" },
                new { title = "Fields one apart", detail = "", code = "a.OnField<int>(\"_level\", n => b.SetField(\"_level\", n + 1)); b.OnField<int>(\"_level\", n => a.SetField(\"_level\", n + 1));" },
                new { title = "Presses going back and forth", detail = "", code = "a.OnClick(\"Button:0\", () => b.Press(\"Button:0\")); b.OnClick(\"Button:0\", () => a.Press(\"Button:0\"));" },
                new { title = "A locked button", detail = "", code = "a.OnClick(\"Button:0\", () => b.Press(\"Button:1\"));" },
            },
            note = "",
        }));

        using var hostA = Start(Host, apiA, _settingsA, Settings);
        using var hostB = Start(Host, apiB, _settingsB, Settings);
        try
        {
            var (a, b) = await TwoProgramsAsync(hostA, hostB, apiA, apiB, "LEFT READY", "RIGHT READY");

            await DropOnLinkHalfAsync(a, b, hostB);
            await ConnectAsync(hostB);
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "Linked to LEFT", 10, "the chip", apiB);

            SetSlider(AppOf(hostA), 20);
            await Task.Delay(1500);
            var left = (double)SliderIn(AppOf(hostA)).GetCurrentPropertyValue(RangeValuePattern.ValueProperty);
            var right = (double)SliderIn(AppOf(hostB)).GetCurrentPropertyValue(RangeValuePattern.ValueProperty);
            left.Should().BeLessThan(100, "it has not run away");
            right.Should().BeLessThan(100, "it has not run away");

            // A press goes over once, a locked button stays locked, and the fields stand still.
            var inA = AppOf(hostA);
            TopLevelWindows.Click(inA, ButtonIn(inA, "Show"));
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t.StartsWith("RIGHT LEVEL") && t.EndsWith("PRESSED 1"), 5, "the press in the other program", apiB);
            await Task.Delay(800);
            var levelNow = TextsIn(AppOf(hostB)).First(t => t.StartsWith("RIGHT LEVEL"));
            levelNow.Should().EndWith("PRESSED 1", "the press came once, and the locked button was not pressed");
            TextsIn(AppOf(hostA)).Should().Contain(t => t.StartsWith("LEFT LEVEL") && t.EndsWith("PRESSED 1"));
            int.Parse(levelNow.Split(' ')[2]).Should().BeLessThan(10, "the fields have not run away");
        }
        finally
        {
            Stop(hostA);
            Stop(hostB);
        }
    }

    [Fact]
    public async Task A_link_outlives_a_program_being_replaced()
    {
        using var apiA = new StandInClaudeApi();
        using var apiB = new StandInClaudeApi();
        apiA.AnswerWithCode(Meter);
        apiB.AnswerWithCode(Board);
        apiB.AnswerWithText(Proposal());
        // A new interface cannot be patched in: the program is replaced, in the same process, under a new address.
        apiA.AnswerWithCode(Meter
            .Replace("public class DynamicUserControl : UserControl", "public class DynamicUserControl : UserControl, System.IDisposable")
            .Replace("private void Show()", "public void Dispose() { }\n\n    private void Show()"));

        using var hostA = Start(Host, apiA, _settingsA, Settings);
        using var hostB = Start(Host, apiB, _settingsB, Settings);
        try
        {
            var (a, b) = await TwoProgramsAsync(hostA, hostB, apiA, apiB);
            await DropOnLinkHalfAsync(a, b, hostB);
            await ConnectAsync(hostB, UnchosenTitle);
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "AMOUNT 10", 10, "the first value", apiB);

            SetSlider(AppOf(hostA), 33);
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "AMOUNT 33", 5, "the value before", apiB);

            await SendPromptAsync(hostA, "Change it.");
            await WaitForTextAsync(hostA, "no such process", t => t.StartsWith("Hot reload not possible"), 60, "the note that the program is replaced", apiA);
            await WaitForTextAsync(hostA, ChildProcessName(Host), t => t.StartsWith("METER 0"), 30, "the new program", apiA);
            await WaitForTextAsync(hostA, ChildProcessName(Host), t => t == "Linked to Board", 5, "the link still there", apiA);

            // Its new slider is watched, and the other program still reaches it under its new address.
            await Task.Delay(500);
            SetSlider(AppOf(hostA), 66);
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "AMOUNT 66", 10, "the value from the new program", apiB);
            var inA = AppOf(hostA);
            TopLevelWindows.Click(inA, ButtonIn(inA, "Bump"));
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "ROWS 1 LAST 66", 10, "the press from the new program", apiB);
            await WaitForTextAsync(hostA, ChildProcessName(Host), t => t == "METER 1 HEARD 1", 10, "the answer in the new program", apiA);
        }
        finally
        {
            Stop(hostA);
            Stop(hostB);
        }
    }

    [Fact]
    public async Task Two_living_apps_are_linked_nothing_is_saved_and_the_link_ends_with_one_of_them()
    {
        // What a person has after an export: two programs and no N.E.O. in sight.
        Directory.CreateDirectory(_settingsA);
        Directory.CreateDirectory(_settingsB);
        var projectA = Path.Combine(_settingsA, "Meter.resx");
        var projectB = Path.Combine(_settingsB, "Board.resx");
        File.WriteAllText(projectA, JsonConvert.SerializeObject(new ResxData { Version = 2, Code = Meter, Nuget = new(), History = "" }));
        File.WriteAllText(projectB, JsonConvert.SerializeObject(new ResxData { Version = 2, Code = Board, Nuget = new(), History = "" }));
        var savedA = File.ReadAllText(projectA);
        var savedB = File.ReadAllText(projectB);

        using var apiA = new StandInClaudeApi();
        using var apiB = new StandInClaudeApi();
        apiB.AnswerWithText(Proposal());
        using var hostA = StartLiving(apiA, _settingsA, projectA);
        using var hostB = StartLiving(apiB, _settingsB, projectB);
        try
        {
            var a = await WaitForAsync(() => AppOf(hostA) is { } w && TextsIn(w).Any(t => t.StartsWith("METER 0")) ? w : null, 90, "the first program");
            var b = await WaitForAsync(() => AppOf(hostB) is { } w && TextsIn(w).Any(t => t.StartsWith("ROWS 0")) ? w : null, 90, "the second program");
            await Task.Delay(3000); // hot reload arms itself in the background
            Placed(a, b);
            await Task.Delay(400);

            await DropOnLinkHalfAsync(a, b, hostB);
            await DialogOfAsync(hostB);
            TopLevelWindows.Find(w => w.Pid == hostB.Id && w.Title.StartsWith("N.E.O")).Should().BeNull("a link is no reason to show N.E.O.");
            await ConnectAsync(hostB, UnchosenTitle);

            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "AMOUNT 10", 10, "the first value", apiB);
            SetSlider(AppOf(hostA), 55);
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "AMOUNT 55", 5, "the new value", apiB);

            // Nothing of it in either project, and what the apps did is not saved as code either.
            await Task.Delay(800);
            File.ReadAllText(projectA).Should().Be(savedA);
            File.ReadAllText(projectB).Should().Be(savedB);

            // The first program ends: the link is gone, and the other program goes on.
            var windowB = AppOf(hostB);
            Stop(hostA);
            await WaitForAsync(() => TextsIn(AppOf(hostB)).Contains("Linked to Meter") ? null : "gone", 10, "the chip going");
            await WaitForTextAsync(hostB, ChildProcessName(Host), t => t == "Meter was closed. The link is gone.", 5, "the reason over the program", apiB);
            TextsIn(windowB).Should().Contain("AMOUNT 55");
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
