using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using FluentAssertions;
using Neo.App;
using Xunit;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// The WPF host's full-screen hotkey is global, so a second N.E.O. or any other program can already
/// hold it. That used to stop the start at a dialog, with the prompt box locked until someone clicked it
/// away. Nothing here clicks or types: the test only reads the window.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class TakenHotkeySmokeTests : IDisposable
{
    private readonly string _settings = Path.Combine(Path.GetTempPath(), "neo_taken_hotkey_" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task A_taken_hotkey_does_not_stop_the_start()
    {
        using var taken = new HeldHotkey();

        var startInfo = new ProcessStartInfo(TestEnvironment.HostExe("Neo.App")) { UseShellExecute = false };
        startInfo.WorkingDirectory = Path.GetDirectoryName(startInfo.FileName)!;
        startInfo.Environment[SettingsService.FolderEnvironmentVariable] = _settings;
        startInfo.Environment["ANTHROPIC_API_KEY"] = "not-a-real-key";
        using var host = Process.Start(startInfo)!;

        try
        {
            var window = await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O")), 60, "the N.E.O. window");
            await WaitForAsync(() => window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "txtPrompt")) is { Current.IsEnabled: true } box ? box : null, 60, "the prompt box unlocked");

            var note = await WaitForAsync(() => ChatLine(window, "Ctrl+Shift+F"), 10, "the note in the chat");
            note.Should().Contain("Ctrl+2", "the note says what still works");
        }
        finally
        {
            try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch { }
            TopLevelWindows.KillChildrenOf(host.Id);
        }
    }

    private static string? ChatLine(AutomationElement window, string containing) =>
        window.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
            .SelectMany(e =>
            {
                var texts = new List<string> { e.Current.Name };
                if (e.TryGetCurrentPattern(TextPattern.Pattern, out var text)) texts.Add(((TextPattern)text).DocumentRange.GetText(20000));
                return texts;
            })
            .FirstOrDefault(t => t.Contains(containing, StringComparison.Ordinal));

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

    /// <summary>
    /// Holds Ctrl+Shift+F for the test, on a thread of its own: a hotkey without a window belongs to the
    /// thread that registered it. If someone else holds it already, that is the same situation.
    /// </summary>
    private sealed class HeldHotkey : IDisposable
    {
        private const uint ModControl = 0x0002, ModShift = 0x0004;
        private const int Id = 0x7A01;

        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        public HeldHotkey()
        {
            using var held = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                bool mine = RegisterHotKey(IntPtr.Zero, Id, ModControl | ModShift, 'F');
                held.Set();
                _release.Wait();
                if (mine) UnregisterHotKey(IntPtr.Zero, Id);
            }) { IsBackground = true };
            _thread.Start();
            held.Wait();
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
            _release.Dispose();
        }

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
