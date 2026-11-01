using FluentAssertions;
using Neo.IPC;
using Neo.Shared;
using Xunit;
using static Neo.PluginWindow.SmokeTests.DesktopHotReloadSmokeTests;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// The state of a running app, taken from a real plugin window and put into another one, on both toolkits:
/// what fusion and teleport are built on. The second window never saw what happened in the first.
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class AppStateSmokeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "neo_smoke_state_" + Guid.NewGuid().ToString("N")[..8]);

    private const string Avalonia = """
        using System.Collections.Generic;
        using Avalonia;
        using Avalonia.Controls;

        public class DynamicUserControl : UserControl
        {
            private readonly List<string> _rows = new();
            private string _blob = "";
            private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                var entry = new TextBox { Name = "entry" };
                entry.TextChanged += (_, _) =>
                {
                    if (string.IsNullOrEmpty(entry.Text)) return;
                    _rows.Clear();
                    _rows.Add(entry.Text);
                    _blob = new string(entry.Text[0], 3000000);
                };
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Name = "summary", Text = "rows=" + string.Join(",", _rows) + " blob=" + _blob.Length },
                        entry,
                        new TextBox { Name = "draft" },
                        new CheckBox { Name = "done", Content = "Done" },
                    }
                };
            }
        }
        """;

    private const string Wpf = """
        using System.Collections.Generic;
        using System.Windows;
        using System.Windows.Controls;

        public class DynamicUserControl : UserControl
        {
            private readonly List<string> _rows = new();
            private string _blob = "";
            private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                var entry = new TextBox { Name = "entry" };
                entry.TextChanged += (_, _) =>
                {
                    if (string.IsNullOrEmpty(entry.Text)) return;
                    _rows.Clear();
                    _rows.Add(entry.Text);
                    _blob = new string(entry.Text[0], 3000000);
                };
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Name = "summary", Text = "rows=" + string.Join(",", _rows) + " blob=" + _blob.Length });
                panel.Children.Add(entry);
                panel.Children.Add(new TextBox { Name = "draft" });
                panel.Children.Add(new CheckBox { Name = "done", Content = "Done" });
                Content = panel;
            }
        }
        """;

    public static TheoryData<string, string> DesktopHeads => new()
    {
        { "Neo.PluginWindowAvalonia", "--standalone" },
        { "Neo.PluginWindowWPF", "" },
    };

    private static readonly Dictionary<string, string> HotReloadOn = new() { [HotReloadRuntime.EnvironmentVariable] = HotReloadRuntime.EnvironmentValue };

    [Theory]
    [MemberData(nameof(DesktopHeads))]
    public async Task What_a_user_did_in_one_window_arrives_in_another(string head, string launchArgs)
    {
        bool wpf = head.Contains("WPF", StringComparison.Ordinal);
        var dllPath = Path.Combine(_directory, "DynamicUserControl.dll");
        await CompileAsync(wpf ? Wpf : Avalonia, dllPath, wpf);

        await using var first = await ChildSession.StartAsync(TestEnvironment.HeadExe(head), launchArgs, HotReloadOn);
        await first.LoadAsync(dllPath);
        (await first.WaitForLoadsAsync(1)).Should().BeTrue(string.Join(" | ", first.Errors));
        (await first.SetPropertyAsync("entry", "Text", "apple"))!.Success.Should().BeTrue();
        (await first.SetPropertyAsync("draft", "Text", "half a sentence"))!.Success.Should().BeTrue();
        (await first.SetPropertyAsync("done", "IsChecked", "true"))!.Success.Should().BeTrue();

        var capture = await first.RequestAsync(IpcTypes.CaptureAppState, "{}");
        capture!.Type.Should().Be(IpcTypes.AppStateCaptured);
        var captured = Json.FromJson<AppStateCapturedMessage>(capture.PayloadJson)!;
        captured.Image.Should().NotBeNull();
        captured.Values.Should().Be(2, "the rows and the blob");
        captured.Bytes.Should().BeGreaterThan(3000000);
        captured.Left.Should().ContainSingle().Which.Should().StartWith("_clock", "a stopwatch belongs to the process");

        await using var second = await ChildSession.StartAsync(TestEnvironment.HeadExe(head), launchArgs, HotReloadOn);
        await second.LoadAsync(dllPath);
        (await second.WaitForLoadsAsync(1)).Should().BeTrue(string.Join(" | ", second.Errors));
        Property(await second.InspectVisualTreeAsync(), "summary", "Text").Should().Be("rows= blob=0");

        var reply = await second.RequestAsync(IpcTypes.RestoreAppState, Json.ToJson(new RestoreAppStateRequest(captured.Image!, ["RebuildUi", "BuildUi"])));
        reply!.Type.Should().Be(IpcTypes.AppStateRestored);
        var restored = Json.FromJson<AppStateRestoredMessage>(reply.PayloadJson)!;
        restored.Success.Should().BeTrue(restored.Error);
        restored.Values.Should().Be(2);
        restored.Failed.Should().BeEmpty();
        restored.UiValues.Should().BeGreaterThanOrEqualTo(3);

        var tree = await second.InspectVisualTreeAsync();
        Property(tree, "summary", "Text").Should().Be("rows=apple blob=3000000", "the fields arrived, and the UI was built again to show them");
        Property(tree, "entry", "Text").Should().Be("apple");
        Property(tree, "draft", "Text").Should().Be("half a sentence", "this only ever existed in a control");
        Property(tree, "done", "IsChecked").Should().Be("True");
        second.Errors.Should().BeEmpty();
        second.ChildIsAlive.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(DesktopHeads))]
    public async Task Without_the_ui_state_only_the_fields_arrive(string head, string launchArgs)
    {
        bool wpf = head.Contains("WPF", StringComparison.Ordinal);
        var dllPath = Path.Combine(_directory, "DynamicUserControl.dll");
        await CompileAsync(wpf ? Wpf : Avalonia, dllPath, wpf);

        await using var first = await ChildSession.StartAsync(TestEnvironment.HeadExe(head), launchArgs, HotReloadOn);
        await first.LoadAsync(dllPath);
        (await first.WaitForLoadsAsync(1)).Should().BeTrue(string.Join(" | ", first.Errors));
        (await first.SetPropertyAsync("entry", "Text", "apple"))!.Success.Should().BeTrue();
        (await first.SetPropertyAsync("draft", "Text", "half a sentence"))!.Success.Should().BeTrue();
        var captured = Json.FromJson<AppStateCapturedMessage>((await first.RequestAsync(IpcTypes.CaptureAppState, "{}"))!.PayloadJson)!;

        await using var second = await ChildSession.StartAsync(TestEnvironment.HeadExe(head), launchArgs, HotReloadOn);
        await second.LoadAsync(dllPath);
        (await second.WaitForLoadsAsync(1)).Should().BeTrue(string.Join(" | ", second.Errors));

        var reply = await second.RequestAsync(IpcTypes.RestoreAppState, Json.ToJson(new RestoreAppStateRequest(captured.Image!, ["BuildUi"], RestoreUi: false)));
        Json.FromJson<AppStateRestoredMessage>(reply!.PayloadJson)!.Values.Should().Be(2);

        var tree = await second.InspectVisualTreeAsync();
        Property(tree, "summary", "Text").Should().Be("rows=apple blob=3000000");
        (Property(tree, "draft", "Text") ?? "").Should().BeEmpty("the other program's controls are no business of this one");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
