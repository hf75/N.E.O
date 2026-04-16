using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Neo.AssemblyForge.Tests;

public class JsonSchemataTests
{
    [Fact]
    public void StructuredResponseSchema_IsValidJson()
    {
        var act = () => JObject.Parse(AssemblyForgeJsonSchemata.StructuredResponse);

        act.Should().NotThrow();
    }

    [Fact]
    public void StructuredResponseSchema_ContainsAllSevenRequiredFields()
    {
        var schema = JObject.Parse(AssemblyForgeJsonSchemata.StructuredResponse);

        var required = schema["required"]!.ToObject<List<string>>()!;

        required.Should().HaveCount(7);
        required.Should().Contain("Code");
        required.Should().Contain("Patch");
        required.Should().Contain("NuGetPackages");
        required.Should().Contain("Explanation");
        required.Should().Contain("Chat");
        required.Should().Contain("PowerShellScript");
        required.Should().Contain("ConsoleAppCode");
    }

    [Fact]
    public void StructuredResponseSchema_PropertiesMatchRequired()
    {
        var schema = JObject.Parse(AssemblyForgeJsonSchemata.StructuredResponse);

        var properties = (JObject)schema["properties"]!;
        var required = schema["required"]!.ToObject<List<string>>()!;

        foreach (var field in required)
        {
            properties.Should().ContainKey(field);
        }
    }

    [Fact]
    public void StructuredResponseSchema_HasTitle()
    {
        var schema = JObject.Parse(AssemblyForgeJsonSchemata.StructuredResponse);

        schema["title"]!.ToString().Should().Be("StructuredResponse");
    }

    [Fact]
    public void PatchReviewResponseSchema_IsValidJson()
    {
        var act = () => JObject.Parse(AssemblyForgeJsonSchemata.PatchReviewResponse);

        act.Should().NotThrow();
    }

    [Fact]
    public void PatchReviewResponseSchema_ContainsAllRequiredFields()
    {
        var schema = JObject.Parse(AssemblyForgeJsonSchemata.PatchReviewResponse);

        var required = schema["required"]!.ToObject<List<string>>()!;

        required.Should().HaveCount(6);
        required.Should().Contain("MatchesPrompt");
        required.Should().Contain("PromptSummary");
        required.Should().Contain("RiskLevel");
        required.Should().Contain("RiskSummary");
        required.Should().Contain("Findings");
        required.Should().Contain("SuggestedSafetyImprovements");
    }

    [Fact]
    public void PatchReviewResponseSchema_PropertiesMatchRequired()
    {
        var schema = JObject.Parse(AssemblyForgeJsonSchemata.PatchReviewResponse);

        var properties = (JObject)schema["properties"]!;
        var required = schema["required"]!.ToObject<List<string>>()!;

        foreach (var field in required)
        {
            properties.Should().ContainKey(field);
        }
    }

    [Fact]
    public void PatchReviewResponseSchema_RiskLevel_HasEnum()
    {
        var schema = JObject.Parse(AssemblyForgeJsonSchemata.PatchReviewResponse);

        var riskLevel = (JObject)schema["properties"]!["RiskLevel"]!;
        var enumValues = riskLevel["enum"]!.ToObject<List<string>>()!;

        enumValues.Should().Contain("safe");
        enumValues.Should().Contain("caution");
        enumValues.Should().Contain("dangerous");
        enumValues.Should().Contain("unknown");
    }

    [Fact]
    public void PatchReviewResponseSchema_HasTitle()
    {
        var schema = JObject.Parse(AssemblyForgeJsonSchemata.PatchReviewResponse);

        schema["title"]!.ToString().Should().Be("PatchReviewResponse");
    }
}
