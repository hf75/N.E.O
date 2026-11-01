using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;
using Neo.App;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// A whole prompt in a real host, with only the Anthropic API as a stand-in: what such tests have in common.
/// Input goes out through <see cref="TopLevelWindows"/>, which only sends it to the window in front.
/// </summary>
internal static class RealHost
{
    /// <summary>The code of a tool that runs <paramref name="onLoaded"/> once and has a method Show for its one text.</summary>
    public static string Tool(string hostProject, string onLoaded) => $$"""
        using System.Collections.Generic;
        using {{(hostProject == "Neo.App.Avalonia" ? "Avalonia.Controls" : "System.Windows.Controls")}};
        using Neo.App;

        public class DynamicUserControl : UserControl
        {
            private readonly TextBlock _text = new TextBlock { Text = "working" };
            public DynamicUserControl() { Content = _text; Loaded += async (_, _) => { {{onLoaded}} }; }
            private void Show(string text) => _text.Text = text;
        }
        """;

    public static Process Start(string hostProject, StandInClaudeApi api, string settingsFolder, object settings)
    {
        Directory.CreateDirectory(settingsFolder);
        File.WriteAllText(Path.Combine(settingsFolder, "settings.json"), JsonSerializer.Serialize(settings));

        var startInfo = new ProcessStartInfo(TestEnvironment.HostExe(hostProject)) { UseShellExecute = false };
        startInfo.WorkingDirectory = Path.GetDirectoryName(startInfo.FileName)!;
        startInfo.Environment[SettingsService.FolderEnvironmentVariable] = settingsFolder;
        startInfo.Environment["NEO_ANTHROPIC_BASE_URL"] = api.BaseUrl;
        startInfo.Environment["ANTHROPIC_API_KEY"] = "not-a-real-key";
        startInfo.Environment.Remove("OPENAI_API_KEY");
        startInfo.Environment.Remove("GEMINI_API_KEY");
        return Process.Start(startInfo)!;
    }

    /// <summary>Ends the host and the app window it started, and nobody else's.</summary>
    public static void Stop(Process host)
    {
        try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch { }
        TopLevelWindows.KillChildrenOf(host.Id);
    }

    public static string ChildProcessName(string hostProject) => hostProject == "Neo.App.Avalonia" ? "Neo.PluginWindowAvalonia" : "Neo.PluginWindowWPF";

    public static async Task<AutomationElement> SendPromptAsync(Process host, string prompt)
    {
        var window = await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O")), 60, "the N.E.O. window");
        var box = await WaitForAsync(() => window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "txtPrompt")) is { Current.IsEnabled: true } found ? found : null, 60, "the prompt box");

        TopLevelWindows.Click(window, box);
        ((ValuePattern)box.GetCurrentPattern(ValuePattern.Pattern)).SetValue(prompt);
        await Task.Delay(200);
        TopLevelWindows.Keys(window, "{ENTER}");
        return window;
    }

    public static IEnumerable<string> TextsIn(AutomationElement window) =>
        window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().Select(e => e.Current.Name).Where(n => n.Length > 0);

    /// <summary>A text in the window of the host's app, or in the host itself where the app sits inside it.</summary>
    public static async Task<string> WaitForTextAsync(Process host, string childProcess, Func<string, bool> wanted, int seconds, string what, StandInClaudeApi api)
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

    public static async Task<T> WaitForAsync<T>(Func<T?> probe, int seconds, string what) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            try { if (probe() is { } found) return found; } catch { }
            await Task.Delay(200);
        }
        throw new TimeoutException("not found: " + what);
    }
}
