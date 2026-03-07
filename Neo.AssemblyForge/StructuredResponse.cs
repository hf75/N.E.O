using Newtonsoft.Json;
using System.Collections.Generic;

namespace Neo.AssemblyForge;

public sealed class StructuredResponse
{
    [JsonProperty("Code")]
    public string Code { get; set; } = string.Empty;

    [JsonProperty("Patch")]
    public string Patch { get; set; } = string.Empty;

    [JsonProperty("NuGetPackages")]
    public List<string> NuGetPackages { get; set; } = new();

    [JsonProperty("Explanation")]
    public string Explanation { get; set; } = string.Empty;

    [JsonProperty("Chat")]
    public string Chat { get; set; } = string.Empty;

    [JsonProperty("PowerShellScript")]
    public string PowerShellScript { get; set; } = string.Empty;

    [JsonProperty("ConsoleAppCode")]
    public string ConsoleAppCode { get; set; } = string.Empty;
}
