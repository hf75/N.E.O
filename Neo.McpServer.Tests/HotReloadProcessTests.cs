using FluentAssertions;
using Xunit;

namespace Neo.McpServer.Tests;

/// <summary>
/// The scenario hot reload exists for, end to end through the real server and a real window:
/// an app is running and holds what the user did with it; the next prompt adds a feature; the
/// data, the text in a box and the selected state are still there afterwards — and the new
/// feature works. Everything in between is real: Roslyn's delta analysis in the server, the pipe,
/// MetadataUpdater in the child, the UI rebuild with the state carried over.
/// </summary>
[Collection("ExportedProcesses")]
[Trait("Category", "UiSmoke")]
public class HotReloadProcessTests
{
    private const string V1 = """
        using System.Collections.ObjectModel;
        using System.ComponentModel;
        using Avalonia;
        using Avalonia.Controls;
        using Neo.App;

        public class DynamicUserControl : UserControl, INotifyPropertyChanged
        {
            private readonly ObservableCollection<string> _rows = new();

            public DynamicUserControl()
            {
                _rows.CollectionChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowCount)));
                BuildUi();
            }

            private void BuildUi()
            {
                Content = new StackPanel
                {
                    Margin = new Thickness(16), Spacing = 8,
                    Children =
                    {
                        new TextBlock { Name = "title", Text = "Workbook viewer v1" },
                        new TextBox { Name = "filter", PlaceholderText = "Filter" },
                        new CheckBox { Name = "headers", Content = "First row has headers" },
                        new ListBox { Name = "rows", ItemsSource = _rows, Height = 160 },
                    }
                };
            }

            [McpObservable("Number of loaded rows.")]
            public int RowCount => _rows.Count;

            [McpCallable("Loads a row, as if read from the Excel file.")]
            public int LoadRow(string text) { _rows.Add(text); return _rows.Count; }

            public new event PropertyChangedEventHandler? PropertyChanged;
        }
        """;

    // "Add a way to tag rows": a new field with an initializer, a new callable, a changed UI.
    private static readonly string V2 = V1
        .Replace("private readonly ObservableCollection<string> _rows = new();",
                 "private readonly ObservableCollection<string> _rows = new();\n    private readonly System.Collections.Generic.List<string> _tags = new() { \"imported\" };")
        .Replace("\"Workbook viewer v1\"", "\"Workbook viewer v2 — \" + _rows.Count + \" rows, \" + _tags.Count + \" tag(s)\"")
        .Replace("    [McpObservable(\"Number of loaded rows.\")]",
                 "    [McpCallable(\"Adds a tag. Returns the number of tags.\")]\n    public int AddTag(string tag) { _tags.Add(tag); return _tags.Count; }\n\n    [McpObservable(\"Number of loaded rows.\")]");

    [Fact]
    public async Task A_new_feature_arrives_while_the_loaded_data_and_the_ui_state_stay()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var server = StdioJsonRpcProcess.Start("dotnet", $"\"{McpServerProcessTests.ServerDll}\"");
        await server.InitializeAsync("neo-hot-reload-tests");

        try
        {
            (await server.CallToolAsync("compile_and_preview", new { sourceCode = new[] { V1 }, framework = "avalonia" }, TimeSpan.FromMinutes(3)))
                .Should().StartWith("SUCCESS", "server stderr:\n" + server.StandardError);

            // What the user did with the running app.
            (await server.CallToolAsync("invoke_method", new { method = "LoadRow", argsJson = "[\"row 1\"]" })).Should().Contain("1");
            (await server.CallToolAsync("invoke_method", new { method = "LoadRow", argsJson = "[\"row 2\"]" })).Should().Contain("2");
            (await server.CallToolAsync("set_property", new { target = "filter", propertyName = "Text", value = "typed by the user" })).Should().NotContain("not found");
            (await server.CallToolAsync("set_property", new { target = "headers", propertyName = "IsChecked", value = "true" })).Should().NotContain("not found");

            // The next prompt.
            var updated = await server.CallToolAsync("update_preview", new { sourceCode = new[] { V2 }, framework = "avalonia" }, TimeSpan.FromMinutes(3));
            updated.Should().StartWith("SUCCESS", "server stderr:\n" + server.StandardError);
            updated.Should().Contain("Hot reload (generation 1)", "the app must be patched in place, not replaced");
            updated.Should().Contain("state is preserved");

            // Nothing was lost …
            (await server.CallToolAsync("read_observable", new { observable = "RowCount" })).Should().Contain("2", "the loaded rows live in the instance that survived");
            var tree = await server.CallToolAsync("inspect_visual_tree", new { }, TimeSpan.FromSeconds(60));
            tree.Should().Contain("typed by the user", "text that only existed in the TextBox is carried over into the rebuilt UI");

            // … and the new version is what runs: rebuilt UI, initialised new field, new callable.
            tree.Should().Contain("Workbook viewer v2 — 2 rows, 1 tag(s)");
            (await server.CallToolAsync("invoke_method", new { method = "AddTag", argsJson = "[\"urgent\"]" }, TimeSpan.FromSeconds(60)))
                .Should().Contain("2", "the new field was initialised on the live instance ({\"imported\"}) and the manifest was refreshed");

            (await server.CallToolAsync("get_runtime_errors", new { })).Should().Contain("No runtime errors");
        }
        finally
        {
            try { await server.CallToolAsync("close_preview", new { }, TimeSpan.FromSeconds(20)); } catch { /* server is killed anyway */ }
        }
    }

    private const string WpfV1 = """
        using System.Windows;
        using System.Windows.Controls;

        public class DynamicUserControl : UserControl
        {
            private string _lastFilter = "";      // state that only lives in the instance

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                var filter = new TextBox { Name = "filter" };
                filter.TextChanged += (_, _) => _lastFilter = filter.Text;

                var panel = new StackPanel { Margin = new Thickness(16) };
                panel.Children.Add(new TextBlock { Name = "title", Text = "WPF viewer v1 [" + _lastFilter + "]" });
                panel.Children.Add(filter);
                panel.Children.Add(new CheckBox { Name = "headers", Content = "First row has headers" });
                Content = panel;
            }
        }
        """;

    [Fact]
    public async Task The_wpf_window_hot_reloads_too()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var server = StdioJsonRpcProcess.Start("dotnet", $"\"{McpServerProcessTests.ServerDll}\"");
        await server.InitializeAsync("neo-hot-reload-tests");

        try
        {
            (await server.CallToolAsync("compile_and_preview", new { sourceCode = new[] { WpfV1 }, framework = "wpf" }, TimeSpan.FromMinutes(3)))
                .Should().StartWith("SUCCESS", "server stderr:\n" + server.StandardError);
            (await server.CallToolAsync("set_property", new { target = "filter", propertyName = "Text", value = "typed by the user" })).Should().NotContain("not found");
            (await server.CallToolAsync("set_property", new { target = "headers", propertyName = "IsChecked", value = "true" })).Should().NotContain("not found");

            var v2 = WpfV1.Replace("\"WPF viewer v1 [\"", "\"WPF viewer v2 [\"")
                          .Replace("private string _lastFilter = \"\";", "private string _lastFilter = \"\";\n    private readonly System.Collections.Generic.List<int> _added = new() { 1, 2, 3 };")
                          .Replace("_lastFilter + \"]\"", "_lastFilter + \"] added=\" + _added.Count");
            var updated = await server.CallToolAsync("update_preview", new { sourceCode = new[] { v2 }, framework = "wpf" }, TimeSpan.FromMinutes(3));

            updated.Should().StartWith("SUCCESS", "server stderr:\n" + server.StandardError);
            updated.Should().Contain("Hot reload (generation 1)");

            var tree = await server.CallToolAsync("inspect_visual_tree", new { }, TimeSpan.FromSeconds(60));
            tree.Should().Contain("WPF viewer v2 [typed by the user] added=3",
                "the field written by the old handler survived, the new field was initialised, the UI was rebuilt by the new code");
        }
        finally
        {
            try { await server.CallToolAsync("close_preview", new { }, TimeSpan.FromSeconds(20)); } catch { }
        }
    }

    [Fact]
    public async Task A_change_that_cannot_be_patched_falls_back_to_a_full_reload_and_says_why()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var server = StdioJsonRpcProcess.Start("dotnet", $"\"{McpServerProcessTests.ServerDll}\"");
        await server.InitializeAsync("neo-hot-reload-tests");

        try
        {
            (await server.CallToolAsync("compile_and_preview", new { sourceCode = new[] { V1 }, framework = "avalonia" }, TimeSpan.FromMinutes(3)))
                .Should().StartWith("SUCCESS");
            await server.CallToolAsync("invoke_method", new { method = "LoadRow", argsJson = "[\"row 1\"]" });

            // A new base interface is a "rude edit": the runtime cannot change a type's shape like that.
            var rude = V1.Replace("UserControl, INotifyPropertyChanged", "UserControl, INotifyPropertyChanged, System.IDisposable")
                         .Replace("    [McpObservable(", "    public void Dispose() { }\n\n    [McpObservable(");
            var updated = await server.CallToolAsync("update_preview", new { sourceCode = new[] { rude }, framework = "avalonia" }, TimeSpan.FromMinutes(3));

            updated.Should().StartWith("SUCCESS", "the new version still has to be shown");
            updated.Should().Contain("Hot reload not possible").And.Contain("ENC");

            var tree = await server.CallToolAsync("inspect_visual_tree", new { }, TimeSpan.FromSeconds(60));
            tree.Should().Contain("Workbook viewer v1");
            (await server.CallToolAsync("read_observable", new { observable = "RowCount" }, TimeSpan.FromSeconds(60)))
                .Should().Contain("0", "a full reload starts from scratch — that is the old behaviour");
        }
        finally
        {
            try { await server.CallToolAsync("close_preview", new { }, TimeSpan.FromSeconds(20)); } catch { }
        }
    }
}
