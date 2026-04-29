using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Neo.App.Mcp.Internal;

/// <summary>
/// Frozen-Mode equivalent of Dev-Mode's <c>LiveMcpDynamicTool</c>: one MCP tool per
/// <c>[McpCallable]</c> method, building its JSON Schema from the manifest's parameter list.
/// Invocation goes straight to <see cref="InProcessDispatcher"/> — no IPC roundtrip.
///
/// <para>Tool name is plain snake_case (no <c>app.&lt;id&gt;.</c> prefix) since a Frozen EXE is one app.</para>
/// </summary>
internal sealed class DynamicTool : McpServerTool
{
    private readonly CallableEntry _entry;
    private readonly InProcessDispatcher _dispatcher;
    private readonly Tool _protocolTool;

    public DynamicTool(CallableEntry entry, InProcessDispatcher dispatcher)
    {
        _entry = entry;
        _dispatcher = dispatcher;

        _protocolTool = new Tool
        {
            Name = Naming.ToSnakeCase(entry.Name),
            Description = BuildDescription(entry),
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
        var result = await _dispatcher.InvokeMethodAsync(_entry, argsJson, cancellationToken);

        if (result.Success)
        {
            var text = string.IsNullOrEmpty(result.ResultJson) ? "OK (no return value)." : result.ResultJson!;
            return new CallToolResult { IsError = false, Content = [new TextContentBlock { Text = text }] };
        }
        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = $"ERROR: {result.Error}" }] };
    }

    // ── Schema generation (mirrors Dev-Mode mapping for cross-mode consistency) ────

    private static JsonElement BuildInputSchema(CallableEntry entry)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var p in entry.Parameters)
        {
            properties[p.Name] = NetTypeToJsonSchemaProp(p.TypeName);
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

    private static string MarshalArgsToPositionalArray(CallableEntry entry, IDictionary<string, JsonElement>? args)
    {
        var arr = new JsonArray();
        foreach (var p in entry.Parameters)
            arr.Add(args != null && args.TryGetValue(p.Name, out var element)
                ? JsonNode.Parse(element.GetRawText())
                : null);
        return arr.ToJsonString(JsonOptions.Default);
    }

    private static string BuildDescription(CallableEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrEmpty(entry.Description) ? $"Calls method '{entry.Name}'." : entry.Description);
        if (entry.OffUiThread) sb.Append("  [OffUiThread]");
        if (entry.TimeoutSeconds != 30) sb.Append("  [Timeout=").Append(entry.TimeoutSeconds).Append("s]");
        return sb.ToString();
    }
}
