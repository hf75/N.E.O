using System.Diagnostics;
using System.Windows.Automation;
using FluentAssertions;
using Neo.App;
using Xunit;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// The share dialog in the real hosts. Its looks come from the styles in App.xaml, and WPF resolves those
/// only when the window is created: a wrong key compiles, then crashes on the first click on Share. This
/// stops short of creating an invitation, which would open the network and, on a new machine, a firewall prompt.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class ShareDialogSmokeTests : IDisposable
{
    private readonly string _settings = Path.Combine(Path.GetTempPath(), "neo_share_dialog_" + Guid.NewGuid().ToString("N")[..8]);

    [Theory]
    [InlineData("Neo.App")]
    [InlineData("Neo.App.Avalonia")]
    public async Task The_dialog_opens_and_says_what_is_missing(string hostProject)
    {
        var startInfo = new ProcessStartInfo(TestEnvironment.HostExe(hostProject)) { UseShellExecute = false };
        startInfo.WorkingDirectory = Path.GetDirectoryName(startInfo.FileName)!;
        startInfo.Environment[SettingsService.FolderEnvironmentVariable] = _settings;
        startInfo.Environment["ANTHROPIC_API_KEY"] = "not-a-real-key";
        using var host = Process.Start(startInfo)!;

        try
        {
            var window = await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O")), 60, "the N.E.O. window");
            await WaitForAsync(() => ById(window, "txtPrompt") is { Current.IsEnabled: true } box ? box : null, 60, "the prompt box unlocked");

            var dialog = await OpenDialogAsync(host, window);

            foreach (var id in new[] { "NameBox", "InviteOut", "BtnCreateInvite", "BtnCopyInvite", "ReplyIn", "BtnConnect", "InviteIn", "BtnJoin", "ReplyOut", "BtnCopyReply" })
                ById(dialog, id).Should().NotBeNull($"the dialog has {id}");
            ById(dialog, "StatusText")!.Current.Name.Should().Be("Not shared.");

            TopLevelWindows.Click(dialog, ById(dialog, "BtnJoin")!);
            await WaitForAsync(() => ById(dialog, "ErrorText") is { Current.Name: "Paste the invitation first." } error ? error : null, 5, "the message about the missing invitation");
        }
        finally
        {
            try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch { }
            TopLevelWindows.KillChildrenOf(host.Id);
        }
    }

    private static async Task<AutomationElement> OpenDialogAsync(Process host, AutomationElement window)
    {
        AutomationElement? Dialog() => TopLevelWindows.Find(w => w.Pid == host.Id && w.Title == "Share session");

        var share = await WaitForAsync(() => ById(window, "BtnShare"), 10, "the share button");
        TopLevelWindows.Click(window, share);
        try
        {
            return await WaitForAsync(Dialog, 8, "the share dialog");
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException && host.HasExited)
        {
            throw new InvalidOperationException($"The host ended instead of opening the dialog, exit code {host.ExitCode}. A wrong style key does that.", ex);
        }
        catch (TimeoutException)
        {
            // On the Avalonia host the click did nothing in two bursts, with the pointer on the button; cause not found yet.
            var r = share.Current.BoundingRectangle;
            var under = AutomationElement.FromPoint(new System.Windows.Point(r.X + r.Width / 2, r.Y + r.Height / 2));
            var said = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>().Select(e => e.Current.Name).Where(n => n.Length > 0);
            var state = $"under the button: process {under.Current.ProcessId} (host {host.Id}), id '{under.Current.AutomationId}'; host has focus: {window.Current.HasKeyboardFocus}; N.E.O. says: {string.Join(" | ", said)}";

            TopLevelWindows.Click(window, share);
            await Task.Delay(3000);
            throw new TimeoutException($"No dialog after the click. A second click opens it: {Dialog() != null}. Before that, {state}");
        }
    }

    private static AutomationElement? ById(AutomationElement root, string id) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

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
