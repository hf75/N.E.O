using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using FluentAssertions;
using Xunit;
using static Neo.PluginWindow.SmokeTests.RealHost;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// A running app that cannot be patched gets replaced, and the new version runs in a process of its own: what
/// the old version started besides its window, here one more window and a timer that keeps it up, ends with
/// the old process. The new window opens where the old one was, as large. The real Avalonia host; the model is
/// a stand-in.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class FreshProcessSmokeTests : IDisposable
{
    private const string Host = "Neo.App.Avalonia";
    private const string Leftover = "LEFTOVER OF VERSION 1";

    private readonly string _settings = Path.Combine(Path.GetTempPath(), "neo_fresh_" + Guid.NewGuid().ToString("N")[..8]);

    // Nothing of the next version knows of this window or of the timer.
    private const string Version1 = """
        using System;
        using Avalonia;
        using Avalonia.Controls;
        using Avalonia.Threading;

        public class DynamicUserControl : UserControl
        {
            private readonly Window _extra = new Window
            {
                Title = "LEFTOVER OF VERSION 1", Width = 260, Height = 120, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(40, 40),
            };
            private readonly DispatcherTimer _keepUp = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };

            public DynamicUserControl()
            {
                Content = new TextBlock { Text = "VERSION 1", Margin = new Thickness(20, 50, 20, 20) };
                _keepUp.Tick += (_, _) => { if (!_extra.IsVisible) _extra.Show(); };
                _keepUp.Start();
            }
        }
        """;

    // A new interface cannot be patched in: the app is replaced.
    private const string Version2 = """
        using System;
        using Avalonia;
        using Avalonia.Controls;

        public class DynamicUserControl : UserControl, IDisposable
        {
            public DynamicUserControl() => Content = new TextBlock { Text = "VERSION 2", Margin = new Thickness(20, 50, 20, 20) };
            public void Dispose() { }
        }
        """;

    private static object Settings => new { UseAvalonia = true, ClaudeModel = "stand-in", ClaudeEffort = "low", AcceptAutomatic = true };

    [Fact]
    public async Task A_replaced_app_takes_what_it_started_along_and_the_new_one_opens_where_it_was()
    {
        using var api = new StandInClaudeApi();
        api.AnswerWithCode(Version1);
        api.AnswerWithCode(Version2);

        using var host = Start(Host, api, _settings, Settings);
        try
        {
            await SendPromptAsync(host, "Build it.");
            await WaitForAppTextAsync(host, "VERSION 1", 60, "the first version");
            var oldProcess = (await WaitForAsync(() => TopLevelWindows.Find(w => w.Title == Leftover), 10, "the window of its own")).Current.ProcessId;
            await Task.Delay(3000); // hot reload arms itself in the background, and N.E.O. docks its app once more after a prompt

            // The user made the app window wider than it opens.
            var oldWindow = (IntPtr)AppWindowOf(oldProcess)!.Current.NativeWindowHandle;
            GetWindowRect(oldWindow, out var r);
            MoveWindow(oldWindow, r.L, r.T, 720, r.B - r.T, true);
            await Task.Delay(800);
            GetWindowRect(oldWindow, out var before);

            await SendPromptAsync(host, "Change it.");
            await WaitForTextAsync(host, "no such process", t => t.StartsWith("Hot reload not possible"), 60, "the note that the app is replaced", api);
            await WaitForAsync(() => HasEnded(oldProcess) ? "ended" : null, 15, "the end of the old process");
            await WaitForAppTextAsync(host, "VERSION 2", 30, "the new version");

            // The window of its own went with the old process, and so did the timer that would have shown it again.
            var children = Process.GetProcessesByName(ChildProcessName(Host)).Where(p => TopLevelWindows.ParentProcessId(p.Id) == host.Id).Select(p => p.Id).ToList();
            children.Should().ContainSingle("the new version runs in a process of its own").Which.Should().NotBe(oldProcess);
            await Task.Delay(1000);
            TopLevelWindows.Find(w => w.Title == Leftover).Should().BeNull("nothing of the new version could have closed it");

            // Where it was, as large as it was.
            GetWindowRect((IntPtr)AppWindowOf(children[0])!.Current.NativeWindowHandle, out var after);
            (after.R - after.L).Should().BeCloseTo(before.R - before.L, 2, "the width the user gave it");
            after.L.Should().BeCloseTo(before.L, 2);
            after.T.Should().BeCloseTo(before.T, 2);
        }
        finally
        {
            Stop(host);
        }
    }

    private static AutomationElement? AppWindowOf(int processId) => TopLevelWindows.Find(w => w.Pid == processId && w.Title != Leftover);

    // In the app window: the window of its own belongs to the same process, and either may come first.
    private static Task<string> WaitForAppTextAsync(Process host, string text, int seconds, string what) =>
        WaitForAsync(() => Process.GetProcessesByName(ChildProcessName(Host))
            .Where(p => TopLevelWindows.ParentProcessId(p.Id) == host.Id)
            .Select(p => AppWindowOf(p.Id))
            .Where(w => w != null)
            .SelectMany(w => TextsIn(w!))
            .FirstOrDefault(t => t == text), seconds, what);

    private static bool HasEnded(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_settings, recursive: true); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);
}
