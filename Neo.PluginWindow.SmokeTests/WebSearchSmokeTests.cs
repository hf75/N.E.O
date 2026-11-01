using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;
using FluentAssertions;
using Neo.App;
using Xunit;
using static Neo.PluginWindow.SmokeTests.RealHost;

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

    private const string LaunchRecord = "public record Launch(string Name, int Year);";

    private Process Start(string host, StandInClaudeApi api, object settings) => RealHost.Start(host, api, _settings, settings);

    [Theory]
    [InlineData("Neo.App")]
    [InlineData("Neo.App.Avalonia")]
    public async Task A_generated_app_asks_the_web_and_shows_the_answer_with_its_source(string hostProject)
    {
        using var api = new StandInClaudeApi();
        api.AnswerWithCode(Tool(hostProject, """
            try
            {
                var answer = await AIWebSearch.AskAsync("How is the weather?");
                var data = await AIWebSearch.AskAsync<List<Launch>>("Which launches?");
                Show("ANSWER " + answer.Text + " FROM " + answer.Sources[0].Title + " AT " + answer.Sources[0].Url + " DATA " + data.Data[0].Name + data.Data[0].Year + " SOURCES " + data.Sources.Count);
            }
            catch (System.Exception ex) { Show("ANSWER failed: " + ex); }
            """) + LaunchRecord);
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
        api.AnswerWithCodeAfterSearching("widget api docs", PageTitle, PageUrl, Tool(hostProject, """Show("BUILT AFTER SEARCH");"""));

        using var host = Start(hostProject, api, new { UseAvalonia = hostProject == "Neo.App.Avalonia", ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true, ClaudeWebSearch = true });

        try
        {
            await SendPromptAsync(host, "Build a widget tool.");

            // Accept changes automatically is on, but this code was written after reading the web: it is shown first.
            var review = await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == host.Id && w.Title == "Review Changes"), 60, "the review dialog");
            var report = await WaitForAsync(() => review.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "WebSearchText")), 15, "the report in the dialog");
            report.Current.Name.Should().Contain("Searched the web").And.Contain("widget api docs").And.Contain(PageUrl);
            report.Current.IsOffscreen.Should().BeFalse("whoever decides about the code should read this without opening anything");

            var apply = await WaitForAsync(() => review.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "ApplyButton")), 15, "the Apply button");
            ((InvokePattern)apply.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

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
    public async Task Allowed_to_search_but_no_need_to_it_stays_automatic()
    {
        const string host = "Neo.App.Avalonia";
        using var api = new StandInClaudeApi();
        api.AnswerWithCode(Tool(host, """Show("BUILT WITHOUT A SEARCH");"""));

        using var process = Start(host, api, new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true, ClaudeWebSearch = true });

        try
        {
            await SendPromptAsync(process, "Build it.");

            await WaitForTextAsync(process, ChildProcessName(host), t => t == "BUILT WITHOUT A SEARCH", 60, "the tool, with no dialog in between", api);

            JsonDocument.Parse(api.Requests.Single()).RootElement.GetProperty("tools")[0].GetProperty("name").GetString().Should().Be("web_search");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            TopLevelWindows.KillChildrenOf(process.Id);
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
        api.AnswerWithCode(Tool(host, """Show("BUILT WITHOUT SEARCH");"""));

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

    [Theory]
    [InlineData("Neo.App")]
    [InlineData("Neo.App.Avalonia")]
    public async Task The_settings_dialog_offers_the_model_and_switches_searching_while_building_on(string hostProject)
    {
        using var api = new StandInClaudeApi();
        using var host = Start(hostProject, api, new { UseAvalonia = hostProject == "Neo.App.Avalonia", ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true });

        try
        {
            var window = await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O")), 60, "the N.E.O. window");
            var codeButton = await WaitForAsync(() => window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "BtnCodeEditor")), 30, "the toolbar");
            await WaitForAsync(() => window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "txtPrompt")) is { Current.IsEnabled: true } box ? box : null, 60, "the prompt box");
            var gear = TreeWalker.ControlViewWalker.GetPreviousSibling(codeButton);

            // A click on the Avalonia host right after its start sometimes does nothing, cause unknown
            // (see ShareDialogSmokeTests). A person clicks again, and this test is about the dialog.
            AutomationElement? dialog = null;
            for (int attempt = 0; attempt < 3 && dialog == null; attempt++)
            {
                TopLevelWindows.Click(window, gear);
                try { dialog = await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == host.Id && w.Title == "Settings"), 5, "the settings dialog"); }
                catch (TimeoutException) when (attempt < 2) { }
            }

            // Avalonia's automation fails for a moment right after a window opens.
            await WaitForAsync(() => TextsIn(dialog!).Contains("Web Search Model:") ? "seen" : null, 15, "the model of the web search");

            var check = await WaitForAsync(() => dialog!.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "ClaudeWebSearchCheckBox")), 15, "the check box");
            var toggle = (TogglePattern)check.GetCurrentPattern(TogglePattern.Pattern);
            toggle.Current.ToggleState.Should().Be(ToggleState.Off, "off unless the user wants it");
            toggle.Toggle();
            await Task.Delay(300);

            var ok = dialog.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "OK")));
            ((InvokePattern)ok.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == host.Id && w.Title == "Settings") == null ? "closed" : null, 15, "the dialog to close");

            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(_settings, "settings.json")));
            saved.RootElement.GetProperty("ClaudeWebSearch").GetBoolean().Should().BeTrue();
        }
        finally
        {
            try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch { }
            TopLevelWindows.KillChildrenOf(host.Id);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_settings, recursive: true); } catch { }
    }
}
