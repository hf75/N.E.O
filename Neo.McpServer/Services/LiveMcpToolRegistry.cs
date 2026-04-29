using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Server;
using Neo.IPC;
using Neo.McpServer.LiveMcp;

namespace Neo.McpServer.Services;

/// <summary>
/// Phase 2 dynamic-tools registry: owns the <see cref="McpServerPrimitiveCollection{T}"/> that
/// the MCP server augments its always-on tool set with, and maps each running preview app to
/// a deterministic per-method tool name (<c>app.&lt;windowId&gt;.&lt;snake_method&gt;</c>).
///
/// <para><b>Hot-reload behaviour (M2.4):</b> on every manifest update we hash each callable's
/// signature (class + method + ordered param types). Tools whose hash didn't change stay
/// registered as-is, so a hot-reload that only changed method bodies fires no
/// <c>notifications/tools/list_changed</c> traffic. Tools with changed signatures are
/// remove+add'd, generating exactly one notification per actual API change. This matters
/// because Claude Code CLI's deferred-schema-fetch (Phase 0 finding) is a network round-trip
/// per surfaced tool — chatty re-registrations hurt UX.</para>
///
/// <para>Adds and removes raise <see cref="McpServerPrimitiveCollection{T}.Changed"/>; the SDK
/// forwards that to <c>notifications/tools/list_changed</c>. The capability flag
/// <c>Tools.ListChanged = true</c> must be set on <see cref="ModelContextProtocol.Server.McpServerOptions"/>
/// for clients to honour it.</para>
/// </summary>
public sealed class LiveMcpToolRegistry
{
    private readonly McpServerPrimitiveCollection<McpServerTool> _toolCollection = new();

    // appId → (toolName → registered entry)
    private readonly ConcurrentDictionary<string, Dictionary<string, RegisteredEntry>> _byApp =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _mutateGate = new();

    /// <summary>The collection passed to <c>McpServerOptions.ToolCollection</c>.</summary>
    public McpServerPrimitiveCollection<McpServerTool> ToolCollection => _toolCollection;

    /// <summary>Diagnostic: tool names currently registered (across all apps).</summary>
    public IReadOnlyList<string> GetRegisteredToolNames() => _toolCollection.PrimitiveNames.ToList();

    /// <summary>Diagnostic: tool names currently registered for one specific app.</summary>
    public IReadOnlyList<string> GetRegisteredToolNamesForApp(string appId) =>
        _byApp.TryGetValue(appId, out var map)
            ? map.Keys.ToList()
            : Array.Empty<string>();

    /// <summary>
    /// Register or refresh tools for an app from its current manifest.
    ///
    /// <para>Per-method hash dedup: tools whose signatures match the previous manifest stay
    /// registered (no list_changed traffic). Removed/changed methods are unregistered;
    /// added/changed methods are registered.</para>
    /// </summary>
    public void RegisterApp(string appId, AppManifestMessage manifest, PreviewSessionManager preview)
    {
        if (string.IsNullOrEmpty(appId)) throw new ArgumentException("appId required.", nameof(appId));
        if (manifest == null) throw new ArgumentNullException(nameof(manifest));
        if (preview == null) throw new ArgumentNullException(nameof(preview));

        var sanitized = SanitizeAppId(appId);
        var desired = new Dictionary<string, (string hash, McpCallableEntry entry)>(StringComparer.Ordinal);
        foreach (var c in manifest.Callables)
        {
            var name = $"app.{sanitized}.{ToSnakeCase(c.Name)}";
            var hash = HashCallable(manifest.ClassFullName, c);
            desired[name] = (hash, c);
        }

        lock (_mutateGate)
        {
            var existing = _byApp.GetOrAdd(appId, _ => new Dictionary<string, RegisteredEntry>(StringComparer.Ordinal));

            // Phase 1: remove tools that disappeared or whose signature changed.
            var toRemove = existing
                .Where(kv => !desired.TryGetValue(kv.Key, out var d) || d.hash != kv.Value.Hash)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var name in toRemove)
            {
                if (existing.TryGetValue(name, out var rt))
                {
                    _toolCollection.Remove(rt.Tool);
                    existing.Remove(name);
                }
            }

            // Phase 2: add tools that are new or whose signature changed.
            foreach (var (name, (hash, entry)) in desired)
            {
                if (existing.ContainsKey(name)) continue; // unchanged — keep existing instance
                var tool = new LiveMcpDynamicTool(appId, name, entry, preview);
                _toolCollection.Add(tool);
                existing[name] = new RegisteredEntry(hash, tool);
            }
        }
    }

    /// <summary>Unregister all tools for an app — called on preview stop / process exit.</summary>
    public void UnregisterApp(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return;
        lock (_mutateGate)
        {
            if (!_byApp.TryRemove(appId, out var map)) return;
            foreach (var rt in map.Values)
                _toolCollection.Remove(rt.Tool);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    private sealed record RegisteredEntry(string Hash, McpServerTool Tool);

    /// <summary>
    /// Stable per-method hash. Inputs: declaring class FullName, method name, ordered
    /// (name, .NET type) pairs of parameters, return type. Body changes don't affect the hash —
    /// that is the whole point of M2.4.
    /// </summary>
    internal static string HashCallable(string classFullName, McpCallableEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append(classFullName).Append('|');
        sb.Append(entry.Name).Append('|');
        foreach (var p in entry.Parameters)
            sb.Append(p.Name).Append(':').Append(p.TypeName).Append(',');
        sb.Append("->").Append(entry.ReturnTypeName);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes.AsSpan(0, 6));
    }

    /// <summary>
    /// Strip everything that's not <c>[a-z0-9_]</c> and lowercase. Empty results fall back to "app".
    /// </summary>
    internal static string SanitizeAppId(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch >= 'a' && ch <= 'z') sb.Append(ch);
            else if (ch >= 'A' && ch <= 'Z') sb.Append((char)(ch + ('a' - 'A')));
            else if (ch >= '0' && ch <= '9') sb.Append(ch);
            else if (ch == '_') sb.Append('_');
        }
        return sb.Length == 0 ? "app" : sb.ToString();
    }

    /// <summary>
    /// PascalCase / camelCase → snake_case. Inserts <c>_</c> before each uppercase letter that
    /// follows a lowercase letter or digit, then lowercases the whole thing.
    /// <c>AddItem</c> → <c>add_item</c>; <c>RefreshFromAPI</c> → <c>refresh_from_api</c>.
    /// </summary>
    internal static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (i > 0 && char.IsUpper(ch))
            {
                var prev = name[i - 1];
                var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (char.IsLower(prev) || char.IsDigit(prev) || nextIsLower)
                    sb.Append('_');
            }
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }
}
