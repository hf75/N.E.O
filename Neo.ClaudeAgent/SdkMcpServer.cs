using System.Text.Json;
using System.Text.Json.Nodes;

namespace Neo.ClaudeAgent;

/// <summary>
/// The MCP server of the tools that run in this process. Claude Code sends it JSON-RPC messages inside
/// mcp_message control requests on the session's own pipe, the way the official SDKs serve their in-process
/// tools, so no port and no second program are needed. It knows tools only.
/// </summary>
internal sealed class SdkMcpServer(string name, IReadOnlyList<ClaudeAgentTool> tools)
{
    public string Name => name;

    /// <summary>The JSON-RPC answer to <paramref name="message"/>; null for a notification, which gets none.</summary>
    public async Task<JsonObject?> HandleAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var id = message["id"]?.DeepClone();
        var method = (string?)message["method"];
        if (id == null || method == null)
            return null;

        try
        {
            JsonNode result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = (string?)message["params"]?["protocolVersion"] ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = name, ["version"] = "1.0.0" },
                },
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = new JsonArray(tools.Select(Describe).ToArray<JsonNode?>()) },
                "tools/call" => await CallAsync(message["params"] as JsonObject, cancellationToken).ConfigureAwait(false),
                _ => throw new MethodNotFound(method),
            };
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (MethodNotFound)
        {
            return Error(id, -32601, $"Method not found: {method}");
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            return Error(id, -32602, exception.Message);
        }
    }

    private static JsonObject Describe(ClaudeAgentTool tool) => new()
    {
        ["name"] = tool.Name,
        ["description"] = tool.Description,
        ["inputSchema"] = JsonNode.Parse(tool.InputSchema.GetRawText()),
    };

    private async Task<JsonNode> CallAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        var toolName = (string?)parameters?["name"] ?? throw new ArgumentException("tools/call without a tool name.");
        var tool = tools.FirstOrDefault(candidate => candidate.Name == toolName) ?? throw new ArgumentException($"There is no tool {toolName}.");
        using var input = JsonDocument.Parse(parameters?["arguments"]?.ToJsonString() ?? "{}");

        ClaudeAgentToolResult outcome;
        try
        {
            outcome = await tool.Run(input.RootElement.Clone(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Claude reads what went wrong and can try differently; the session goes on.
            outcome = new ClaudeAgentToolResult($"{exception.GetType().Name}: {exception.Message}", IsError: true);
        }

        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = outcome.Text });
        if (outcome.Image is { } image)
            content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(image.Data), ["mimeType"] = image.MediaType });
        return new JsonObject { ["content"] = content, ["isError"] = outcome.IsError };
    }

    private static JsonObject Error(JsonNode id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private sealed class MethodNotFound(string method) : Exception(method);
}
