using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using FluentAssertions;
using Neo.App;
using Newtonsoft.Json;
using Xunit;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// A living app the way its user meets it: the real host started with <c>--play</c> on a project,
/// driven through UI Automation. Only the app is on screen and shows its one-time hint; Ctrl+Shift+N
/// inside the app calls N.E.O. up, and again puts it away; closing the app ends the host.
/// The Avalonia host docks its own window beside the app window; the WPF host shows its panel inside
/// the window the app is embedded in.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class LivingAppSmokeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "neo_living_" + Guid.NewGuid().ToString("N")[..8]);

    private const string AvaloniaTodo = """
        using System.Collections.Generic;
        using Avalonia;
        using Avalonia.Controls;
        using Avalonia.Layout;

        namespace Neo.App
        {
            public class DynamicUserControl : UserControl
            {
                private readonly List<string> _items = new();
                private string _pending = "";
                public DynamicUserControl() { BuildUi(); }
                private void BuildUi()
                {
                    var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
                    panel.Children.Add(new TextBlock { Name = "title", Text = "ToDo", FontSize = 24 });
                    foreach (var item in _items) panel.Children.Add(new TextBlock { Text = "• " + item });
                    var input = new TextBox { Name = "input", Text = _pending };
                    input.TextChanged += (_, _) => _pending = input.Text ?? "";
                    var add = new Button { Name = "add", Content = "Hinzufügen" };
                    add.Click += (_, _) => { if (_pending.Trim().Length > 0) { _items.Add(_pending.Trim()); _pending = ""; BuildUi(); } };
                    panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { input, add } });
                    Content = panel;
                }
            }
        }
        """;

    private const string WpfTodo = """
        using System.Collections.Generic;
        using System.Windows;
        using System.Windows.Controls;

        namespace Neo.App
        {
            public class DynamicUserControl : UserControl
            {
                private readonly List<string> _items = new();
                private string _pending = "";
                public DynamicUserControl() { BuildUi(); }
                private void BuildUi()
                {
                    var panel = new StackPanel { Margin = new Thickness(16) };
                    panel.Children.Add(new TextBlock { Name = "title", Text = "ToDo", FontSize = 24 });
                    foreach (var item in _items) panel.Children.Add(new TextBlock { Text = "• " + item });
                    var input = new TextBox { Name = "input", Text = _pending, Width = 200 };
                    input.TextChanged += (_, _) => _pending = input.Text;
                    var add = new Button { Name = "add", Content = "Hinzufügen" };
                    add.Click += (_, _) => { if (_pending.Trim().Length > 0) { _items.Add(_pending.Trim()); _pending = ""; BuildUi(); } };
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    row.Children.Add(input); row.Children.Add(add);
                    panel.Children.Add(row);
                    Content = panel;
                }
            }
        }
        """;

    public static TheoryData<string> Hosts => new() { "Neo.App.Avalonia", "Neo.App" };

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task The_user_sees_only_the_app_and_calls_neo_up_with_the_shortcut(string hostProject)
    {
        bool wpf = hostProject == "Neo.App";
        var exe = TestEnvironment.HostExe(hostProject);
        File.Exists(exe).Should().BeTrue($"the host must be built: {exe}");

        Directory.CreateDirectory(_directory);
        var project = Path.Combine(_directory, "Notes.resx");
        File.WriteAllText(project, JsonConvert.SerializeObject(new ResxData { Version = 2, Code = wpf ? WpfTodo : AvaloniaTodo, Nuget = new(), History = "" }));

        using var host = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false, Arguments = $"--play \"{project}\"" })!;
        try
        {
            // Only the app, with its content and the one-time hint.
            var app = await WaitForAsync(() => AppWindow("Notes", wpf, withContent: true), 90, "the app window with its content");
            (await WaitForAsync(() => app.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, PlayerProject.HintText)), 10, "the hint")).Should().NotBeNull();
            NeoVisible(host.Id, wpf).Should().BeFalse("N.E.O. stays hidden until asked for");

            // The user works with the app.
            ((ValuePattern)app.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).GetCurrentPattern(ValuePattern.Pattern)).SetValue("Milch kaufen");
            ((InvokePattern)app.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Hinzufügen")).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            await Task.Delay(600);
            app = AppWindow("Notes", wpf, withContent: true)!;
            app.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "• Milch kaufen")).Should().NotBeNull();

            // Ctrl+Shift+N inside the app: N.E.O. comes …
            app.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).SetFocus();
            await Task.Delay(300);
            TopLevelWindows.Keys(app, "^+n");
            (await WaitForAsync(() => NeoVisible(host.Id, wpf) ? new object() : null, 10, "N.E.O. after Ctrl+Shift+N")).Should().NotBeNull();
            NeoPrompt(host.Id, wpf)!.SetFocus();

            // … and goes again, from N.E.O.'s side.
            await Task.Delay(300);
            TopLevelWindows.Keys(NeoWindow(host.Id, wpf)!, "^+n");
            await Task.Delay(1200);
            NeoVisible(host.Id, wpf).Should().BeFalse("the same shortcut puts N.E.O. away");
            AppWindow("Notes", wpf, withContent: true)!.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "• Milch kaufen"))
                .Should().NotBeNull("the app kept its state throughout");

            // Closing the app ends everything.
            var window = AppWindow("Notes", wpf, withContent: false)!;
            if (wpf)
                ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();
            else
            {
                window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).SetFocus();
                await Task.Delay(300);
                TopLevelWindows.Keys(window, "%{F4}");
            }
            host.WaitForExit(15000).Should().BeTrue("the host exits with the app window");
        }
        finally
        {
            try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch { }
            TopLevelWindows.KillChildrenOf(host.Id); // only this host's app window, never another N.E.O.'s
        }
    }

    // The window the user sees as the app: the child's own window under the Avalonia host, the
    // host's window (title = app name, content-only) under the WPF host. With content: only when
    // the app's controls are in it.
    private static AutomationElement? AppWindow(string title, bool wpf, bool withContent)
    {
        var window = FindWindow(w => w.Title == title);
        if (window == null || !withContent) return window;
        if (!wpf) return window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Hinzufügen")) != null ? window : null;
        // WPF: the app lives in the embedded child process' window.
        foreach (var p in Process.GetProcessesByName("Neo.PluginWindowWPF"))
            foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, p.Id)))
                if (w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Hinzufügen")) != null) return w;
        return null;
    }

    private static bool NeoVisible(int hostPid, bool wpf)
        => wpf ? NeoPrompt(hostPid, wpf) is { } prompt && !prompt.Current.IsOffscreen
               : FindWindow(w => w.Pid == hostPid && w.Title.StartsWith("N.E.O. ·")) != null;

    private static AutomationElement? NeoWindow(int hostPid, bool wpf) =>
        wpf ? FindWindow(w => w.Pid == hostPid) : FindWindow(w => w.Pid == hostPid && w.Title.StartsWith("N.E.O. ·"));

    private static AutomationElement? NeoPrompt(int hostPid, bool wpf) =>
        NeoWindow(hostPid, wpf)?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "txtPrompt"));

    private static AutomationElement? FindWindow(Func<(int Pid, string Title), bool> predicate) => TopLevelWindows.Find(predicate);

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, int seconds, string what) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            try { if (probe() is { } found) return found; } catch { }
            await Task.Delay(300);
        }
        throw new TimeoutException("not found: " + what);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
