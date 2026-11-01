using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;
using FluentAssertions;
using Neo.App;
using Xunit;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// Web search in the real hosts. A generated app asks the web from its own process; Claude looks something
/// up while it builds a tool, and the user is told. Only the Anthropic API is a stand-in.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class WebSearchSmokeTests : IDisposable
{
    private readonly string _settings = Path.Combine(Path.GetTempPath(), "neo_web_search_" + Guid.NewGuid().ToString("N")[..8]);

    private const string PageTitle = "The page it found";
    private const string PageUrl = "https://example.test/found";

    private static string WithContent(string host, string body) => host == "Neo.App.Avalonia"
        ? $$"""
            using System.Collections.Generic;
            using Avalonia.Controls;
            using Neo.App;

            public class DynamicUserControl : UserControl
            {
                private readonly TextBlock _text = new TextBlock { Text = "working" };
                public DynamicUserControl() { Content = _text; Loaded += async (_, _) => { {{body}} }; }
                private void Show(string text) => _text.Text = text;
            }
            """
        : $$"""
            using System.Collections.Generic;
            using System.Windows.Controls;
            using Neo.App;

            public class DynamicUserControl : UserControl
            {
                private readonly TextBlock _text = new TextBlock { Text = "working" };
                public DynamicUserControl() { Content = _text; Loaded += async (_, _) => { {{body}} }; }
                private void Show(string text) => _text.Text = text;
            }
            """;

    private const string Counter = "public record Launch(string Name, int Year);";

    private Process Start(string host, StandInClaudeApi api, object settings)
    {
        Directory.CreateDirectory(_settings);
        File.WriteAllText(Path.Combine(_settings, "settings.json"), JsonSerializer.Serialize(settings));

        var startInfo = new ProcessStartInfo(TestEnvironment.HostExe(host)) { UseShellExecute = false };
        startInfo.WorkingDirectory = Path.GetDirectoryName(startInfo.FileName)!;
        startInfo.Environment[SettingsService.FolderEnvironmentVariable] = _settings;
        startInfo.Environment["NEO_ANTHROPIC_BASE_URL"] = api.BaseUrl;
        startInfo.Environment["ANTHROPIC_API_KEY"] = "not-a-real-key";
        startInfo.Environment.Remove("OPENAI_API_KEY");
        startInfo.Environment.Remove("GEMINI_API_KEY");
        return Process.Start(startInfo)!;
    }

    private static string ChildProcessName(string host) => host == "Neo.App.Avalonia" ? "Neo.PluginWindowAvalonia" : "Neo.PluginWindowWPF";

    private static async Task<AutomationElement> SendPromptAsync(Process host, string prompt)
    {
        var window = await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O")), 60, "the N.E.O. window");
        var box = await WaitForAsync(() => window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "txtPrompt")) is { Current.IsEnabled: true } found ? found : null, 60, "the prompt box");

        TopLevelWindows.Click(window, box);
        ((ValuePattern)box.GetCurrentPattern(ValuePattern.Pattern)).SetValue(prompt);
        await Task.Delay(200);
        TopLevelWindows.Keys(window, "{ENTER}");
        return window;
    }

    private static IEnumerable<string> TextsIn(AutomationElement window) =>
        window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().Select(e => e.Current.Name).Where(n => n.Length > 0);

    /// <summary>A text in the window of the host's app, or in the host itself where the app sits inside it.</summary>
    private static async Task<string> WaitForTextAsync(Process host, string childProcess, Func<string, bool> wanted, int seconds, string what, StandInClaudeApi api)
    {
        string? Probe()
        {
            foreach (var p in Process.GetProcessesByName(childProcess))
            {
                if (TopLevelWindows.ParentProcessId(p.Id) == host.Id && TopLevelWindows.Find(w => w.Pid == p.Id) is { } window
                    && TextsIn(window).FirstOrDefault(wanted) is { } text)
                    return text;
            }
            return TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O")) is { } neo ? TextsIn(neo).FirstOrDefault(wanted) : null;
        }

        try
        {
            return await WaitForAsync(Probe, seconds, what);
        }
        catch (TimeoutException)
        {
            var neo = TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O"));
            throw new TimeoutException($"not found: {what}. {api.Requests.Count} request(s) reached the API. N.E.O. says: " + (neo == null ? "(no window)" : string.Join(" | ", TextsIn(neo))));
        }
    }

    [Theory]
    [InlineData("Neo.App")]
    [InlineData("Neo.App.Avalonia")]
    public async Task A_generated_app_asks_the_web_and_shows_the_answer_with_its_source(string hostProject)
    {
        using var api = new StandInClaudeApi();
        api.AnswerWithCode(WithContent(hostProject, """
            try
            {
                var answer = await AIWebSearch.AskAsync("How is the weather?");
                var data = await AIWebSearch.AskAsync<List<Launch>>("Which launches?");
                Show("ANSWER " + answer.Text + " FROM " + answer.Sources[0].Title + " AT " + answer.Sources[0].Url + " DATA " + data.Data[0].Name + data.Data[0].Year + " SOURCES " + data.Sources.Count);
            }
            catch (System.Exception ex) { Show("ANSWER failed: " + ex); }
            """) + Counter);
        api.AnswerAfterSearching("weather today", PageTitle, PageUrl, "Sunny in the test.");
        api.AnswerAfterSearching("next launches", PageTitle, PageUrl, $$"""{"result":[{"name":"Ariane","year":2026}],"usedPages":["{{PageUrl}}"]}""", cited: false);

        using var host = Start(hostProject, api, new { UseAvalonia = hostProject == "Neo.App.Avalonia", ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true, PluginAgentModels = new Dictionary<string, string> { ["WebSearch"] = "stand-in-search" } });

        try
        {
            await SendPromptAsync(host, "Build a weather tool.");

            var shown = await WaitForTextAsync(host, ChildProcessName(hostProject), t => t.StartsWith("ANSWER"), 60, "the answer in the app", api);

            shown.Should().Be($"ANSWER Sunny in the test. FROM {PageTitle} AT {PageUrl} DATA Ariane2026 SOURCES 1");

            var requests = api.Requests.Select(r => JsonDocument.Parse(r).RootElement).ToList();
            requests.Should().HaveCount(3);
            requests[0].TryGetProperty("tools", out _).Should().BeFalse("searching while building is off unless the user wants it");
            requests[0].GetProperty("system").GetString().Should().Contain("AIWebSearch.AskAsync", "the prompt tells about it");

            var asked = requests[1];
            asked.GetProperty("model").GetString().Should().Be("stand-in-search", "the model comes from the settings");
            asked.GetProperty("tools")[0].GetProperty("type").GetString().Should().Be("web_search_20250305");
            asked.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("How is the weather?");
            requests[2].GetProperty("output_config").GetProperty("format").GetProperty("type").GetString().Should().Be("json_schema");
        }
        finally
        {
            try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch { }
            TopLevelWindows.KillChildrenOf(host.Id);
        }
    }

    [Theory]
    [InlineData("Neo.App")]
    [InlineData("Neo.App.Avalonia")]
    public async Task Claude_looks_something_up_while_building_and_the_user_reads_what(string hostProject)
    {
        using var api = new StandInClaudeApi();
        api.AnswerWithCodeAfterSearching("widget api docs", PageTitle, PageUrl, WithContent(hostProject, """Show("BUILT AFTER SEARCH");"""));

        using var host = Start(hostProject, api, new { UseAvalonia = hostProject == "Neo.App.Avalonia", ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true, ClaudeWebSearch = true });

        try
        {
            await SendPromptAsync(host, "Build a widget tool.");

            await WaitForTextAsync(host, ChildProcessName(hostProject), t => t == "BUILT AFTER SEARCH", 60, "the tool", api);
            await WaitForTextAsync(host, ChildProcessName(hostProject), t => t.Contains("Searched the web") && t.Contains("widget api docs") && t.Contains(PageUrl), 15, "the report in the chat", api);

            var request = JsonDocument.Parse(api.Requests.Single()).RootElement;
            request.GetProperty("tools")[0].GetProperty("max_uses").GetInt32().Should().Be(3);
            request.GetProperty("system").GetString().Should().Contain("You can search the web while you write the code");
            request.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString().Should().Be("json_schema");
        }
        finally
        {
            try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch { }
            TopLevelWindows.KillChildrenOf(host.Id);
        }
    }

    [Fact]
    public async Task A_second_change_still_patches_the_running_app_in_place()
    {
        // The search helper is compiled into every build now, and that must not cost the hot reload.
        // Patched in place, the running control keeps its identity; reloaded, it gets a new one.
        const string host = "Neo.App.Avalonia";
        const string tool = """
            using System;
            using Avalonia.Controls;
            using Avalonia.Threading;

            public class DynamicUserControl : UserControl
            {
                private readonly string _instance = Guid.NewGuid().ToString("N");
                private readonly TextBlock _text = new TextBlock();

                public DynamicUserControl()
                {
                    Content = _text;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                    timer.Tick += (_, _) => _text.Text = Version() + " " + _instance;
                    timer.Start();
                }

                private string Version() => "FIRST VERSION";
            }
            """;

        using var api = new StandInClaudeApi();
        api.AnswerWithCode(tool);
        api.AnswerWithCode(tool.Replace("FIRST VERSION", "SECOND VERSION"));

        using var process = Start(host, api, new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true });

        try
        {
            await SendPromptAsync(process, "Build it.");
            var first = await WaitForTextAsync(process, ChildProcessName(host), t => t.StartsWith("FIRST VERSION"), 60, "the first version", api);
            await Task.Delay(3000); // hot reload arms itself in the background

            await SendPromptAsync(process, "Change it.");

            var second = await WaitForTextAsync(process, ChildProcessName(host), t => t.StartsWith("SECOND VERSION"), 60, "the second version", api);
            second.Should().EndWith(first.Split(' ').Last(), "a change patches the running app; it does not start it again");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            TopLevelWindows.KillChildrenOf(process.Id);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_tool_is_still_built_when_searching_is_impossible_and_the_user_is_told(bool switchedOff)
    {
        const string host = "Neo.App.Avalonia";
        using var api = new StandInClaudeApi { SearchSwitchedOff = switchedOff, ModelsCannotSearch = !switchedOff };
        api.AnswerWithCode(WithContent(host, """Show("BUILT WITHOUT SEARCH");"""));

        using var process = Start(host, api, new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true, ClaudeWebSearch = true });

        try
        {
            await SendPromptAsync(process, "Build it.");

            await WaitForTextAsync(process, ChildProcessName(host), t => t == "BUILT WITHOUT SEARCH", 60, "the tool", api);
            await WaitForTextAsync(process, ChildProcessName(host), t => t.Contains("Built without it"), 15, "the note in the chat", api);

            var last = JsonDocument.Parse(api.Requests.Last()).RootElement;
            last.TryGetProperty("tools", out _).Should().BeFalse();
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            TopLevelWindows.KillChildrenOf(process.Id);
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
