using FluentAssertions;
using Neo.AssemblyForge;
using Neo.McpServer.Services;
using Xunit;

namespace Neo.McpServer.Tests;

/// <summary>
/// Generated code is compiled against the Avalonia assemblies the plugin window ships. These
/// tests push real source through <see cref="CompilationPipeline"/> — the same path
/// compile_and_preview takes — so they fail when the shipped Avalonia and what we tell the
/// models about it (<see cref="AvaloniaGuidance"/>) drift apart:
/// the samples must compile, every API the guidance recommends must compile, and every API it
/// declares gone must really be gone.
///
/// <para>Needs the built Avalonia plugin window (build-order reference in the csproj). No window
/// is opened and nothing is downloaded.</para>
/// </summary>
public class AvaloniaVersionCompileTests
{
    private static async Task<CompilationPipeline.CompilationResult> CompileAsync(
        string source, IReadOnlyDictionary<string, string>? packages = null)
        => await new CompilationPipeline().CompileAsync(new[] { source }, packages, framework: "avalonia");

    private static string Wrap(string usings, string members, string constructorBody) => $$"""
        using System;
        using System.Linq;
        using System.Threading.Tasks;
        using Avalonia;
        using Avalonia.Controls;
        using Avalonia.Controls.Primitives;
        using Avalonia.Input;
        using Avalonia.Media;
        {{usings}}

        public class DynamicUserControl : UserControl
        {
            public DynamicUserControl()
            {
                {{constructorBody}}
            }

            {{members}}
        }
        """;

    public static TheoryData<string> Samples()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot, "samples", "live-mcp"), "*.cs"))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Sample_app_compiles_against_the_shipped_Avalonia(string sampleFile)
    {
        var source = await File.ReadAllTextAsync(Path.Combine(RepoRoot, "samples", "live-mcp", sampleFile));

        var result = await CompileAsync(source);

        result.Success.Should().BeTrue($"{sampleFile} should compile, errors: {string.Join(" | ", result.Errors)}");
    }

    [Fact]
    public async Task Every_api_the_guidance_recommends_compiles()
    {
        var source = Wrap(
            usings: """
                using Avalonia.Animation.Easings;
                using Avalonia.Input.Platform;
                using Avalonia.Interactivity;
                using Avalonia.Platform.Storage;
                using Avalonia.Threading;
                """,
            constructorBody: """
                var check = new CheckBox { Content = "Done" };
                check.IsCheckedChanged += (_, _) => { var on = check.IsChecked == true; };
                var toggle = new ToggleSwitch();
                toggle.IsCheckedChanged += (_, _) => { };

                var box = new TextBox { PlaceholderText = "Search", UseFloatingPlaceholder = true };
                var number = new NumericUpDown { PlaceholderText = "0" };
                var auto = new AutoCompleteBox { PlaceholderText = "City" };
                var date = new CalendarDatePicker { PlaceholderText = "Day" };

                box.GotFocus += OnFocus;
                box.LostFocus += OnFocus;

                DragDrop.SetAllowDrop(this, true);
                AddHandler(DragDrop.DropEvent, OnDrop);

                var list = new ListBox();
                Control? first = list.ContainerFromIndex(0);
                int index = first == null ? -1 : list.IndexFromContainer(first);

                var popup = new Popup { Placement = PlacementMode.Bottom };
                var menu = new ContextMenu { Placement = PlacementMode.Pointer };
                var brush = new RadialGradientBrush
                {
                    RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                    RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                };
                var easing = new SplineEasing(0.2, 0, 0, 1);
                uint argb = Colors.Red.ToUInt32();
                TextOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);

                var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                box.Tapped += (_, _) => { };
                box.DoubleTapped += (_, _) => { };

                var web = new NativeWebView { Source = new Uri("https://example.com") };
                web.NavigationStarted += (_, e) => e.Cancel = false;
                web.NavigationCompleted += (_, e) => { var ok = e.IsSuccess; };
                web.NewWindowRequested += (_, e) => e.Handled = true;
                web.WebMessageReceived += (_, e) => { var body = e.Body; };
                web.NavigateToString("<html></html>");
                bool canBack = web.CanGoBack && web.CanGoForward;

                Content = new StackPanel { Children = { check, toggle, box, number, auto, date, list, web } };
                """,
            members: """
                private void OnFocus(object? sender, FocusChangedEventArgs e) { }

                private void OnDrop(object? sender, DragEventArgs e)
                {
                    var text = e.DataTransfer.TryGetText();
                    var files = e.DataTransfer.TryGetFiles();
                    bool hasFile = e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(DataFormat.Text);
                }

                private async Task StartDragAsync(PointerPressedEventArgs press)
                {
                    var data = new DataTransfer();
                    data.Add(DataTransferItem.CreateText("payload"));
                    await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
                }

                private async Task DialogsAndClipboardAsync()
                {
                    var top = TopLevel.GetTopLevel(this)!;
                    var opened = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
                    var saved = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions());
                    var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());

                    var clipboard = top.Clipboard!;
                    await clipboard.SetTextAsync("copied");
                    string? pasted = await clipboard.TryGetTextAsync();

                    if (top is Window window)
                        window.WindowDecorations = WindowDecorations.None;
                }

                private async Task<string?> ScriptAsync(NativeWebView web)
                {
                    web.Navigate(new Uri("https://example.com"));
                    web.GoBack(); web.GoForward(); web.Refresh(); web.Stop();
                    return await web.InvokeScript("1 + 1");
                }

                public override void Render(DrawingContext context)
                {
                    using (context.PushTransform(Matrix.CreateTranslation(1, 1)))
                        base.Render(context);
                }

                private sealed class AccentButton : Button
                {
                    protected override Type StyleKeyOverride => typeof(Button);
                }
                """);

        var result = await CompileAsync(source);

        result.Success.Should().BeTrue("the guidance must only recommend APIs that exist, errors: " +
            string.Join(" | ", result.Errors));
    }

    [Theory]
    [InlineData("Checked event", "var c = new CheckBox(); c.Checked += (_, _) => { };")]
    [InlineData("Unchecked event", "var c = new ToggleButton(); c.Unchecked += (_, _) => { };")]
    [InlineData("OpenFileDialog", "var d = new OpenFileDialog();")]
    [InlineData("SaveFileDialog", "var d = new SaveFileDialog();")]
    [InlineData("OpenFolderDialog", "var d = new OpenFolderDialog();")]
    [InlineData("Clipboard.GetTextAsync", "var t = TopLevel.GetTopLevel(this)!.Clipboard!.GetTextAsync();")]
    [InlineData("DragEventArgs.Data", "AddHandler(DragDrop.DropEvent, (_, e) => { var d = e.Data; });")]
    [InlineData("DataFormats", "var f = DataFormats.Text;")]
    [InlineData("DataObject", "var d = new DataObject(); d.Set(\"x\", \"y\");")]
    [InlineData("GotFocusEventArgs", "GotFocus += (object? s, GotFocusEventArgs e) => { };")]
    [InlineData("SystemDecorations enum", "var d = SystemDecorations.None;")]
    [InlineData("GetVisualRoot", "var r = Avalonia.VisualTree.VisualExtensions.GetVisualRoot(this);")]
    [InlineData("Gestures class", "var e = Gestures.TappedEvent;")]
    [InlineData("RadialGradientBrush.Radius", "var b = new RadialGradientBrush { Radius = 0.5 };")]
    [InlineData("IStyleable", "Type? k = ((Avalonia.Styling.IStyleable)this).StyleKey;")]
    [InlineData("IBinding", "Avalonia.Data.IBinding? b = null;")]
    [InlineData("ItemContainerGenerator.ContainerFromIndex", "var c = new ListBox().ItemContainerGenerator.ContainerFromIndex(0);")]
    [InlineData("Popup.PlacementMode", "var p = new Popup { PlacementMode = PlacementMode.Bottom };")]
    [InlineData("CubicBezierEasing", "var e = new Avalonia.Animation.Easings.CubicBezierEasing();")]
    [InlineData("Color.ToUint32", "uint v = Colors.Red.ToUint32();")]
    public async Task Api_the_guidance_declares_gone_does_not_compile(string what, string statement)
    {
        var result = await CompileAsync(Wrap(usings: "", members: "", constructorBody: statement));

        result.Success.Should().BeFalse(
            $"the guidance tells models that '{what}' no longer exists — if it compiles again, the guidance is wrong");

        // "Checked event" → Checked, "Popup.PlacementMode" → PlacementMode
        var identifier = what.Split(' ')[0].Split('.')[^1];
        string.Join(" ", result.Errors).Should().Contain(identifier,
            "the snippet has to fail because of that API, not because of a mistake in this test");
    }

    [Fact]
    public async Task A_project_that_still_pins_the_old_Avalonia_compiles_against_the_shipped_one()
    {
        // Sessions and skills saved before the move to Avalonia 12 list "Avalonia|11.3.12". The
        // plugin window provides these packages, so they must be ignored — not downloaded and
        // referenced next to the shipped version, which would make every Avalonia type ambiguous.
        var source = Wrap(usings: "", members: "", constructorBody: "Content = new TextBlock { Text = \"hello\" };");
        var oldPins = new Dictionary<string, string>
        {
            ["Avalonia"] = "11.3.12",
            ["Avalonia.Desktop"] = "11.3.12",
            ["Avalonia.Themes.Fluent"] = "11.3.12",
            ["Avalonia.Fonts.Inter"] = "11.3.12",
        };

        var result = await CompileAsync(source, oldPins);

        result.Success.Should().BeTrue("errors: " + string.Join(" | ", result.Errors));
        result.DependencyDllPaths.Should().NotContain(p => Path.GetFileName(p).StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase),
            "the Avalonia the child runs on is its own");
    }

    // <repo>/Neo.McpServer.Tests/bin/<Configuration>/<tfm>/
    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
