using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Neo.IPC;
using Neo.McpServer.Services;

namespace Neo.McpServer.Tools;

/// <summary>
/// Live-MCP Phase 1 meta-tools. These three tools are static (always present) and
/// give Claude a generic way to drive any annotated Neo app:
///
/// <list type="bullet">
///   <item><c>inspect_app_api</c> — list the manifest the app emitted at load time</item>
///   <item><c>invoke_method</c>   — call any [McpCallable] method by name with JSON args</item>
///   <item><c>read_observable</c> — read any [McpObservable] property by name</item>
/// </list>
///
/// Phase 2 will add per-method tools (<c>app.&lt;id&gt;.&lt;method&gt;</c>) that wrap the same
/// underlying invocation path; these meta-tools remain as the always-on fallback.
/// </summary>
[McpServerToolType]
public sealed class LiveMcpTools
{
    [McpServerTool(Name = "inspect_app_api")]
    [Description("Lists the Live-MCP manifest of a running preview app: callable methods, " +
        "observable properties, and triggerable controls (with their [McpCallable]/[McpObservable]/" +
        "[McpTriggerable] descriptions). Use this to discover what the app exposes before calling " +
        "invoke_method or read_observable. Pass windowId to target a specific window in multi-window mode.")]
    public static string InspectAppApi(
        PreviewSessionManager preview,
        [Description("Window ID for multi-window mode. Omit for the default window.")] string? windowId = null)
    {
        var manifest = preview.GetManifest(windowId);
        if (manifest == null)
        {
            var running = preview.GetRunningAppIds();
            if (running.Count == 0)
                return "No preview app is running. Use compile_and_preview first.";
            return $"No Live-MCP manifest available for windowId='{windowId ?? "default"}'. " +
                   $"The loaded app may not have any [McpCallable]/[McpObservable]/[McpTriggerable] members. " +
                   $"Running apps: {string.Join(", ", running)}.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"App: {manifest.AppId}  ({manifest.ClassFullName})");
        sb.AppendLine();

        sb.AppendLine($"Callables ({manifest.Callables.Count}):");
        if (manifest.Callables.Count == 0) sb.AppendLine("  (none)");
        foreach (var c in manifest.Callables)
        {
            var paramList = string.Join(", ", c.Parameters.Select(p => $"{p.TypeName} {p.Name}"));
            var ret = c.ReturnTypeName == "System.Void" ? "void" : c.ReturnTypeName;
            var flags = new List<string>();
            if (c.OffUiThread) flags.Add("OffUiThread");
            if (c.TimeoutSeconds != 30) flags.Add($"Timeout={c.TimeoutSeconds}s");
            var flagStr = flags.Count > 0 ? $"  [{string.Join(", ", flags)}]" : "";
            sb.AppendLine($"  {ret} {c.Name}({paramList}){flagStr}");
            if (!string.IsNullOrEmpty(c.Description)) sb.AppendLine($"    — {c.Description}");
        }
        sb.AppendLine();

        sb.AppendLine($"Observables ({manifest.Observables.Count}):");
        if (manifest.Observables.Count == 0) sb.AppendLine("  (none)");
        foreach (var o in manifest.Observables)
        {
            var watchable = o.Watchable ? "  [Watchable]" : "";
            sb.AppendLine($"  {o.TypeName} {o.Name}{watchable}");
            if (!string.IsNullOrEmpty(o.Description)) sb.AppendLine($"    — {o.Description}");
        }
        sb.AppendLine();

        sb.AppendLine($"Triggerables ({manifest.Triggerables.Count}):");
        if (manifest.Triggerables.Count == 0) sb.AppendLine("  (none)");
        foreach (var t in manifest.Triggerables)
        {
            sb.AppendLine($"  {t.ControlType} {t.Name}  (control name: '{t.ControlName}')");
            if (!string.IsNullOrEmpty(t.Description)) sb.AppendLine($"    — {t.Description}");
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "invoke_method")]
    [Description("Invokes an [McpCallable] method on the running preview app by name. " +
        "Pass arguments as a JSON array string matching the parameter order from inspect_app_api. " +
        "Returns the JSON-serialized return value on success, or an error message with code on failure. " +
        "Subject to Live-MCP loop protection: a chain of invoke_method ⇄ Ai.Trigger calls is capped at " +
        "5 hops by default (configurable via NEO_LIVEMCP_MAX_DEPTH).")]
    public static async Task<string> InvokeMethod(
        PreviewSessionManager preview,
        [Description("Method name as listed by inspect_app_api.")] string method,
        [Description("JSON array of positional arguments matching the method signature, e.g. '[\"hello\", 42]'. " +
            "Pass '[]' for parameterless methods.")] string argsJson = "[]",
        [Description("Window ID for multi-window mode. Omit for the default window.")] string? windowId = null)
    {
        var result = await preview.InvokeAppMethodAsync(windowId, method, argsJson);
        if (result.Success)
            return string.IsNullOrEmpty(result.ResultJson)
                ? "OK (no return value)."
                : $"OK: {result.ResultJson}";

        return $"ERROR [{result.ErrorCode ?? "unknown"}]: {result.Error}";
    }

    [McpServerTool(Name = "read_observable")]
    [Description("Reads the current value of an [McpObservable] property on the running preview app. " +
        "Returns the JSON-serialized value, or an error message on failure. " +
        "Use inspect_app_api to discover available observable names.")]
    public static async Task<string> ReadObservable(
        PreviewSessionManager preview,
        [Description("Property name as listed by inspect_app_api.")] string observable,
        [Description("Window ID for multi-window mode. Omit for the default window.")] string? windowId = null)
    {
        var result = await preview.ReadAppObservableAsync(windowId, observable);
        if (result.Success)
            return result.ValueJson ?? "null";

        return $"ERROR: {result.Error}";
    }
}
