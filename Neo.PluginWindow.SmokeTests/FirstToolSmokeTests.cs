using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;
using FluentAssertions;
using Neo.App;
using Xunit;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// The moment N.E.O. is about: a prompt in the real host, and a tool on the screen. Only the Anthropic
/// API is a stand-in, which answers at once, so what this measures is the host. Models name the Avalonia
/// packages; downloading them used to take half a minute (and 2.3 GB) for files the compiler did not use.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class FirstToolSmokeTests : IDisposable
{
    private readonly string _settings = Path.Combine(Path.GetTempPath(), "neo_first_tool_" + Guid.NewGuid().ToString("N")[..8]);

    private const string Tool = """
        using Avalonia;
        using Avalonia.Controls;

        public class DynamicUserControl : UserControl
        {
            public DynamicUserControl() { Content = new Button { Content = "Count" }; }
        }
        """;

    private static readonly string[] Packages = ["Avalonia|12.1.2", "Avalonia.Desktop|12.1.2", "Avalonia.Themes.Fluent|12.1.2"];

    [Fact]
    public async Task The_first_tool_downloads_nothing_the_host_ships_and_a_retry_takes_more_care()
    {
        using var api = new StandInClaudeApi();
        api.AnswerWithCode(Tool.Replace("new Button", "new NoSuchControl"), Packages);
        api.AnswerWithCode(Tool, Packages);

        Directory.CreateDirectory(_settings);
        File.WriteAllText(Path.Combine(_settings, "settings.json"), JsonSerializer.Serialize(new { ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true }));

        var startInfo = new ProcessStartInfo(TestEnvironment.HostExe("Neo.App.Avalonia")) { UseShellExecute = false };
        startInfo.WorkingDirectory = Path.GetDirectoryName(startInfo.FileName)!;
        startInfo.Environment[SettingsService.FolderEnvironmentVariable] = _settings;
        startInfo.Environment["NEO_ANTHROPIC_BASE_URL"] = api.BaseUrl;
        startInfo.Environment["ANTHROPIC_API_KEY"] = "not-a-real-key";
        using var host = Process.Start(startInfo)!;

        try
        {
            var window = await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O")), 60, "the N.E.O. window");
            var prompt = await WaitForAsync(() => window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "txtPrompt")) is { Current.IsEnabled: true } box ? box : null, 60, "the prompt box");

            Process.GetProcessesByName("Neo.PluginWindowAvalonia").Should().Contain(
                p => TopLevelWindows.ParentProcessId(p.Id) == host.Id && TopLevelWindows.Find(w => w.Pid == p.Id) != null,
                "the prompt box waits for the app window: a build has nowhere to go before that, and the prompt would be lost");

            TopLevelWindows.Click(window, prompt);
            ((ValuePattern)prompt.GetCurrentPattern(ValuePattern.Pattern)).SetValue("Build a button.");
            await Task.Delay(200);
            var clock = Stopwatch.StartNew();
            TopLevelWindows.Keys(window, "{ENTER}");

            try
            {
                await WaitForAsync(() =>
                {
                    foreach (var p in Process.GetProcessesByName("Neo.PluginWindowAvalonia"))
                        if (TopLevelWindows.ParentProcessId(p.Id) == host.Id && TopLevelWindows.Find(w => w.Pid == p.Id) is { } app)
                            return app.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Count"));
                    return null;
                }, 30, "the tool");
            }
            catch (TimeoutException)
            {
                var said = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                    .Cast<AutomationElement>().Select(e => e.Current.Name).Where(n => n.Length > 0);
                throw new TimeoutException($"No tool. {api.Requests.Count} request(s) reached the API. Prompt box: '{((ValuePattern)prompt.GetCurrentPattern(ValuePattern.Pattern)).Current.Value}'. N.E.O. says: " + string.Join(" | ", said));
            }

            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "two compiles take seconds; the download alone took over 30");

            var efforts = api.Requests.Select(r => JsonDocument.Parse(r).RootElement.GetProperty("output_config").GetProperty("effort").GetString());
            efforts.Should().Equal(["low", "medium"], "the code of the first try did not build, so the second takes more care");
        }
        finally
        {
            try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch { }
            TopLevelWindows.KillChildrenOf(host.Id);
        }
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, int seconds, string what) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            try { if (probe() is { } found) return found; } catch { }
            await Task.Delay(200);
        }
        throw new TimeoutException("not found: " + what);
    }

    public void Dispose()
    {
        try { Directory.Delete(_settings, recursive: true); } catch { }
    }
}
