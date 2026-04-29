using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Neo.IPC;
using Neo.McpServer.Services;

namespace Neo.McpServer.LiveMcp;

/// <summary>
/// One MCP tool that fronts a single <c>[McpCallable]</c> method on a running preview app.
/// Created by <see cref="LiveMcpToolRegistry"/> when an <see cref="AppManifestMessage"/> arrives;
/// removed when the app stops or its manifest changes.
///
/// <para>Tool name follows VISION.md §2.4: <c>app.&lt;windowId&gt;.&lt;snake_method&gt;</c>.</para>
///
/// <para>The JSON Schema for the tool's input is built from the manifest's parameter list, so Claude
/// gets typed, named parameters instead of the stringly-typed <c>argsJson</c> shape that
/// <c>invoke_method</c> exposes. Args from <see cref="CallToolRequestParams.Arguments"/> are
/// re-marshalled to a positional JSON array and forwarded through
/// <see cref="PreviewSessionManager.InvokeAppMethodAsync"/>, which preserves all of Phase-1's
/// loop-protection, timeout, and dispatcher semantics.</para>
/// </summary>
internal sealed class LiveMcpDynamicTool : McpServerTool
{
    private readonly string _appId;
    private readonly string _toolName;
    private readonly McpCallableEntry _entry;
    private readonly PreviewSessionManager _preview;
    private readonly Tool _protocolTool;

    public LiveMcpDynamicTool(string appId, string toolName, McpCallableEntry entry, PreviewSessionManager preview)
    {
        _appId = appId;
        _toolName = toolName;
        _entry = entry;
        _preview = preview;

        _protocolTool = new Tool
        {
            Name = toolName,
            Description = BuildDescription(appId, entry),
            InputSchema = BuildInputSchema(entry)
        };
    }

    public override Tool ProtocolTool => _protocolTool;

    public override IReadOnlyList<object> Metadata { get; } = Array.Empty<object>();

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var argsJson = MarshalArgsToPositionalArray(_entry, request.Params?.Arguments);
        var result = await _preview.InvokeAppMethodAsync(
            windowId: _appId,
            method: _entry.Name,
            argsJson: argsJson,
            ct: cancellationToken);

        if (result.Success)
        {
            var text = string.IsNullOrEmpty(result.ResultJson)
                ? "OK (no return value)."
                : result.ResultJson!;
            return new CallToolResult
            {
                IsError = false,
                Content = [new TextContentBlock { Text = text }]
            };
        }

        var code = result.ErrorCode ?? "unknown";
        var msg = result.Error ?? "Unknown error.";
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"ERROR [{code}]: {msg}" }]
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Schema generation
    // ─────────────────────────────────────────────────────────────────────────

    private static JsonElement BuildInputSchema(McpCallableEntry entry)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var p in entry.Parameters)
        {
            properties[p.Name] = NetTypeToJsonSchemaProp(p.TypeName);
            // Live-MCP v1: every manifest parameter is required. Optional/default-valued
            // parameters need an attribute extension before they can be flagged here.
            required.Add(p.Name);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };

        return JsonSerializer.SerializeToElement(schema);
    }

    private static JsonObject NetTypeToJsonSchemaProp(string netTypeFullName)
    {
        // Map common .NET types to JSON Schema. Anything unrecognized falls through to
        // "object" — Claude will still pass the value through as JSON.
        var (jsonType, isInteger) = netTypeFullName switch
        {
            "System.String" => ("string", false),
            "System.Boolean" => ("boolean", false),
            "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16"
                or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64" => ("integer", true),
            "System.Single" or "System.Double" or "System.Decimal" => ("number", false),
            _ => ("object", false)
        };

        var prop = new JsonObject { ["type"] = jsonType };
        if (isInteger) prop["format"] = "int64";
        return prop;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Args marshaling: Claude's named-args dict → positional JSON array (the shape
    // LiveMcpDispatcher.DeserializeArgs expects on the app side).
    // ─────────────────────────────────────────────────────────────────────────

    private static string MarshalArgsToPositionalArray(McpCallableEntry entry, IDictionary<string, JsonElement>? args)
    {
        var arr = new JsonArray();
        foreach (var p in entry.Parameters)
        {
            if (args != null && args.TryGetValue(p.Name, out var element))
                arr.Add(JsonNode.Parse(element.GetRawText()));
            else
                arr.Add(null);
        }
        return arr.ToJsonString(Json.Options);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Description: prepend the [McpCallable] description with a hint about
    // origin so Claude knows which app this tool belongs to.
    // ─────────────────────────────────────────────────────────────────────────

    private static string BuildDescription(string appId, McpCallableEntry entry)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(entry.Description))
            sb.Append(entry.Description);
        else
            sb.Append($"Calls method '{entry.Name}' on the running preview app.");
        sb.Append("  [Live-MCP app=").Append(appId).Append(']');
        if (entry.OffUiThread) sb.Append("  [OffUiThread]");
        if (entry.TimeoutSeconds != 30) sb.Append("  [Timeout=").Append(entry.TimeoutSeconds).Append("s]");
        return sb.ToString();
    }
}
