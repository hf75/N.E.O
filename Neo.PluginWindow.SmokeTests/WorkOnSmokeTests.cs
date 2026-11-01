using System.Windows.Automation;
using FluentAssertions;
using Xunit;
using static Neo.PluginWindow.SmokeTests.RealHost;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// The user goes on working while the AI writes the next change. The stand-in holds its answer back, so the
/// test decides how long the model "thinks". The Avalonia host: the WPF host covers its embedded app meanwhile.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class WorkOnSmokeTests : IDisposable
{
    private readonly string _settings = Path.Combine(Path.GetTempPath(), "neo_work_on_" + Guid.NewGuid().ToString("N")[..8]);

    private const string Host = "Neo.App.Avalonia";

    private const string Counter = """
        using Avalonia.Controls;

        public class DynamicUserControl : UserControl
        {
            private int _clicks;
            private TextBlock _label = null!;

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                _label = new TextBlock { Text = Say() };
                var count = new Button { Content = "Count" };
                count.Click += (_, _) => { _clicks++; _label.Text = Say(); };
                var panel = new StackPanel();
                panel.Children.Add(count);
                panel.Children.Add(_label);
                Content = panel;
            }

            private string Say() => "FIRST " + _clicks;
        }
        """;

    private const string Trouble = """
        using Avalonia.Controls;

        public class DynamicUserControl : UserControl
        {
            private TextBlock _label = null!;

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                _label = new TextBlock { Text = "FIRST" };
                var go = new Button { Content = "Go" };
                go.Click += (_, _) => Go();
                var panel = new StackPanel();
                panel.Children.Add(go);
                panel.Children.Add(_label);
                Content = panel;
            }

            private void Go() { throw new System.InvalidOperationException("boom"); }
        }
        """;

    private static AutomationElement App(System.Diagnostics.Process host) =>
        System.Diagnostics.Process.GetProcessesByName(ChildProcessName(Host))
            .Where(p => TopLevelWindows.ParentProcessId(p.Id) == host.Id)
            .Select(p => TopLevelWindows.Find(w => w.Pid == p.Id))
            .First(w => w != null)!;

    private static AutomationElement ButtonIn(AutomationElement window, string name) =>
        window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().First(b => b.Current.Name == name);

    [Fact]
    public async Task The_app_stays_in_use_while_the_next_change_is_written_and_the_patch_finds_it_as_it_was_left()
    {
        using var api = new StandInClaudeApi();
        api.AnswerWithCode(Counter);
        api.AnswerWithCode(Counter.Replace("FIRST", "SECOND"));

        using var host = Start(Host, api, _settings, new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true });
        try
        {
            await SendPromptAsync(host, "Build it.");
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "FIRST 0", 60, "the first version", api);
            await Task.Delay(3000); // hot reload arms itself in the background

            api.HoldAnswers();
            await SendPromptAsync(host, "Change it.");
            await WaitForAsync(() => api.Requests.Count == 2 ? "asked" : null, 30, "the second request");

            // The model is writing. The app is there, and it works.
            var app = await WaitForAsync(() => App(host), 10, "the app window");
            TextsIn(app).Should().NotContain(t => t.StartsWith("Generating"), "nothing covers the app");
            TopLevelWindows.Click(app, ButtonIn(app, "Count"));
            await Task.Delay(300);
            TopLevelWindows.Click(app, ButtonIn(app, "Count"));
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "FIRST 2", 10, "two clicks counted while the model writes", api);

            var neo = await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O")), 10, "the N.E.O. window");
            TextsIn(neo).Should().Contain("Generating...", "N.E.O. says what it is doing, now that the app window does not");

            api.ReleaseAnswers();

            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "SECOND 2", 30, "the change, with the clicks made meanwhile", api);
        }
        finally
        {
            api.ReleaseAnswers();
            Stop(host);
        }
    }

    [Fact]
    public async Task An_error_while_the_next_change_is_written_is_repaired_when_that_is_done()
    {
        using var api = new StandInClaudeApi();
        api.AnswerWithCode(Trouble);
        api.AnswerWithCode(Trouble.Replace("\"FIRST\"", "\"SECOND\""));
        api.AnswerWithCode(Trouble.Replace("\"FIRST\"", "\"SECOND\"").Replace("""throw new System.InvalidOperationException("boom");""", """_label.Text = "REPAIRED";"""));

        using var host = Start(Host, api, _settings, new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true });
        try
        {
            await SendPromptAsync(host, "Build it.");
            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "FIRST", 60, "the first version", api);
            await Task.Delay(3000);

            api.HoldAnswers();
            await SendPromptAsync(host, "Change it.");
            await WaitForAsync(() => api.Requests.Count == 2 ? "asked" : null, 30, "the second request");

            var app = await WaitForAsync(() => App(host), 10, "the app window");
            TopLevelWindows.Click(app, ButtonIn(app, "Go"));
            await WaitForTextAsync(host, ChildProcessName(Host), t => t.Contains("repairing"), 10, "the red banner", api);

            api.ReleaseAnswers();

            await WaitForTextAsync(host, ChildProcessName(Host), t => t == "REPAIRED", 60, "the repair, and the failed click done again", api);
            api.Requests.Should().HaveCount(3, "the change first, then the repair");
            api.Prompts[2].Should().Contain("Please fix the previous error");
        }
        finally
        {
            api.ReleaseAnswers();
            Stop(host);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_settings, recursive: true); } catch { }
    }
}
