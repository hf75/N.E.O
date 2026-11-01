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
                var panel = new StackPanel();
                panel.Children.Add(_words);
                panel.Children.Add(_state);
                panel.Children.Add(_result);
                panel.Children.Add(_lines);
                panel.Children.Add(press);
                panel.Children.Add(write);
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
