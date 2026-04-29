using System.Text;

namespace Neo.App.Mcp.Internal;

/// <summary>
/// Tool/resource naming for Frozen-Mode. Mirrors the Dev-Mode <c>LiveMcpToolRegistry</c>
/// conventions but without the per-app prefix — a Frozen EXE is one app, so tool names are
/// plain <c>add_item</c>, <c>complete_item</c>, etc. and resource URIs are <c>app://CompletedCount</c>.
/// </summary>
internal static class Naming
{
    /// <summary>PascalCase / camelCase → snake_case.</summary>
    public static string ToSnakeCase(string name)
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

    /// <summary>Canonical resource URI for an observable property.</summary>
    public static string BuildResourceUri(string observableName) => $"app://{observableName}";
}
