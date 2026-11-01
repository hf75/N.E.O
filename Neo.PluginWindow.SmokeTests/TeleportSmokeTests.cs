using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using FluentAssertions;
using Neo.App;
using Newtonsoft.Json;
using Xunit;
using static Neo.PluginWindow.SmokeTests.RealHost;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// The running app goes from one N.E.O. to another and back: two real hosts on one PC, a real shared session set
/// up through the real dialog, over real WebRTC. This opens the network, so on a new machine the firewall may ask.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class TeleportSmokeTests : IDisposable
{
    private readonly string _settingsA = Path.Combine(Path.GetTempPath(), "neo_teleport_a_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _settingsB = Path.Combine(Path.GetTempPath(), "neo_teleport_b_" + Guid.NewGuid().ToString("N")[..8]);

    public static TheoryData<string> Hosts => new() { "Neo.App.Avalonia", "Neo.App" };

    private static string Counter(string hostProject) => $$"""
        using {{(hostProject == "Neo.App.Avalonia" ? "Avalonia.Controls" : "System.Windows.Controls")}};

        public class DynamicUserControl : UserControl
        {
            private int _count;
            private string _note = "";

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                var add = new Button { Content = "Count" };
                add.Click += (_, _) => { _count++; BuildUi(); };
                var box = new TextBox { Name = "note", Text = _note };
                box.TextChanged += (_, _) => _note = box.Text ?? "";
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = "COUNT " + _count });
                panel.Children.Add(box);
                panel.Children.Add(add);
                Content = panel;
            }
        }
        """;

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task The_running_app_goes_to_the_other_NEO_and_comes_home(string hostProject)
    {
        using var apiA = new StandInClaudeApi();
        using var apiB = new StandInClaudeApi();
        apiA.AnswerWithCode(Counter(hostProject));
        bool avalonia = hostProject == "Neo.App.Avalonia";

        using var hostA = Start(hostProject, apiA, _settingsA, new { UseAvalonia = avalonia, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true, SessionName = "Anna" });
        using var hostB = Start(hostProject, apiB, _settingsB, new { UseAvalonia = avalonia, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true, SessionName = "Ben" });
        var child = ChildProcessName(hostProject);
        try
        {
            await SendPromptAsync(hostA, "Build it.");
            await WaitForTextAsync(hostA, child, t => t == "COUNT 0", 60, "the app at Anna's", apiA);
            await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == hostB.Id && w.Title.StartsWith("N.E.O")), 60, "Ben's N.E.O.");

            // One above the other, each with its app beside it or in it.
            int width = avalonia ? 640 : 1000;
            MoveWindow(Handle(Neo(hostA)), 20, 20, width, 490, true);
            MoveWindow(Handle(Neo(hostB)), 20, 520, width, 490, true);
            await Task.Delay(500);

            // The session, through the dialog, as two people do it.
            var dialogA = await OpenShareDialogAsync(hostA);
            MoveAside(dialogA, 20);
            TopLevelWindows.Click(dialogA, ById(dialogA, "BtnCreateInvite")!);
            var invite = await WaitForAsync(() => ValueOf(dialogA, "InviteOut") is { Length: > 0 } code ? code : null, 30, "the invitation");

            var dialogB = await OpenShareDialogAsync(hostB);
            MoveAside(dialogB, 40);
            SetValue(dialogB, "InviteIn", invite);
            TopLevelWindows.Click(dialogB, ById(dialogB, "BtnJoin")!);
            var reply = await WaitForAsync(() => ValueOf(dialogB, "ReplyOut") is { Length: > 0 } code ? code : null, 30, "the reply");

            SetValue(dialogA, "ReplyIn", reply);
            TopLevelWindows.Click(dialogA, ById(dialogA, "BtnConnect")!);
            await WaitForTextAsync(hostB, child, t => t == "COUNT 0", 60, "Anna's app at Ben's", apiB);
            await Task.Delay(3000); // hot reload arms itself in the background

            // Anna works: this is only in her copy.
            var appA = await AppAsync(hostA, avalonia);
            TopLevelWindows.Click(appA, ButtonIn(appA, "Count"));
            await WaitForTextAsync(hostA, child, t => t == "COUNT 1", 10, "a count at Anna's", apiA);
            appA = await AppAsync(hostA, avalonia);
            TopLevelWindows.Click(appA, ButtonIn(appA, "Count"));
            await WaitForTextAsync(hostA, child, t => t == "COUNT 2", 10, "another count at Anna's", apiA);
            appA = await AppAsync(hostA, avalonia);
            SetValue(appA, "note", "half a thought");
            await Task.Delay(200);

            // She sends it. Ben is asked, and nothing has moved yet.
            await WaitForAsync(() => ById(dialogA, "BtnTeleport") is { Current.IsOffscreen: false } button ? button : null, 15, "the teleport button");
            TopLevelWindows.Click(dialogA, ById(dialogA, "BtnTeleport")!);
            var question = await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == hostB.Id && w.Title == "Take the running app?"), 20, "the question at Ben's");
            TextsIn(question).Should().Contain(t => t.Contains("Anna wants to send you the running app"));
            AppOf(hostA, avalonia).Should().NotBeNull("nothing leaves before he says yes");
            TopLevelWindows.Click(question, ById(question, "BtnYes")!);

            // Her window goes. His app is the one she left.
            await WaitForAsync(() => AppOf(hostA, avalonia) == null ? "gone" : null, 20, "Anna's app going away");
            await WaitForTextAsync(hostB, child, t => t == "COUNT 2", 20, "the count at Ben's", apiB);
            var appB = await AppAsync(hostB, avalonia);
            await WaitForAsync(() => ValueOf(appB, "note") == "half a thought" ? "there" : null, 10, "the text she was typing");
            await WaitForTextAsync(hostB, child, t => t.Contains("The app came from Anna"), 10, "the line over the app", apiB);
            await WaitForTextAsync(hostA, "no such process", t => t.Contains("The app is at Ben's now"), 10, "the chat at Anna's", apiA);
            await WaitForAsync(() => ValueOf(dialogA, "BtnTeleport") == null && ById(dialogA, "BtnTeleport") is { Current.Name: "Show my copy" } ? "ok" : null, 10, "the button to get her copy back");

            // Coming to the front does not bring her window back.
            TopLevelWindows.Foreground(Neo(hostA));
            await Task.Delay(1000);
            AppOf(hostA, avalonia).Should().BeNull();

            // A prompt of hers changes the app, wherever it is: his running copy gets it and keeps what it holds, her window stays away.
            apiA.AnswerWithCode(Counter(hostProject).Replace("panel.Children.Add(add);", "panel.Children.Add(add);\n                panel.Children.Add(new TextBlock { Text = \"VERSION 2\" });"));
            await SendPromptAsync(hostA, "Add a version line.");
            await WaitForTextAsync(hostB, child, t => t == "VERSION 2", 60, "her change in the app at Ben's", apiB);
            await WaitForTextAsync(hostB, child, t => t == "COUNT 2", 5, "the count still at Ben's", apiB);
            await Task.Delay(1500);
            AppOf(hostA, avalonia).Should().BeNull("the app is not back, only changed");
            appB = await AppAsync(hostB, avalonia);

            // Ben goes on with it, and sends it home. Nobody asks Anna, it is hers.
            TopLevelWindows.Click(appB, ButtonIn(appB, "Count"));
            await WaitForTextAsync(hostB, child, t => t == "COUNT 3", 10, "a count at Ben's", apiB);
            await WaitForAsync(() => ById(dialogB, "BtnTeleport") is { Current.IsOffscreen: false } button ? button : null, 15, "the teleport button at Ben's");
            TopLevelWindows.Click(dialogB, ById(dialogB, "BtnTeleport")!);
            await WaitForTextAsync(hostA, child, t => t == "COUNT 3", 20, "what Ben did, at Anna's", apiA);
            await WaitForAsync(() => AppOf(hostB, avalonia) == null ? "gone" : null, 20, "Ben's app going away");
            TopLevelWindows.Find(w => w.Pid == hostA.Id && w.Title == "Take the running app?").Should().BeNull();

            // She goes on where he stopped, in the same running app she had.
            appA = await AppAsync(hostA, avalonia);
            TopLevelWindows.Click(appA, ButtonIn(appA, "Count"));
            await WaitForTextAsync(hostA, child, t => t == "COUNT 4", 10, "a count at Anna's again", apiA);
        }
        finally
        {
            Stop(hostA);
            Stop(hostB);
        }
    }

    // Under WPF the app sits inside the N.E.O. window, and a dialog of that window stays above it.
    private static void MoveAside(AutomationElement dialog, int y) => SetWindowPos(Handle(dialog), IntPtr.Zero, 1040, y, 0, 0, 0x0001 /* NOSIZE */ | 0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */);

    [Fact]
    public async Task A_living_app_goes_away_and_its_user_is_not_left_with_an_empty_screen()
    {
        const string Host = "Neo.App.Avalonia";
        Directory.CreateDirectory(_settingsA);
        var project = Path.Combine(_settingsA, "Counter.resx");
        File.WriteAllText(project, JsonConvert.SerializeObject(new ResxData { Version = 2, Code = Counter(Host), Nuget = new(), History = "" }));
        File.WriteAllText(Path.Combine(_settingsA, "settings.json"), System.Text.Json.JsonSerializer.Serialize(new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true, SessionName = "Anna" }));

        using var apiA = new StandInClaudeApi();
        using var apiB = new StandInClaudeApi();
        var exe = TestEnvironment.HostExe(Host);
        var living = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false, Arguments = $"--play \"{project}\"" };
        living.Environment[SettingsService.FolderEnvironmentVariable] = _settingsA;
        living.Environment["NEO_ANTHROPIC_BASE_URL"] = apiA.BaseUrl;
        living.Environment["ANTHROPIC_API_KEY"] = "not-a-real-key";
        using var hostA = Process.Start(living)!;
        using var hostB = Start(Host, apiB, _settingsB, new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true, SessionName = "Ben" });
        var child = ChildProcessName(Host);
        try
        {
            var app = await WaitForAsync(() => AppOf(hostA, true) is { } w && TextsIn(w).Contains("COUNT 0") ? w : null, 90, "the living app");
            await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == hostB.Id && w.Title.StartsWith("N.E.O")), 60, "Ben's N.E.O.");
            await Task.Delay(3000); // hot reload arms itself in the background
            MoveWindow(Handle(app), 700, 20, 500, 400, true);
            MoveWindow(Handle(Neo(hostB)), 20, 520, 640, 490, true);
            await Task.Delay(400);

            // Its user calls N.E.O. up and shares the session.
            ById(app, "note")!.SetFocus();
            await Task.Delay(300);
            TopLevelWindows.Keys(app, "^+n");
            await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == hostA.Id && w.Title.StartsWith("N.E.O")), 10, "N.E.O. after Ctrl+Shift+N");

            var dialogA = await OpenShareDialogAsync(hostA);
            MoveAside(dialogA, 20);
            TopLevelWindows.Click(dialogA, ById(dialogA, "BtnCreateInvite")!);
            var invite = await WaitForAsync(() => ValueOf(dialogA, "InviteOut") is { Length: > 0 } code ? code : null, 30, "the invitation");
            var dialogB = await OpenShareDialogAsync(hostB);
            MoveAside(dialogB, 40);
            SetValue(dialogB, "InviteIn", invite);
            TopLevelWindows.Click(dialogB, ById(dialogB, "BtnJoin")!);
            var reply = await WaitForAsync(() => ValueOf(dialogB, "ReplyOut") is { Length: > 0 } code ? code : null, 30, "the reply");
            SetValue(dialogA, "ReplyIn", reply);
            TopLevelWindows.Click(dialogA, ById(dialogA, "BtnConnect")!);
            await WaitForTextAsync(hostB, child, t => t == "COUNT 0", 60, "the living app at Ben's", apiB);
            await Task.Delay(3000);

            app = await AppAsync(hostA, true);
            TopLevelWindows.Click(app, ButtonIn(app, "Count"));
            await WaitForTextAsync(hostA, child, t => t == "COUNT 1", 10, "a count in the living app", apiA);

            // It goes to Ben. Her N.E.O. is what she has left on the screen.
            await WaitForAsync(() => ById(dialogA, "BtnTeleport") is { Current.IsOffscreen: false } button ? button : null, 15, "the teleport button");
            TopLevelWindows.Click(dialogA, ById(dialogA, "BtnTeleport")!);
            var question = await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == hostB.Id && w.Title == "Take the running app?"), 20, "the question at Ben's");
            TopLevelWindows.Click(question, ById(question, "BtnYes")!);
            await WaitForAsync(() => AppOf(hostA, true) == null ? "gone" : null, 20, "the living app going away");
            await WaitForTextAsync(hostB, child, t => t == "COUNT 1", 20, "the count at Ben's", apiB);
            await Task.Delay(1000);
            AppOf(hostA, true).Should().BeNull();
            hostA.HasExited.Should().BeFalse("the living app waits for it");

            // She closes that too: her copy comes back, or there would be nothing on the screen and a process nobody can reach.
            TopLevelWindows.Keys(Neo(hostA), "%{F4}");
            await WaitForAsync(() => AppOf(hostA, true), 15, "her copy coming back");
            await WaitForTextAsync(hostA, child, t => t == "COUNT 1", 10, "her copy as it was", apiA);
            await WaitForTextAsync(hostA, child, t => t.Contains("Your copy of the app is back"), 10, "the line over it", apiA);
            hostA.HasExited.Should().BeFalse();
        }
        finally
        {
            Stop(hostA);
            Stop(hostB);
        }
    }

    private static AutomationElement Neo(Process host) => TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O"))!;

    private static IntPtr Handle(AutomationElement window) => (IntPtr)window.Current.NativeWindowHandle;

    // The app of a host where a person sees it: a window of its own under Avalonia, inside N.E.O. under WPF.
    private static AutomationElement? AppOf(Process host, bool avalonia)
    {
        var windows = Process.GetProcessesByName(ChildProcessName(avalonia ? "Neo.App.Avalonia" : "Neo.App"))
            .Where(p => TopLevelWindows.ParentProcessId(p.Id) == host.Id)
            .Select(p => TopLevelWindows.Find(w => w.Pid == p.Id));
        return windows.FirstOrDefault(w => w != null);
    }

    private static Task<AutomationElement> AppAsync(Process host, bool avalonia) => WaitForAsync(() => AppOf(host, avalonia), 15, "the app window");

    private static AutomationElement ButtonIn(AutomationElement window, string name) =>
        window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().First(b => b.Current.Name == name);

    private static AutomationElement? ById(AutomationElement root, string id) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static string? ValueOf(AutomationElement root, string id) =>
        ById(root, id) is { } element && element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? ((ValuePattern)pattern).Current.Value : null;

    private static void SetValue(AutomationElement root, string id, string value) =>
        ((ValuePattern)ById(root, id)!.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);

    private static async Task<AutomationElement> OpenShareDialogAsync(Process host)
    {
        var window = Neo(host);
        var share = await WaitForAsync(() => ById(window, "BtnShare"), 10, "the share button");
        for (int attempt = 0; ; attempt++)
        {
            TopLevelWindows.Click(window, share);
            try { return await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == host.Id && w.Title == "Share session"), 5, "the share dialog"); }
            catch (TimeoutException) when (attempt == 0) { /* the click that goes nowhere now and then, see ShareDialogSmokeTests */ }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_settingsA, recursive: true); } catch { }
        try { Directory.Delete(_settingsB, recursive: true); } catch { }
    }

    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
