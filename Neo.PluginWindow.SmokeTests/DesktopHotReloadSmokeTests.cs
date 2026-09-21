using System.Runtime.InteropServices;
using FluentAssertions;
using Neo.Agents;
using Neo.HotReload;
using Neo.Shared;
using Xunit;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>
/// Hot reload the way the desktop hosts do it (<c>AppController.DisplayCompiledDllAsync</c>): the
/// slim plugin window heads they ship, started with edit-and-continue switched on, every build
/// compiled by the real agent to the same path, <see cref="HotReloadCoordinator"/> deciding
/// between patching the running app and replacing it.
///
/// <para>The acceptance test for the problem hot reload was built for: the user has worked with
/// the app (typed a filter, ticked a box — state the app keeps in a field and state that only
/// lives in the controls), then asks for a change. Afterwards all of it is still there, and the
/// change is live.</para>
/// </summary>
[Collection("PluginWindowProcesses")]
[Trait("Category", "UiSmoke")]
public class DesktopHotReloadSmokeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "neo_smoke_hotreload_" + Guid.NewGuid().ToString("N")[..8]);

    private const string AvaloniaV1 = """
        using Avalonia;
        using Avalonia.Controls;

        public class DynamicUserControl : UserControl
        {
            private string _lastFilter = "";   // state the app keeps

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                var filter = new TextBox { Name = "filter" };
                filter.TextChanged += (_, _) => _lastFilter = filter.Text ?? "";
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Children =
                    {
                        new TextBlock { Name = "title", Text = "Viewer v1 [" + _lastFilter + "]" },
                        filter,
                        new CheckBox { Name = "headers", Content = "First row has headers" },
                    }
                };
            }
        }
        """;

    private const string WpfV1 = """
        using System.Windows;
        using System.Windows.Controls;

        public class DynamicUserControl : UserControl
        {
            private string _lastFilter = "";   // state the app keeps

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                var filter = new TextBox { Name = "filter" };
                filter.TextChanged += (_, _) => _lastFilter = filter.Text;
                var panel = new StackPanel { Margin = new Thickness(16) };
                panel.Children.Add(new TextBlock { Name = "title", Text = "Viewer v1 [" + _lastFilter + "]" });
                panel.Children.Add(filter);
                panel.Children.Add(new CheckBox { Name = "headers", Content = "First row has headers" });
                Content = panel;
            }
        }
        """;

    // The next prompt: a new field with an initializer, and a UI that shows more.
    private static string NextVersion(string v1) => v1
        .Replace("private string _lastFilter = \"\";",
                 "private string _lastFilter = \"\";\n    private readonly System.Collections.Generic.List<int> _added = new() { 1, 2, 3 };")
        .Replace("\"Viewer v1 [\" + _lastFilter + \"]\"", "\"Viewer v2 [\" + _lastFilter + \"] added=\" + _added.Count");

    // Head as the respective desktop host starts it.
    public static TheoryData<string, string> DesktopHeads => new()
    {
        { "Neo.PluginWindowAvalonia", "--standalone" },  // under Neo.App.Avalonia
        { "Neo.PluginWindowWPF", "" },                   // embedded under Neo.App
    };

    [Theory]
    [MemberData(nameof(DesktopHeads))]
    public async Task Refining_a_running_app_keeps_what_the_user_did(string head, string launchArgs)
    {
        bool wpf = head.Contains("WPF", StringComparison.Ordinal);
        var dllPath = Path.Combine(_directory, "DynamicUserControl.dll");
        var v1 = wpf ? WpfV1 : AvaloniaV1;

        await using var child = await ChildSession.StartAsync(TestEnvironment.HeadExe(head), launchArgs,
            new Dictionary<string, string> { [HotReloadRuntime.EnvironmentVariable] = HotReloadRuntime.EnvironmentValue });
        child.HotReloadCapabilities.Should().NotBeNullOrEmpty("a head started with edit-and-continue announces what it can patch");

        using var hotReload = new HotReloadCoordinator();

        // First version: a full load, then arm hot reload — as DisplayCompiledDllAsync does.
        await CompileAsync(v1, dllPath, wpf);
        await child.LoadAsync(dllPath);
        (await child.WaitForLoadsAsync(1)).Should().BeTrue(string.Join(" | ", child.Errors));
        (await hotReload.StartAsync(dllPath, child.HotReloadCapabilities)).Should().BeNull();

        // The user works with the app.
        (await child.SetPropertyAsync("filter", "Text", "typed by the user"))!.Success.Should().BeTrue();
        (await child.SetPropertyAsync("headers", "IsChecked", "true"))!.Success.Should().BeTrue();

        // The next prompt, compiled over the running build's file like the hosts do.
        await CompileAsync(NextVersion(v1), dllPath, wpf);
        var attempt = await hotReload.TryAsync(dllPath, child.HotReloadAsync);

        attempt.Applied.Should().BeTrue(attempt.Message);
        attempt.Message.Should().Contain("UI rebuilt");
        child.LoadExecutedCount.Should().Be(1, "the app was patched, not loaded again");

        var tree = await child.InspectVisualTreeAsync();
        Property(tree, "title", "Text").Should().Be("Viewer v2 [typed by the user] added=3",
            "new UI code, the field the old handler wrote, and the new field initialised on the running instance");
        Property(tree, "filter", "Text").Should().Be("typed by the user", "the text in the box only existed in the old control");
        Property(tree, "headers", "IsChecked").Should().Be("True", "the ticked box only existed in the old control");

        await Task.Delay(300);
        child.Errors.Should().BeEmpty();
        child.ChildIsAlive.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(DesktopHeads))]
    public async Task Controls_that_get_a_name_in_the_next_version_keep_their_state(string head, string launchArgs)
    {
        // A box the user typed into has no name in one version and gets one in the next (the model
        // names what it wants to address). The UI is rebuilt; its state is found again by position.
        // The versions are compiled to the same path, as the desktop hosts do — which also covers the
        // reported "file is being used by another process" on the compile after a hot reload.
        bool wpf = head.Contains("WPF", StringComparison.Ordinal);
        var dllPath = Path.Combine(_directory, "DynamicUserControl.dll");
        var v1 = wpf ? WpfNotesV1 : AvaloniaNotesV1;

        await using var child = await ChildSession.StartAsync(TestEnvironment.HeadExe(head), launchArgs,
            new Dictionary<string, string> { [HotReloadRuntime.EnvironmentVariable] = HotReloadRuntime.EnvironmentValue });
        using var hotReload = new HotReloadCoordinator();

        await CompileAsync(v1, dllPath, wpf);
        await child.LoadAsync(dllPath);
        (await child.WaitForLoadsAsync(1)).Should().BeTrue(string.Join(" | ", child.Errors));
        (await hotReload.StartAsync(dllPath, child.HotReloadCapabilities)).Should().BeNull();

        (await child.SetPropertyAsync("TextBox:0", "Text", "typed by the user"))!.Success.Should().BeTrue();

        // Two prompts in a row: a new title, then names for the controls.
        var v2 = v1.Replace("\"Notes v1\"", "\"Notes v2\"");
        await CompileAsync(v2, dllPath, wpf);
        (await hotReload.TryAsync(dllPath, child.HotReloadAsync)).Applied.Should().BeTrue();

        var v3 = v2.Replace("new TextBlock {", "new TextBlock { Name = \"title\",").Replace("new TextBox()", "new TextBox { Name = \"notes\" }");
        await CompileAsync(v3, dllPath, wpf);
        var attempt = await hotReload.TryAsync(dllPath, child.HotReloadAsync);

        attempt.Applied.Should().BeTrue(attempt.Message);
        child.LoadExecutedCount.Should().Be(1, "neither change replaced the app");
        var tree = await child.InspectVisualTreeAsync();
        Property(tree, "title", "Text").Should().Be("Notes v2");
        Property(tree, "notes", "Text").Should().Be("typed by the user",
            "the box had no name when the user typed; it is found again by its position");

        await Task.Delay(300);
        child.Errors.Should().BeEmpty();
        child.ChildIsAlive.Should().BeTrue();
    }

    private const string AvaloniaNotesV1 = """
        // The shape of the desktop base code: namespace Neo.App.
        using System;
        using Avalonia;
        using Avalonia.Controls;

        namespace Neo.App
        {
            public class DynamicUserControl : UserControl
            {
                public DynamicUserControl() { BuildUi(); }
                private void BuildUi()
                {
                    var panel = new StackPanel { Margin = new Thickness(16) };
                    panel.Children.Add(new TextBlock { Text = "Notes v1" });
                    panel.Children.Add(new TextBox());
                    Content = panel;
                }
            }
        }
        """;

    private const string WpfNotesV1 = """
        // The shape of the desktop base code: namespace Neo.App.
        using System;
        using System.Windows;
        using System.Windows.Controls;

        namespace Neo.App
        {
            public class DynamicUserControl : UserControl
            {
                public DynamicUserControl() { BuildUi(); }
                private void BuildUi()
                {
                    var panel = new StackPanel { Margin = new Thickness(16) };
                    panel.Children.Add(new TextBlock { Text = "Notes v1" });
                    panel.Children.Add(new TextBox());
                    Content = panel;
                }
            }
        }
        """;

    [Theory]
    [MemberData(nameof(DesktopHeads))]
    public async Task Handlers_the_rebuilt_ui_subscribes_again_run_once(string head, string launchArgs)
    {
        // BuildUi subscribes to a box that outlives the rebuild (a routed event of the toolkit)
        // and to a collection in a field (a plain .NET event). Building the UI again subscribes
        // again; without cleanup, typing would count twice and add every entry twice.
        bool wpf = head.Contains("WPF", StringComparison.Ordinal);
        var dllPath = Path.Combine(_directory, "DynamicUserControl.dll");
        var v1 = wpf ? WpfCounterV1 : AvaloniaCounterV1;

        await using var child = await ChildSession.StartAsync(TestEnvironment.HeadExe(head), launchArgs,
            new Dictionary<string, string> { [HotReloadRuntime.EnvironmentVariable] = HotReloadRuntime.EnvironmentValue });
        using var hotReload = new HotReloadCoordinator();

        await CompileAsync(v1, dllPath, wpf);
        await child.LoadAsync(dllPath);
        (await child.WaitForLoadsAsync(1)).Should().BeTrue(string.Join(" | ", child.Errors));
        (await hotReload.StartAsync(dllPath, child.HotReloadCapabilities)).Should().BeNull();

        (await child.SetPropertyAsync("trigger", "Text", "a"))!.Success.Should().BeTrue();
        (await WaitForPropertyAsync(child, "status", "Text", "text=1 entries=1")).Should().Be("text=1 entries=1");

        await CompileAsync(v1.Replace("\"Counter v1\"", "\"Counter v2\""), dllPath, wpf);
        var attempt = await hotReload.TryAsync(dllPath, child.HotReloadAsync);

        attempt.Applied.Should().BeTrue(attempt.Message);
        attempt.Message.Should().Contain("2 duplicate event subscription(s) removed");

        (await child.SetPropertyAsync("trigger", "Text", "ab"))!.Success.Should().BeTrue();
        (await WaitForPropertyAsync(child, "status", "Text", "text=2 entries=2")).Should().Be("text=2 entries=2",
            "one handler per event, as after a fresh start");

        await Task.Delay(300);
        child.Errors.Should().BeEmpty();
    }

    private const string AvaloniaCounterV1 = """
        using System.Collections.ObjectModel;
        using Avalonia;
        using Avalonia.Controls;

        public class DynamicUserControl : UserControl
        {
            private readonly TextBox _trigger = new TextBox { Name = "trigger" };
            private readonly ObservableCollection<string> _entries = new();
            private int _textChanges, _entryChanges;
            private TextBlock _status = new TextBlock();

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                _trigger.TextChanged += OnTriggerChanged;
                _entries.CollectionChanged += (_, _) => { _entryChanges++; ShowCounts(); };

                (_trigger.Parent as Panel)?.Children.Remove(_trigger);
                _status = new TextBlock { Name = "status" };
                var panel = new StackPanel { Margin = new Thickness(16) };
                panel.Children.Add(new TextBlock { Name = "title", Text = "Counter v1" });
                panel.Children.Add(_trigger);
                panel.Children.Add(_status);
                Content = panel;
                ShowCounts();
            }

            private void OnTriggerChanged(object? sender, TextChangedEventArgs e)
            {
                _textChanges++;
                _entries.Add(_trigger.Text ?? "");
            }

            private void ShowCounts() => _status.Text = "text=" + _textChanges + " entries=" + _entryChanges;
        }
        """;

    private const string WpfCounterV1 = """
        using System.Collections.ObjectModel;
        using System.Windows;
        using System.Windows.Controls;

        public class DynamicUserControl : UserControl
        {
            private readonly TextBox _trigger = new TextBox { Name = "trigger" };
            private readonly ObservableCollection<string> _entries = new();
            private int _textChanges, _entryChanges;
            private TextBlock _status = new TextBlock();

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                _trigger.TextChanged += OnTriggerChanged;
                _entries.CollectionChanged += (_, _) => { _entryChanges++; ShowCounts(); };

                (_trigger.Parent as Panel)?.Children.Remove(_trigger);
                _status = new TextBlock { Name = "status" };
                var panel = new StackPanel { Margin = new Thickness(16) };
                panel.Children.Add(new TextBlock { Name = "title", Text = "Counter v1" });
                panel.Children.Add(_trigger);
                panel.Children.Add(_status);
                Content = panel;
                ShowCounts();
            }

            private void OnTriggerChanged(object sender, TextChangedEventArgs e)
            {
                _textChanges++;
                _entries.Add(_trigger.Text);
            }

            private void ShowCounts() => _status.Text = "text=" + _textChanges + " entries=" + _entryChanges;
        }
        """;

    [Theory]
    [MemberData(nameof(DesktopHeads))]
    public async Task A_package_the_change_adds_arrives_with_the_update(string head, string launchArgs)
    {
        // "Use library X for this": the new build references an assembly the running app never
        // had. The host streams it to the window before the update; the app is patched, not reloaded.
        bool wpf = head.Contains("WPF", StringComparison.Ordinal);
        var dllPath = Path.Combine(_directory, "DynamicUserControl.dll");
        var libraryPath = Path.Combine(_directory, "lib", "HotReloadGreeter.dll");
        await CompileLibraryAsync(
            "namespace Greeting { public static class Greeter { public static string Greet(string who) => \"Hello \" + who; } }",
            "HotReloadGreeter", libraryPath);
        var v1 = wpf ? WpfNotesV1 : AvaloniaNotesV1;

        await using var child = await ChildSession.StartAsync(TestEnvironment.HeadExe(head), launchArgs,
            new Dictionary<string, string> { [HotReloadRuntime.EnvironmentVariable] = HotReloadRuntime.EnvironmentValue });
        using var hotReload = new HotReloadCoordinator();

        await CompileAsync(v1, dllPath, wpf);
        await child.LoadAsync(dllPath);
        (await child.WaitForLoadsAsync(1)).Should().BeTrue(string.Join(" | ", child.Errors));
        (await hotReload.StartAsync(dllPath, child.HotReloadCapabilities)).Should().BeNull();
        (await child.SetPropertyAsync("TextBox:0", "Text", "typed by the user"))!.Success.Should().BeTrue();

        var v2 = v1.Replace("new TextBlock { Text = \"Notes v1\" }", "new TextBlock { Name = \"title\", Text = Greeting.Greeter.Greet(\"v2\") }");
        await CompileAsync(v2, dllPath, wpf, [libraryPath]);
        var attempt = await hotReload.TryAsync(dllPath, request => child.HotReloadAsync(request, [libraryPath]));

        attempt.Applied.Should().BeTrue(attempt.Message);
        attempt.Message.Should().Contain("HotReloadGreeter.dll");
        child.LoadExecutedCount.Should().Be(1, "the app was patched, not loaded again");
        var tree = await child.InspectVisualTreeAsync();
        Property(tree, "title", "Text").Should().Be("Hello v2", "the patched code runs against the new library");

        await Task.Delay(300);
        child.Errors.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(DesktopHeads))]
    public async Task Selection_follows_the_item_and_open_tree_nodes_stay_open(string head, string launchArgs)
    {
        // The user selected "alpha" and opened two levels of a tree. The change sorts the list the
        // other way round: the selection moves with the item, and the nested node - which only
        // exists once its parent is open - is opened again too.
        bool wpf = head.Contains("WPF", StringComparison.Ordinal);
        var dllPath = Path.Combine(_directory, "DynamicUserControl.dll");
        var v1 = wpf ? WpfListsV1 : AvaloniaListsV1;

        await using var child = await ChildSession.StartAsync(TestEnvironment.HeadExe(head), launchArgs,
            new Dictionary<string, string> { [HotReloadRuntime.EnvironmentVariable] = HotReloadRuntime.EnvironmentValue });
        using var hotReload = new HotReloadCoordinator();

        await CompileAsync(v1, dllPath, wpf);
        await child.LoadAsync(dllPath);
        (await child.WaitForLoadsAsync(1)).Should().BeTrue(string.Join(" | ", child.Errors));
        (await hotReload.StartAsync(dllPath, child.HotReloadCapabilities)).Should().BeNull();

        (await child.SetPropertyAsync("names", "SelectedIndex", "0"))!.Success.Should().BeTrue();
        (await child.SetPropertyAsync("docs", "IsExpanded", "true"))!.Success.Should().BeTrue();
        (await RetryAsync(() => child.SetPropertyAsync("work", "IsExpanded", "true"))).Should().BeTrue("the nested node appears once its parent is open");

        await CompileAsync(v1.Replace("_names.ToList()", "_names.OrderByDescending(n => n).ToList()"), dllPath, wpf);
        var attempt = await hotReload.TryAsync(dllPath, child.HotReloadAsync);

        attempt.Applied.Should().BeTrue(attempt.Message);
        var tree = await child.InspectVisualTreeAsync();
        Property(tree, "names", "SelectedIndex").Should().Be("2", "\"alpha\" is last now, and still selected");
        Property(tree, "docs", "IsExpanded").Should().Be("True");
        Property(tree, "work", "IsExpanded").Should().Be("True", "restored in a second pass, after its parent was open again");

        await Task.Delay(300);
        child.Errors.Should().BeEmpty();
    }

    private const string AvaloniaListsV1 = """
        using System.Collections.Generic;
        using System.Linq;
        using Avalonia;
        using Avalonia.Controls;

        public class DynamicUserControl : UserControl
        {
            private readonly List<string> _names = new() { "alpha", "beta", "gamma" };

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                var panel = new StackPanel { Margin = new Thickness(16) };
                panel.Children.Add(new ListBox { Name = "names", ItemsSource = _names.ToList() });
                var docs = new TreeViewItem { Name = "docs", Header = "Documents" };
                var work = new TreeViewItem { Name = "work", Header = "Work" };
                work.Items.Add(new TreeViewItem { Header = "Report" });
                docs.Items.Add(work);
                var tree = new TreeView { Name = "tree" };
                tree.Items.Add(docs);
                panel.Children.Add(tree);
                Content = panel;
            }
        }
        """;

    private const string WpfListsV1 = """
        using System.Collections.Generic;
        using System.Linq;
        using System.Windows;
        using System.Windows.Controls;

        public class DynamicUserControl : UserControl
        {
            private readonly List<string> _names = new() { "alpha", "beta", "gamma" };

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                var panel = new StackPanel { Margin = new Thickness(16) };
                panel.Children.Add(new ListBox { Name = "names", ItemsSource = _names.ToList() });
                var docs = new TreeViewItem { Name = "docs", Header = "Documents" };
                var work = new TreeViewItem { Name = "work", Header = "Work" };
                work.Items.Add(new TreeViewItem { Header = "Report" });
                docs.Items.Add(work);
                var tree = new TreeView { Name = "tree" };
                tree.Items.Add(docs);
                panel.Children.Add(tree);
                Content = panel;
            }
        }
        """;

    private static async Task<string?> WaitForPropertyAsync(ChildSession child, string control, string property, string expected)
    {
        string? value = null;
        for (int i = 0; i < 30; i++)
        {
            value = Property(await child.InspectVisualTreeAsync(), control, property);
            if (value == expected) break;
            await Task.Delay(100);
        }
        return value;
    }

    private static async Task<bool> RetryAsync(Func<Task<Neo.IPC.SetPropertyResultMessage?>> action)
    {
        for (int i = 0; i < 30; i++)
        {
            if ((await action())?.Success == true) return true;
            await Task.Delay(100);
        }
        return false;
    }

    [Fact]
    public async Task A_head_without_edit_and_continue_says_so_and_the_host_reloads_as_before()
    {
        await using var child = await ChildSession.StartAsync(TestEnvironment.HeadExe("Neo.PluginWindowAvalonia"), "--standalone");
        child.HotReloadCapabilities.Should().BeNull();

        var dllPath = Path.Combine(_directory, "DynamicUserControl.dll");
        await CompileAsync(AvaloniaV1, dllPath, wpf: false);

        using var hotReload = new HotReloadCoordinator();
        (await hotReload.StartAsync(dllPath, child.HotReloadCapabilities)).Should().Contain("without hot reload support");
        (await hotReload.TryAsync(dllPath, child.HotReloadAsync)).Applied.Should().BeFalse();
    }

    /// <summary>A property of the named control in the tree JSON the windows send for InspectVisualTree.</summary>
    private static string? Property(string? treeJson, string controlName, string property)
    {
        if (treeJson == null) return null;
        using var document = System.Text.Json.JsonDocument.Parse(treeJson);
        var pending = new Stack<System.Text.Json.JsonElement>();
        pending.Push(document.RootElement);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node.TryGetProperty("name", out var name) && name.GetString() == controlName &&
                node.TryGetProperty("properties", out var properties) && properties.TryGetProperty(property, out var value))
                return value.GetString();
            if (node.TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray()) pending.Push(child);
        }
        return null;
    }

    /// <summary>The compile agent with the hosts' defaults — hot-reloadable builds with a baseline file.</summary>
    private static async Task CompileAsync(string source, string dllPath, bool wpf, IEnumerable<string>? packages = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);

        var coreDirectories = new List<string> { RuntimeEnvironment.GetRuntimeDirectory() };
        var additional = new List<string>(packages ?? []);
        if (wpf)
            coreDirectories.Add(Path.GetDirectoryName(typeof(System.Windows.Window).Assembly.Location)!);
        else
            additional.AddRange(Directory.GetFiles(TestEnvironment.HeadBinDir("Neo.PluginWindowAvalonia"), "Avalonia*.dll"));

        var agent = new CSharpDllCompileAgent();
        agent.SetOption("CoreDllPath", coreDirectories);
        agent.SetInput("Code", new List<string> { source });
        agent.SetInput("AssemblyName", "DynamicUserControl");
        agent.SetInput("DllOutputPath", dllPath);
        agent.SetInput("NuGetDlls", new List<string>());
        agent.SetInput("AdditionalDlls", additional);
        await agent.ExecuteAsync();
    }

    /// <summary>A library that plays a NuGet package.</summary>
    private static async Task CompileLibraryAsync(string source, string assemblyName, string dllPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);
        var agent = new CSharpDllCompileAgent();
        agent.SetOption("CoreDllPath", new List<string> { RuntimeEnvironment.GetRuntimeDirectory() });
        agent.SetInput("Code", new List<string> { source });
        agent.SetInput("AssemblyName", assemblyName);
        agent.SetInput("DllOutputPath", dllPath);
        agent.SetInput("NuGetDlls", new List<string>());
        agent.SetInput("AdditionalDlls", new List<string>());
        await agent.ExecuteAsync();
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }
}
