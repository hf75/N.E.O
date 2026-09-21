using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Neo.IPC;
using Neo.McpServer.Services;

namespace Neo.McpServer.Tools;

public sealed partial class PreviewTools
{
    /// <summary>
    /// Modifies a property on a running control without recompilation.
    /// Instant change, preserves app state (scroll position, user input, timers).
    /// </summary>
    [McpServerTool(Name = "set_property")]
    [Description("Changes a property on a control in the running app WITHOUT recompilation. " +
        "Instant change that preserves app state (scroll positions, user input, timers). " +
        "Use for visual tweaks like colors, font sizes, text, margins, visibility. " +
        "For structural changes (adding controls, changing logic), use update_preview instead.")]
    public static async Task<string> SetProperty(
        PreviewSessionManager preview,
        [Description("Target control. Can be: a Name (e.g. 'myButton'), " +
            "a type name (e.g. 'TextBlock' for first match), " +
            "or type:index (e.g. 'TextBlock:2' for third TextBlock).")] string target,
        [Description("Property name, e.g. 'Foreground', 'FontSize', 'Text', 'IsVisible', 'Opacity', " +
            "'Background', 'Margin', 'FontWeight', 'Width', 'Height'.")] string propertyName,
        [Description("New value as string. Examples: 'Red', '#FF5500', '24', 'Hello World', 'true', " +
            "'10,5,10,5' (for Thickness/Margin), 'Bold' (for FontWeight).")] string value,
        [Description("Window ID for multi-window mode. Use different IDs to create multiple windows. " +
            "Omit for single-window mode (uses default window).")] string? windowId = null)
    {
        if (!preview.IsRunning(windowId))
            return "No preview is running. Call compile_and_preview first.";

        var request = new SetPropertyRequest(target, propertyName, value);
        var result = await preview.SetPropertyAsync(request, windowId: windowId);

        if (result == null)
            return "SetProperty failed: no response from preview window.";

        if (!result.Success)
            return $"SetProperty FAILED: {result.Message}";

        return $"OK: {result.Message}" +
               (result.OldValue != null ? $"\n  Old: {result.OldValue}" : "") +
               (result.NewValue != null ? $"\n  New: {result.NewValue}" : "");
    }

    /// <summary>
    /// Parses NuGet packages from a JSON string like '{"Humanizer": "default", "Bogus": "35.6.1"}'.
    /// Using string instead of Dictionary parameter to avoid MCP SDK deserialization issues.
    /// </summary>
    /// <summary>
    /// Injects live data into a running app's controls without recompilation.
    /// </summary>
    [McpServerTool(Name = "inject_data")]
    [Description("Injects live data into a running app's controls WITHOUT recompilation. " +
        "Three modes: 'replace' pushes JSON array data into ListBox/ItemsControl/ComboBox (replaces existing). " +
        "'append' adds items to existing data (for live feeds). " +
        "'fill' sets values on multiple form controls (TextBox, CheckBox, Slider, ComboBox) in one call. " +
        "Auto-generates display templates for items controls. Preserves app state.")]
    public static async Task<string> InjectData(
        PreviewSessionManager preview,
        [Description("Target control. For 'replace'/'append': the items control (e.g. 'myListBox', 'ListBox:0'). " +
            "For 'fill': 'root' or a parent container name.")] string target,
        [Description("Injection mode: 'replace' (set new items), 'append' (add to existing), " +
            "or 'fill' (set form control values by name).")] string mode,
        [Description("JSON data. For 'replace'/'append': a JSON array of objects, e.g. " +
            "'[{\"name\":\"Alice\",\"age\":30},{\"name\":\"Bob\",\"age\":25}]'. " +
            "For 'fill': a JSON object mapping control names to values, e.g. " +
            "'{\"nameBox\":\"Alice\",\"ageSlider\":30,\"activeCheck\":true}'.")] string dataJson,
        [Description("Auto-generate ItemTemplate if the control has none. Default true.")] bool autoTemplate = true,
        [Description("Comma-separated field names for auto-template (shows only these fields). " +
            "If omitted, all fields are shown. Example: 'name,email,role'.")] string? focusFields = null,
        [Description("Window ID for multi-window mode. Use different IDs to create multiple windows. " +
            "Omit for single-window mode (uses default window).")] string? windowId = null)
    {
        if (!preview.IsRunning(windowId))
            return "No preview is running. Call compile_and_preview first.";

        try
        {
            var request = new InjectDataRequest(target, mode, dataJson, autoTemplate, focusFields);
            var result = await preview.InjectDataAsync(request, windowId: windowId);

            if (result == null)
                return "InjectData failed: no response from preview window.";

            if (!result.Success)
                return $"INJECT FAILED: {result.Message}";

            var fieldsInfo = result.DetectedFields != null
                ? $"\nFields: {string.Join(", ", result.DetectedFields)}"
                : "";

            return $"OK: {result.Message}" +
                   (result.ItemCount.HasValue ? $"\nItem count: {result.ItemCount}" : "") +
                   fieldsInfo;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[inject_data] EXCEPTION: {ex}");
            return $"ERROR: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Reads current data from a running app's controls.
    /// </summary>
    [McpServerTool(Name = "read_data")]
    [Description("Reads current data from a running app's controls. " +
        "For items controls (ListBox, ComboBox): returns all items as JSON array. " +
        "For form containers: returns all named children's current values as JSON object. " +
        "For individual controls: returns the control's current value. " +
        "Use this to see what the user typed, verify inject_data results, or read app state.")]
    public static async Task<string> ReadData(
        PreviewSessionManager preview,
        [Description("Target control. A Name (e.g. 'myListBox'), type (e.g. 'ListBox'), " +
            "type:index (e.g. 'TextBox:2'), or 'root' for the entire UserControl.")] string target,
        [Description("What to read: 'items' (ItemsSource data), 'form' (all named children values), " +
            "'value' (single control's value). If omitted, auto-detects based on control type.")] string? scope = null,
        [Description("Window ID for multi-window mode. Use different IDs to create multiple windows. " +
            "Omit for single-window mode (uses default window).")] string? windowId = null)
    {
        if (!preview.IsRunning(windowId))
            return "No preview is running. Call compile_and_preview first.";

        try
        {
            var request = new ReadDataRequest(target, scope);
            var result = await preview.ReadDataAsync(request, windowId: windowId);

            if (result == null)
                return "ReadData failed: no response from preview window.";

            if (!result.Success)
                return $"READ FAILED: {result.Message}";

            return $"{result.Message}\n{result.DataJson}";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[read_data] EXCEPTION: {ex}");
            return $"ERROR: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
