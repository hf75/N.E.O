namespace Neo.AssemblyForge;

public static class AssemblyForgeJsonSchemata
{
    public static string StructuredResponse = @" {
                              ""$schema"": ""http://json-schema.org/draft-07/schema#"",
                              ""title"": ""StructuredResponse"",
                              ""type"": ""object"",
                              ""properties"": {
                                ""Code"": { ""type"": ""string"" },
                                ""Patch"": { ""type"": ""string"" },
                                ""NuGetPackages"": {
                                  ""type"": ""array"",
                                  ""items"": { ""type"": ""string"" }
                                },
                                ""Explanation"": { ""type"": ""string"" },
                                ""Chat"": { ""type"": ""string"" },
                                ""PowerShellScript"": { ""type"": ""string"" },
                                ""ConsoleAppCode"": { ""type"": ""string"" }
                              },
                              ""required"": [ ""Code"", ""Patch"", ""NuGetPackages"", ""Explanation"", ""Chat"", ""PowerShellScript"", ""ConsoleAppCode"" ],
                              ""additionalProperties"": false
                            }";

    public static string PatchReviewResponse = @" {
                              ""$schema"": ""http://json-schema.org/draft-07/schema#"",
                              ""title"": ""PatchReviewResponse"",
                              ""type"": ""object"",
                              ""properties"": {
                                ""MatchesPrompt"": { ""type"": ""boolean"" },
                                ""PromptSummary"": { ""type"": ""string"" },
                                ""RiskLevel"": { ""type"": ""string"", ""enum"": [ ""safe"", ""caution"", ""dangerous"", ""unknown"" ] },
                                ""RiskSummary"": { ""type"": ""string"" },
                                ""Findings"": {
                                  ""type"": ""array"",
                                  ""items"": { ""type"": ""string"" }
                                },
                                ""SuggestedSafetyImprovements"": {
                                  ""type"": ""array"",
                                  ""items"": { ""type"": ""string"" }
                                }
                              },
                              ""required"": [ ""MatchesPrompt"", ""PromptSummary"", ""RiskLevel"", ""RiskSummary"", ""Findings"", ""SuggestedSafetyImprovements"" ],
                              ""additionalProperties"": false
                            }";
}
