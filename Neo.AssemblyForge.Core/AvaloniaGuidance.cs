using System.Reflection;

namespace Neo.AssemblyForge;

/// <summary>
/// What every prompt that asks a model for Avalonia code has to say about the Avalonia version.
/// One text for all surfaces (desktop hosts, MCP server, Web App): generated code is compiled
/// against and runs on the Avalonia assemblies N.E.O. ships, and the models mostly know the
/// previous major version, so each of them needs the same version pin and the same list of
/// API changes. Every statement below was checked against the shipped assemblies — keep it
/// that way when the version moves.
/// </summary>
public static class AvaloniaGuidance
{
    /// <summary>
    /// The Avalonia version N.E.O. ships, e.g. <c>12.1.2</c>. Comes from the
    /// <c>NeoAvaloniaVersion</c> property in Directory.Build.props, which the build stamps into
    /// every assembly as assembly metadata (this assembly cannot reference Neo.Shared: it also
    /// runs in the browser).
    /// </summary>
    public static string Version { get; } = typeof(AvaloniaGuidance).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "NeoAvaloniaVersion")?.Value ?? string.Empty;

    /// <summary>Major version, e.g. <c>12</c>.</summary>
    public static string MajorVersion => Version.Split('.')[0];

    /// <summary>The version pin. Same sentence everywhere, so the model never sees two numbers.</summary>
    public static string VersionRule =>
        "The host ships Avalonia " + Version + ": your code is compiled against exactly this version and runs on it. " +
        "For compatibility you must always use version " + Version + " for every Avalonia package. ";

    /// <summary>
    /// API differences to the previous major version that generated code keeps running into,
    /// one entry per topic.
    /// </summary>
    public static IReadOnlyList<string> ApiChanges { get; } =
    [
        "CheckBox, RadioButton, ToggleButton, ToggleSwitch: the events Checked, Unchecked and Indeterminate no longer exist. Subscribe to IsCheckedChanged and read IsChecked.",
        "TextBox, AutoCompleteBox, NumericUpDown, CalendarDatePicker: Watermark is now PlaceholderText (UseFloatingWatermark is now UseFloatingPlaceholder).",
        "File dialogs: OpenFileDialog, SaveFileDialog and OpenFolderDialog no longer exist. Use TopLevel.GetTopLevel(this)!.StorageProvider with OpenFilePickerAsync, SaveFilePickerAsync or OpenFolderPickerAsync.",
        "Clipboard: get it from TopLevel.GetTopLevel(this)?.Clipboard. SetTextAsync and TryGetTextAsync are extension methods, so add 'using Avalonia.Input.Platform;'. GetTextAsync no longer exists.",
        "Drag and drop: DragEventArgs.Data is now e.DataTransfer, the DataFormats class is now DataFormat (DataFormat.Text, DataFormat.File), and you read values with e.DataTransfer.TryGetText() or e.DataTransfer.TryGetFiles(). To start a drag, build a DataTransfer from DataTransferItem.CreateText(...) or CreateFile(...) and call DragDrop.DoDragDropAsync. DataObject and IDataObject no longer exist.",
        "GotFocus and LostFocus handlers receive FocusChangedEventArgs; GotFocusEventArgs no longer exists.",
        "Window.SystemDecorations and the SystemDecorations enum are now WindowDecorations. ExtendClientAreaChromeHints, TitleBar and CaptionButtons no longer exist.",
        "GetVisualRoot() no longer exists: use TopLevel.GetTopLevel(visual), and never cast the root of the visual tree to Window or TopLevel.",
        "The Gestures class is no longer public: Tapped, DoubleTapped, Holding, Pinch and ScrollGesture are ordinary events on every control.",
        "RadialGradientBrush.Radius is now RadiusX and RadiusY.",
        "IStyleable no longer exists: write 'protected override Type StyleKeyOverride => typeof(Button);'.",
        "IBinding is now BindingBase and InstancedBinding no longer exists; 'new Binding(\"Path\")' still works.",
        "ItemContainerGenerator.ContainerFromIndex and IndexFromContainer moved to the ItemsControl itself.",
        "The PlacementMode property of Popup and ContextMenu is now Placement; CubicBezierEasing is now SplineEasing; Color.ToUint32 is now ToUInt32; DrawingContext.PushPreTransform and PushPostTransform are now PushTransform; RenderOptions.TextRenderingMode is now TextOptions.TextRenderingMode.",
        "Create every DispatcherTimer on the UI thread: a timer belongs to the dispatcher of the thread that creates it.",
        "SkiaSharp is version 3.",
        "Add-on packages must be built for Avalonia " + MajorVersion + " (for example Avalonia.Controls.DataGrid " + Version + "); a package built for Avalonia 11 fails at runtime.",
    ];

    /// <summary>
    /// The web view in the same form. Desktop only — the control does not exist in the browser,
    /// so the Web App's prompt must not offer it.
    /// </summary>
    public static IReadOnlyList<string> WebViewFacts { get; } =
    [
        "The host ships the official Avalonia web browser control, Avalonia.Controls.NativeWebView (assembly Avalonia.Controls.WebView). It is already referenced: do not add a NuGet package for it, and never use third-party web view or CefGlue packages.",
        "It renders with the platform browser: WebView2 on Windows, WKWebView on macOS, WebKitGTK or WPE on Linux.",
        "Usage: 'var web = new NativeWebView { Source = new Uri(\"https://example.com\") };', web.Navigate(uri), web.NavigateToString(html), 'await web.InvokeScript(js)' (returns the script result as a string), GoBack(), GoForward(), Refresh(), Stop(), CanGoBack, CanGoForward.",
        "Events: NavigationStarted (e.Cancel), NavigationCompleted (e.IsSuccess), NewWindowRequested (e.Handled), WebMessageReceived (e.Body). JavaScript sends a string to C# with invokeCSharpAction(text), which raises WebMessageReceived.",
        "Give the web view its own area of the layout and do not place other controls on top of it.",
    ];

    private const string ApiHeader = " API — most examples you know were written for Avalonia 11. These APIs changed; always use the new form: ";
    private const string WebViewHeader = "WEB CONTENT: ";

    /// <summary><see cref="ApiChanges"/> as one paragraph, for the single-string system prompts.</summary>
    public static string ApiNotes
        => "AVALONIA " + MajorVersion + ApiHeader + string.Concat(ApiChanges.Select(c => "• " + c + " "));

    /// <summary><see cref="WebViewFacts"/> as one paragraph, for the single-string system prompts.</summary>
    public static string WebViewNotes
        => WebViewHeader + string.Concat(WebViewFacts.Select(c => c + " "));

    /// <summary>Renders entries as a Markdown list, for the prompts that are written in Markdown.</summary>
    public static string AsMarkdownList(IEnumerable<string> entries)
        => string.Concat(entries.Select(e => "- " + e + "\n"));
}
