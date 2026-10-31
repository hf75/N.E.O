using System.Diagnostics;
using System.Windows.Automation;
using FluentAssertions;
using Neo.App;
using Xunit;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// The Avalonia host and its app window are two top-level windows of two processes, but one app to
/// the user: coming back from another program, the one that gets activated brings the other along.
/// The "other program" is a window of this test; nobody else's windows are touched.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class HostWindowOrderSmokeTests : IDisposable
{
    private readonly string _settings = Path.Combine(Path.GetTempPath(), "neo_order_" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task Coming_back_from_another_program_brings_both_windows_along()
    {
        var exe = TestEnvironment.HostExe("Neo.App.Avalonia");
        File.Exists(exe).Should().BeTrue($"the host must be built: {exe}");

        var startInfo = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        startInfo.Environment[SettingsService.FolderEnvironmentVariable] = _settings;
        using var host = Process.Start(startInfo)!;

        using var other = new OtherProgram();
        try
        {
            var neo = await WaitForAsync(() => TopLevelWindows.Find(w => w.Pid == host.Id && w.Title.StartsWith("N.E.O")), 90, "the N.E.O. window");
            var app = await WaitForAsync(() =>
            {
                foreach (var p in Process.GetProcessesByName("Neo.PluginWindowAvalonia"))
                    if (TopLevelWindows.ParentProcessId(p.Id) == host.Id && TopLevelWindows.Find(w => w.Pid == p.Id) is { } window)
                        return window;
                return null;
            }, 60, "the app window");
            await Task.Delay(2000);

            var neoHandle = (IntPtr)neo.Current.NativeWindowHandle;
            var appHandle = (IntPtr)app.Current.NativeWindowHandle;
            var otherWindow = AutomationElement.FromHandle(other.Handle);

            // Back through the N.E.O. window: the app window comes along, the focus stays with N.E.O.
            TopLevelWindows.Foreground(otherWindow);
            await Task.Delay(500);
            TopLevelWindows.IsAbove(other.Handle, appHandle).Should().BeTrue("the other program is in front now");
            TopLevelWindows.Foreground(neo);
            (await WaitForOrderAsync(appHandle, other.Handle)).Should().BeTrue("the app window comes along with N.E.O.");
            TopLevelWindows.IsAbove(neoHandle, other.Handle).Should().BeTrue();
            Native.GetForegroundWindow().Should().Be(neoHandle, "raising the app window must not take the focus");

            // Back through the app window: N.E.O. comes along, the focus stays with the app.
            TopLevelWindows.Foreground(otherWindow);
            await Task.Delay(500);
            TopLevelWindows.IsAbove(other.Handle, neoHandle).Should().BeTrue("the other program is in front now");
            TopLevelWindows.Foreground(app);
            (await WaitForOrderAsync(neoHandle, other.Handle)).Should().BeTrue("N.E.O. comes along with the app window");
            TopLevelWindows.IsAbove(appHandle, other.Handle).Should().BeTrue();
            Native.GetForegroundWindow().Should().Be(appHandle, "raising N.E.O. must not take the focus");
        }
        finally
        {
            try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch { }
            TopLevelWindows.KillChildrenOf(host.Id); // only this host's app window, never another N.E.O.'s
        }
    }

    private static async Task<bool> WaitForOrderAsync(IntPtr upper, IntPtr lower)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 5)
        {
            if (TopLevelWindows.IsAbove(upper, lower)) return true;
            await Task.Delay(100);
        }
        return false;
    }

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
        try { Directory.Delete(_settings, recursive: true); } catch { }
    }

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    }

    /// <summary>A plain window on a thread of its own, standing in for any other program.</summary>
    private sealed class OtherProgram : IDisposable
    {
        private readonly System.Windows.Forms.Form _form = new() { Text = "Another program (N.E.O. test)", Width = 320, Height = 200 };
        private readonly Thread _thread;

        public OtherProgram()
        {
            using var shown = new ManualResetEventSlim();
            _form.Shown += (_, _) => shown.Set();
            _thread = new Thread(() => System.Windows.Forms.Application.Run(_form));
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            shown.Wait(TimeSpan.FromSeconds(10));
            Handle = (IntPtr)_form.Invoke(new Func<IntPtr>(() => _form.Handle));
        }

        public IntPtr Handle { get; }

        public void Dispose()
        {
            try { _form.Invoke(new Action(_form.Close)); } catch { }
            _thread.Join(TimeSpan.FromSeconds(3));
        }
    }
}
