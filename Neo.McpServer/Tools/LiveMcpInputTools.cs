using System.ComponentModel;
using ModelContextProtocol.Server;
using Neo.McpServer.Services;

namespace Neo.McpServer.Tools;

/// <summary>
/// Phase 3 always-on tools for driving a running preview app's UI:
/// <list type="bullet">
///   <item><c>raise_event</c> — fires any Avalonia <see cref="Avalonia.Interactivity.RoutedEvent"/>
///         on a named control through the bubbling/tunneling pipeline. Use this when you need
///         the actual event semantics (handlers on parent controls run too).</item>
///   <item><c>simulate_input</c> — synthesizes a high-level interaction (click, focus, set_text,
///         key_press, type_text). For most UI-driving needs, this is what to call.</item>
/// </list>
///
/// <para>Both tools are static fallbacks that work even when no <c>[McpTriggerable]</c>
/// attributes are present in the loaded UserControl. Phase 3's main payoff is enabling
/// self-testing apps: Claude generates the app, then drives it to verify the behavior.</para>
/// </summary>
[McpServerToolType]
public sealed class LiveMcpInputTools
{
    [McpServerTool(Name = "raise_event")]
    [Description("Raises an Avalonia RoutedEvent on a named control in the running preview app. " +
        "Routes through the standard Avalonia bubbling/tunneling pipeline so any user handler on " +
        "the control or its ancestors runs as if the interaction were real. Use this when you need " +
        "the precise event-system semantics; for most UI-driving call simulate_input instead.")]
    public static async Task<string> RaiseEvent(
        PreviewSessionManager preview,
        [Description("Target control. Either an x:Name (\"submitButton\") or a Type[:Index] selector " +
            "(\"Button\", \"TextBox:1\"). Same lookup rules as set_property.")] string controlName,
        [Description("RoutedEvent name with or without trailing 'Event' (\"Click\" or \"ClickEvent\"). " +
            "Resolved by reflection on the control type and its base classes.")] string eventName,
        [Description("Optional JSON for richer event args. Currently reserved — pass '{}'.")] string argsJson = "{}",
        [Description("Window ID for multi-window mode. Omit for the default window.")] string? windowId = null)
    {
        var result = await preview.RaiseEventAsync(windowId, controlName, eventName, argsJson);
        if (result.Success) return $"OK: raised {eventName} on '{controlName}'.";
        return $"ERROR [{result.ErrorCode ?? "unknown"}]: {result.Error}";
    }

    [McpServerTool(Name = "simulate_input")]
    [Description("Synthesizes a UI interaction on a named control in the running preview app. " +
        "Supported kinds: " +
        "'click' (Button.Click or RoutedEvent), " +
        "'focus' (Control.Focus()), " +
        "'set_text' (sets Text or Content; params: {\"text\":\"…\"}), " +
        "'key_press' (KeyDown+KeyUp; params: {\"key\":\"Enter\"} — any Avalonia.Input.Key name), " +
        "'type_text' (one TextInputEvent per character; params: {\"text\":\"hello\"}). " +
        "All operations marshal to the UI thread.")]
    public static async Task<string> SimulateInput(
        PreviewSessionManager preview,
        [Description("Target control. x:Name or Type[:Index] selector — same as raise_event/set_property.")] string controlName,
        [Description("Interaction kind: click | focus | set_text | key_press | type_text.")] string kind,
        [Description("JSON params object — shape depends on kind. Pass '{}' for click/focus.")] string paramsJson = "{}",
        [Description("Window ID for multi-window mode. Omit for the default window.")] string? windowId = null)
    {
        var result = await preview.SimulateInputAsync(windowId, controlName, kind, paramsJson);
        if (result.Success) return $"OK: {kind} on '{controlName}'.";
        return $"ERROR [{result.ErrorCode ?? "unknown"}]: {result.Error}";
    }
}
