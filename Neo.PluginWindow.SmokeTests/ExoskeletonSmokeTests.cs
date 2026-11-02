using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Forms;
using FluentAssertions;
using Xunit;
using static Neo.PluginWindow.SmokeTests.RealHost;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// A tool docked to another program, with real windows: the real Avalonia host builds it (the model is a
/// stand-in), and the other program is a window of this test, which N.E.O. knows nothing about.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class ExoskeletonSmokeTests : IDisposable
{
    private const string Host = "Neo.App.Avalonia";

    private readonly string _settings = Path.Combine(Path.GetTempPath(), "neo_exo_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _title = "ExoTarget" + Guid.NewGuid().ToString("N")[..8];
    private Target? _target;

    private string Tool => $$"""
        using System;
        using Avalonia.Controls;
        using Neo.App;

        public class DynamicUserControl : UserControl
        {
            private readonly TextBlock _words = new TextBlock { Text = "WORDS none" };
            private readonly TextBlock _state = new TextBlock { Text = "STATE waiting" };
            private readonly TextBlock _result = new TextBlock { Text = "RESULT none" };
            private readonly TextBlock _lines = new TextBlock { Text = "LINES none" };
            private readonly ForeignApp _target;

            public DynamicUserControl()
            {
                var press = new Button { Content = "Press" };
                press.Click += async (_, _) => _result.Text = "PRESSED " + await _target.ClickAsync("Say hello");
                var write = new Button { Content = "Write" };
                write.Click += async (_, _) => _result.Text = "WRITTEN " + await _target.SetTextAsync("written by the panel");
                var letGo = new Button { Content = "Let go" };
                letGo.Click += (_, _) => _target.Dispose();
                var panel = new StackPanel();
                panel.Children.Add(_words);
                panel.Children.Add(_state);
                panel.Children.Add(_result);
                panel.Children.Add(_lines);
                panel.Children.Add(press);
                panel.Children.Add(write);
                panel.Children.Add(letGo);
                Content = panel;

                _target = ForeignApp.Attach("{{_title}}", size: 260);
                _target.Opened += () => _state.Text = "STATE open";
                _target.Closed += () => _state.Text = "STATE closed";
                _target.TextChanged += text =>
                {
                    _words.Text = "WORDS " + text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                    _lines.Text = "LINES " + text.Split('\n').Length;
                };
            }
        }
        """;

    /// <summary>The other program: a window with a text box and a button, on a thread of its own.</summary>
    private sealed class Target : IDisposable
    {
        private readonly Thread _thread;
        private Form _form = null!;
        private TextBox _text = null!;
        private readonly ManualResetEventSlim _ready = new();

        public int Pressed { get; private set; }

        public Target(string title, int x, int y)
        {
            _thread = new Thread(() =>
            {
                _form = new Form { Text = title, StartPosition = FormStartPosition.Manual, Bounds = new System.Drawing.Rectangle(x, y, 500, 400) };
                _text = new TextBox { Multiline = true, Dock = DockStyle.Fill };
                var button = new Button { Text = "Say hello", Dock = DockStyle.Top };
                button.Click += (_, _) => Pressed++;
                _form.Controls.Add(_text);
                _form.Controls.Add(button);
                _form.Shown += (_, _) => _ready.Set();
                Application.Run(_form);
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(10));
        }

        public IntPtr Handle => (IntPtr)_form.Invoke(() => _form.Handle);
        public string Text { get => (string)_form.Invoke(() => _text.Text); set => _form.Invoke(() => _text.Text = value); }
        public void MoveTo(int x, int y) => _form.Invoke(() => _form.Location = new System.Drawing.Point(x, y));
        public void Minimize() => _form.Invoke(() => _form.WindowState = FormWindowState.Minimized);
        public void Restore() => _form.Invoke(() => _form.WindowState = FormWindowState.Normal);

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

    private static AutomationElement ButtonIn(AutomationElement window, string name) =>
        window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().First(b => b.Current.Name == name);

    private static IntPtr Handle(AutomationElement window) => (IntPtr)window.Current.NativeWindowHandle;

    private static Rect WindowRect(IntPtr window)
    {
        GetWindowRect(window, out var rect);
        return rect;
    }

    private static Rect VisibleFrame(IntPtr window)
    {
        DwmGetWindowAttribute(window, 9, out Rect rect, Marshal.SizeOf<Rect>());
        return rect;
    }

    /// <summary>Waits until the panel stands right beside the window, with its height.</summary>
    private static async Task WaitUntilDockedAsync(IntPtr panel, IntPtr target, string what)
    {
        await WaitForAsync(() =>
        {
            var p = WindowRect(panel);
            var t = VisibleFrame(target);
            return Math.Abs(p.Left - t.Right) <= 1 && Math.Abs(p.Top - t.Top) <= 1 && Math.Abs(p.Bottom - t.Bottom) <= 1 ? "docked" : null;
        }, 5, what);
    }

    [Fact]
    public async Task A_tool_docks_to_another_program_reads_it_uses_it_and_lives_with_it()
    {
        using var api = new StandInClaudeApi();
        api.AnswerWithCode(Tool);
        api.AnswerWithCode(Tool.Replace("WORDS ", "COUNT "));
        _target = new Target(_title, 300, 200);
        _target.Text = "a private note";

        using var host = Start(Host, api, _settings, new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true });
        try
        {
            await SendPromptAsync(host, $"Give {_title} a word count.");
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "STATE open", 60, "the tool finding the program", api);

            // The model knew what the program looks like, and not what is written in it.
            api.Prompts[0].Should().Contain("Say hello").And.Contain(_title);
            api.Prompts[0].Should().NotContain("a private note");
            var system = System.Text.Json.Nodes.JsonNode.Parse(api.Requests[0])!["system"]!.ToJsonString();
            system.Should().Contain("ForeignApp.Attach");
            await WaitForTextAsync(host, ChildProcessName(Host), t => t.Contains("The AI sees the window title and the structure"), 5, "the chat saying what the model saw", api);

            var panel = await WaitForAsync(() => Panel(host), 10, "the panel");
            var panelWindow = Handle(panel);
            var targetWindow = _target.Handle;

            // It sits right beside the program, without a title bar of its own.
            await WaitUntilDockedAsync(panelWindow, targetWindow, "the panel beside the program");
            (GetWindowLongPtr(panelWindow, -16) & 0x00C00000).Should().Be(0, "it looks like a part of the program");
            WindowRect(panelWindow).Width.Should().BeInRange(260, 260 * 3, "the size the tool asked for, on a scaled screen too");

            // It reads the program, and lines end the same everywhere, whatever the program uses.
            _target.Text = "one two three";
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "WORDS 3", 5, "the word count", api);
            _target.Text = "first line\r\nsecond";
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "LINES 2", 5, "the lines of the text", api);

            // It follows the program, and no longer N.E.O.
            _target.MoveTo(420, 260);
            await WaitUntilDockedAsync(panelWindow, targetWindow, "the panel following the program");
            var neo = TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O"))!;
            var neoRect = WindowRect(Handle(neo));
            MoveWindow(Handle(neo), neoRect.Left + 40, neoRect.Top + 30, neoRect.Width, neoRect.Height, true);
            await Task.Delay(700);
            await WaitUntilDockedAsync(panelWindow, targetWindow, "the panel staying with the program when N.E.O. moves");

            // The program comes to the front, and the panel comes along.
            using (var cover = new Target("Cover" + _title, WindowRect(panelWindow).Left - 40, WindowRect(panelWindow).Top - 40))
            {
                await WaitForAsync(() => TopLevelWindows.IsAbove(cover.Handle, panelWindow) ? "covered" : null, 5, "another window over the panel");
                TopLevelWindows.Foreground(AutomationElement.FromHandle(targetWindow));
                await WaitForAsync(() => TopLevelWindows.IsAbove(panelWindow, cover.Handle) ? "above" : null, 5, "the panel coming to the front with the program");
            }

            // It uses the program.
            TopLevelWindows.Click(panel, ButtonIn(panel, "Press"));
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "PRESSED True", 5, "the button of the program pressed", api);
            _target.Pressed.Should().Be(1);
            TopLevelWindows.Click(panel, ButtonIn(panel, "Write"));
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "WRITTEN True", 5, "the text of the program replaced", api);
            _target.Text.Should().Be("written by the panel");
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "WORDS 4", 5, "the word count of the new text", api);

            // It goes away with the program and comes back with it.
            _target.Minimize();
            await WaitForAsync(() => IsWindowVisible(panelWindow) ? null : "hidden", 5, "the panel hiding with the program");
            _target.Restore();
            await WaitForAsync(() => IsWindowVisible(panelWindow) ? "shown" : null, 5, "the panel coming back");
            await WaitUntilDockedAsync(panelWindow, targetWindow, "the panel beside the restored program");

            // The next change is patched in: it stays where it is, still reading.
            await Task.Delay(3000); // hot reload arms itself in the background
            await SendPromptAsync(host, "Call it a count.");
            await WaitForAsync(() => api.Requests.Count == 2 ? "asked" : null, 30, "the second request");
            await Task.Delay(4000);
            _target.Text = "one two";
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "COUNT 2", 30, "the change, in the docked tool", api);
            await WaitUntilDockedAsync(panelWindow, targetWindow, "the panel still docked after the change");

            // The program ends: the tool is a normal window again, and docks again when the program returns.
            _target.Dispose();
            _target = null;
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "STATE closed", 5, "the tool noticing that the program ended", api);
            IsWindowVisible(panelWindow).Should().BeTrue();
            (GetWindowLongPtr(panelWindow, -16) & 0x00C00000).Should().NotBe(0, "it is a normal window again");

            _target = new Target(_title, 300, 200);
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "STATE open", 5, "the tool finding the program again", api);
            await WaitUntilDockedAsync(panelWindow, _target.Handle, "the panel beside the new window");

            // The tool lets go by itself.
            TopLevelWindows.Click(panel, ButtonIn(panel, "Let go"));
            await WaitForAsync(() => (GetWindowLongPtr(panelWindow, -16) & 0x00C00000) != 0 ? "normal" : null, 5, "the title bar after Dispose");
        }
        finally
        {
            Stop(host);
        }
    }

    // A new interface cannot be patched in: the tool is replaced, in the same process, and nobody tells the one before.
    private string ToolThatMustBeReplaced => Tool
        .Replace("WORDS ", "COUNT ")
        .Replace("public class DynamicUserControl : UserControl", "public class DynamicUserControl : UserControl, IDisposable")
        .Replace("public DynamicUserControl()", "public void Dispose() { }\n\n    public DynamicUserControl()");

    [Fact]
    public async Task A_tool_replaced_while_docked_gets_its_title_bar_back_when_the_program_ends()
    {
        using var api = new StandInClaudeApi();
        api.AnswerWithCode(Tool);
        api.AnswerWithCode(ToolThatMustBeReplaced);
        _target = new Target(_title, 300, 200);

        using var host = Start(Host, api, _settings, new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true });
        try
        {
            await SendPromptAsync(host, $"Give {_title} a word count.");
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "STATE open", 60, "the tool finding the program", api);
            var panelWindow = Handle(await WaitForAsync(() => Panel(host), 10, "the panel"));
            await WaitUntilDockedAsync(panelWindow, _target.Handle, "the panel beside the program");
            var docked = WindowRect(panelWindow).Width;
            await Task.Delay(3000); // hot reload arms itself in the background

            await SendPromptAsync(host, "Call it a count.");
            await WaitForTextAsync(host, "no such process", t => t.StartsWith("Hot reload not possible"), 60, "the note that the tool is replaced", api);
            _target.Text = "one two";
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "COUNT 2", 30, "the new tool reading the program", api);
            await WaitUntilDockedAsync(panelWindow, _target.Handle, "the new tool still docked");

            _target.Dispose();
            _target = null;
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "STATE closed", 5, "the new tool noticing that the program ended", api);
            await Task.Delay(1000); // long enough for a follower left behind to do what it does
            (GetWindowLongPtr(panelWindow, -16) & 0x00C00000).Should().NotBe(0, "it is a normal window again, not a window nobody can move");
            WindowRect(panelWindow).Width.Should().BeGreaterThan(docked, "it has the size it had before it ever docked");

            // The one before it has stopped: the new tool alone follows the program when it returns.
            _target = new Target(_title, 340, 240);
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "STATE open", 5, "the new tool finding the program again", api);
            await WaitUntilDockedAsync(panelWindow, _target.Handle, "the panel beside the new window");
            (GetWindowLongPtr(panelWindow, -16) & 0x00C00000).Should().Be(0, "it looks like a part of the program again");
        }
        finally
        {
            Stop(host);
        }
    }

    private static string ToolWithoutTheProgram(string attaching) => $$"""
        using System;
        using Avalonia.Controls;
        using Neo.App;

        public class DynamicUserControl : UserControl, IDisposable
        {
            public DynamicUserControl()
            {
                Content = new TextBlock { Text = "PLAIN TOOL" };
                {{attaching}}
            }
            public void Dispose() { }
        }
        """;

    [Theory]
    [InlineData("")]
    [InlineData("ForeignApp.Attach(\"NoProgramOfThisName\", size: 260);")]
    public async Task A_tool_replaced_by_one_that_does_not_dock_leaves_a_normal_window(string attaching)
    {
        using var api = new StandInClaudeApi();
        api.AnswerWithCode(Tool);
        api.AnswerWithCode(ToolWithoutTheProgram(attaching));
        _target = new Target(_title, 300, 200);

        using var host = Start(Host, api, _settings, new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true });
        try
        {
            await SendPromptAsync(host, $"Give {_title} a word count.");
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "STATE open", 60, "the tool finding the program", api);
            var panelWindow = Handle(await WaitForAsync(() => Panel(host), 10, "the panel"));
            await WaitUntilDockedAsync(panelWindow, _target.Handle, "the panel beside the program");
            var docked = WindowRect(panelWindow).Width;
            await Task.Delay(3000); // hot reload arms itself in the background

            await SendPromptAsync(host, "Make it something else.");
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "PLAIN TOOL", 60, "the other tool", api);
            await WaitForAsync(() => (GetWindowLongPtr(panelWindow, -16) & 0x00C00000) != 0 ? "normal" : null, 5, "the title bar coming back");
            WindowRect(panelWindow).Width.Should().BeGreaterThan(docked, "it has the size it had before it ever docked");

            // Nothing follows the program any more.
            _target.MoveTo(440, 300);
            await Task.Delay(500);
            var panel = WindowRect(panelWindow);
            var target = VisibleFrame(_target.Handle);
            (Math.Abs(panel.Left - target.Right) <= 1 && Math.Abs(panel.Top - target.Top) <= 1).Should().BeFalse("the tool that docked is gone");
        }
        finally
        {
            Stop(host);
        }
    }

    // A part of a title finds a window, so nothing in common with the first program.
    private static string TitleOfAnotherProgram() => "OtherProgram" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Docks to the first program, and on a button to another one: the window is docked already when that one takes it.</summary>
    private string ToolThatMovesOver(string other, string first) => $$"""
        using Avalonia.Controls;
        using Neo.App;

        public class DynamicUserControl : UserControl
        {
            private readonly TextBlock _first = new TextBlock { Text = "FIRST none" };
            private readonly TextBlock _state = new TextBlock { Text = "SECOND waiting" };
            private readonly ForeignApp _one;
            private ForeignApp _two;

            public DynamicUserControl()
            {
                var moveOver = new Button { Content = "Move over" };
                moveOver.Click += (_, _) =>
                {
                    {{first}}
                    _two = ForeignApp.Attach("{{other}}", size: 260);
                    _two.Opened += () => _state.Text = "SECOND open";
                    _two.Closed += () => _state.Text = "SECOND closed";
                };
                var panel = new StackPanel();
                panel.Children.Add(_first);
                panel.Children.Add(_state);
                panel.Children.Add(moveOver);
                Content = panel;

                _one = ForeignApp.Attach("{{_title}}", size: 260);
                _one.TextChanged += text => _first.Text = "FIRST " + text;
            }
        }
        """;

    private System.Diagnostics.Process StartWith(StandInClaudeApi api, string tool)
    {
        api.AnswerWithCode(tool);
        _target = new Target(_title, 200, 150);
        return Start(Host, api, _settings, new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true });
    }

    /// <summary>The tool docked to the first program, and the button pressed.</summary>
    private async Task<(IntPtr Window, int Docked)> MovedOverAsync(System.Diagnostics.Process host, StandInClaudeApi api)
    {
        await SendPromptAsync(host, "Build it.");
        await WaitForTextAsync(host, ChildProcessName(Host), t => t == "FIRST ", 60, "the tool reading the first program", api);
        var panel = await WaitForAsync(() => Panel(host), 10, "the panel");
        var window = Handle(panel);
        await WaitUntilDockedAsync(window, _target!.Handle, "the panel beside the first program");
        (GetWindowLongPtr(window, -16) & 0x00C00000).Should().Be(0, "it is docked before the other one takes it");
        var docked = WindowRect(window).Width;
        TopLevelWindows.Click(panel, ButtonIn(panel, "Move over"));
        return (window, docked);
    }

    private async Task WaitForTheNormalLookAsync(IntPtr window, int docked)
    {
        await WaitForAsync(() => (GetWindowLongPtr(window, -16) & 0x00C00000) != 0 ? "normal" : null, 5, "the title bar coming back");
        WindowRect(window).Width.Should().BeGreaterThan(docked, "it has the size it had before it ever docked");
    }

    [Fact]
    public async Task A_later_attach_takes_the_window_over_and_the_first_still_reads()
    {
        var second = TitleOfAnotherProgram();
        using var api = new StandInClaudeApi();
        using var other = new Target(second, 500, 300);
        using var host = StartWith(api, ToolThatMovesOver(second, ""));
        try
        {
            var (window, docked) = await MovedOverAsync(host, api);
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "SECOND open", 5, "the tool finding the second program", api);
            await WaitUntilDockedAsync(window, other.Handle, "the panel beside the second program");

            _target!.Text = "still read";
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "FIRST still read", 5, "the first program being read", api);

            // The first one ending is no reason to let go of the second.
            _target.Dispose();
            _target = null;
            await Task.Delay(1000);
            (GetWindowLongPtr(window, -16) & 0x00C00000).Should().Be(0, "the panel is docked to the second program");
            await WaitUntilDockedAsync(window, other.Handle, "the panel still beside the second program");

            other.Dispose();
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "SECOND closed", 5, "the second program ending", api);
            await WaitForTheNormalLookAsync(window, docked);
        }
        finally
        {
            Stop(host);
        }
    }

    [Fact]
    public async Task A_tool_can_let_go_of_one_program_and_dock_to_another_at_once()
    {
        var second = TitleOfAnotherProgram();
        using var api = new StandInClaudeApi();
        using var other = new Target(second, 500, 300);
        using var host = StartWith(api, ToolThatMovesOver(second, "_one.Dispose();"));
        try
        {
            var (window, docked) = await MovedOverAsync(host, api);
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "SECOND open", 5, "the tool finding the second program", api);
            await WaitUntilDockedAsync(window, other.Handle, "the panel beside the second program");

            // Long enough for the first one to end: it leaves the window to the second.
            await Task.Delay(1000);
            (GetWindowLongPtr(window, -16) & 0x00C00000).Should().Be(0, "the panel is docked to the second program");
            await WaitUntilDockedAsync(window, other.Handle, "the panel still beside the second program");

            other.Dispose();
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "SECOND closed", 5, "the second program ending", api);
            await WaitForTheNormalLookAsync(window, docked);
        }
        finally
        {
            Stop(host);
        }
    }

    [Fact]
    public async Task A_later_attach_to_a_program_that_is_not_open_leaves_a_normal_window_until_it_is()
    {
        var second = TitleOfAnotherProgram();
        using var api = new StandInClaudeApi();
        using var host = StartWith(api, ToolThatMovesOver(second, ""));
        try
        {
            var (window, docked) = await MovedOverAsync(host, api);
            await WaitForTheNormalLookAsync(window, docked);

            using var other = new Target(second, 500, 300);
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "SECOND open", 5, "the tool finding the second program", api);
            await WaitUntilDockedAsync(window, other.Handle, "the panel beside the second program");
        }
        finally
        {
            Stop(host);
        }
    }

    public void Dispose()
    {
        _target?.Dispose();
        try { Directory.Delete(_settings, recursive: true); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr(IntPtr window, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
}
