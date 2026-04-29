using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Neo.App.Mcp.Internal;

/// <summary>
/// Frozen-Mode "raise_event" tool. Static: always present, doesn't depend on the manifest.
/// Routes through <see cref="InputDispatcher.RaiseEventAsync"/> exactly like Dev-Mode.
/// </summary>
internal sealed class RaiseEventTool : McpServerTool
{
    private readonly InputDispatcher _input;
    private readonly Tool _protocolTool;

    public RaiseEventTool(InputDispatcher input)
    {
        _input = input;

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["control_name"] = new JsonObject { ["type"] = "string" },
                ["event_name"] = new JsonObject { ["type"] = "string" }
            },
            ["required"] = new JsonArray { "control_name", "event_name" }
        };

        _protocolTool = new Tool
        {
            Name = "raise_event",
            Description =
                "Raises an Avalonia RoutedEvent on a named control in the running app. " +
                "Routes through the standard bubbling/tunneling pipeline so any user handler on " +
                "the control or its ancestors runs as if the interaction were real. Use this when " +
                "you need the precise event-system semantics; for most UI-driving call simulate_input instead.",
            InputSchema = JsonSerializer.SerializeToElement(schema)
        };
    }

    public override Tool ProtocolTool => _protocolTool;
    public override IReadOnlyList<object> Metadata { get; } = Array.Empty<object>();

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var args = request.Params?.Arguments;
        var controlName = ReadString(args, "control_name");
        var eventName = ReadString(args, "event_name");
        if (string.IsNullOrEmpty(controlName) || string.IsNullOrEmpty(eventName))
            return Err("Both 'control_name' and 'event_name' are required.");

        var result = await _input.RaiseEventAsync(controlName, eventName);
        return result.Success
            ? Ok($"OK: raised {eventName} on '{controlName}'.")
            : Err(result.Error ?? "unknown error");
    }

    internal static string? ReadString(IDictionary<string, JsonElement>? args, string key) =>
        args != null && args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    internal static CallToolResult Ok(string text) =>
        new() { IsError = false, Content = [new TextContentBlock { Text = text }] };

    internal static CallToolResult Err(string text) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = $"ERROR: {text}" }] };
}

/// <summary>
/// Frozen-Mode "simulate_input" tool. See Dev-Mode <c>LiveMcpInputTools.SimulateInput</c> for
/// the full kind list and per-kind parameter shapes.
/// </summary>
internal sealed class SimulateInputTool : McpServerTool
{
    private readonly InputDispatcher _input;
    private readonly Tool _protocolTool;

    public SimulateInputTool(InputDispatcher input)
    {
        _input = input;

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["control_name"] = new JsonObject { ["type"] = "string" },
                ["kind"] = new JsonObject { ["type"] = "string" },
                ["params_json"] = new JsonObject { ["type"] = "string" }
            },
            ["required"] = new JsonArray { "control_name", "kind" }
        };

        _protocolTool = new Tool
        {
            Name = "simulate_input",
            Description =
                "Synthesizes a UI interaction on a named control. Kinds: " +
                "click (Button.Click or RoutedEvent), focus (Control.Focus()), " +
                "set_text (sets Text or Content; params: {\"text\":\"…\"}), " +
                "key_press (KeyDown+KeyUp; params: {\"key\":\"Enter\"}), " +
                "type_text (one TextInputEvent per character; params: {\"text\":\"hello\"}). " +
                "All operations marshal to the UI thread.",
            InputSchema = JsonSerializer.SerializeToElement(schema)
        };
    }

    public override Tool ProtocolTool => _protocolTool;
    public override IReadOnlyList<object> Metadata { get; } = Array.Empty<object>();

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var args = request.Params?.Arguments;
        var controlName = RaiseEventTool.ReadString(args, "control_name");
        var kind = RaiseEventTool.ReadString(args, "kind");
        var paramsJson = RaiseEventTool.ReadString(args, "params_json") ?? "{}";

        if (string.IsNullOrEmpty(controlName) || string.IsNullOrEmpty(kind))
            return RaiseEventTool.Err("Both 'control_name' and 'kind' are required.");

        var result = await _input.SimulateInputAsync(controlName, kind, paramsJson);
        return result.Success
            ? RaiseEventTool.Ok($"OK: {kind} on '{controlName}'.")
            : RaiseEventTool.Err(result.Error ?? "unknown error");
    }
}
