using System;
using System.Text.Json.Serialization;

namespace Neo.App.WebApp.Services.Ai;

/// <summary>
/// Mirror of Neo's desktop StructuredResponse contract. The AI is asked to
/// produce a JSON object with these fields; any are optional.
/// </summary>
public sealed class StructuredResponse
{
    [JsonPropertyName("code")]        public string? Code { get; set; }
    [JsonPropertyName("patch")]       public string? Patch { get; set; }
    [JsonPropertyName("explanation")] public string? Explanation { get; set; }
    [JsonPropertyName("chat")]        public string? Chat { get; set; }
    /// <summary>
    /// Either shaped as array of strings ""id@version"" or array of objects
    /// {id, version}. Parser normalises both.
    /// </summary>
    [JsonPropertyName("nuget")]       public NuGetRef[]? NuGet { get; set; }
}

public sealed class NuGetRef
{
    [JsonPropertyName("id")]      public string Id { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}
