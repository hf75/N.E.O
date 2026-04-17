using System.Collections.Generic;

namespace Neo.AssemblyForge;

/// <summary>
/// The shape every Neo surface (desktop host, MCP server, Web App) expects
/// the AI to return. Fields are optional; consumers pick the ones they care
/// about. Property names are PascalCase by C# convention — deserialize with
/// <c>PropertyNameCaseInsensitive = true</c> so both PascalCase (desktop +
/// MCP wire format) and camelCase (Web App wire format) inputs parse cleanly.
/// </summary>
public sealed class StructuredResponse
{
    public string Code { get; set; } = string.Empty;
    public string Patch { get; set; } = string.Empty;
    public List<string> NuGetPackages { get; set; } = new();
    public string Explanation { get; set; } = string.Empty;
    public string Chat { get; set; } = string.Empty;
    public string PowerShellScript { get; set; } = string.Empty;
    public string ConsoleAppCode { get; set; } = string.Empty;
}
