using FluentAssertions;
using Neo.App;
using Newtonsoft.Json;
using Xunit;

namespace Neo.App.Core.Tests;

public class StructuredResponseTests
{
    // ── Default construction ────────────────────────────────────────

    [Fact]
    public void DefaultConstruction_CodeIsEmptyString()
    {
        var response = new StructuredResponse();

        response.Code.Should().BeEmpty();
    }

    [Fact]
    public void DefaultConstruction_PatchIsEmptyString()
    {
        var response = new StructuredResponse();

        response.Patch.Should().BeEmpty();
    }

    [Fact]
    public void DefaultConstruction_NuGetPackagesIsEmptyList()
    {
        var response = new StructuredResponse();

        response.NuGetPackages.Should().NotBeNull();
        response.NuGetPackages.Should().BeEmpty();
    }

    [Fact]
    public void DefaultConstruction_ExplanationIsEmptyString()
    {
        var response = new StructuredResponse();

        response.Explanation.Should().BeEmpty();
    }

    [Fact]
    public void DefaultConstruction_ChatIsEmptyString()
    {
        var response = new StructuredResponse();

        response.Chat.Should().BeEmpty();
    }

    [Fact]
    public void DefaultConstruction_PowerShellScriptIsEmptyString()
    {
        var response = new StructuredResponse();

        response.PowerShellScript.Should().BeEmpty();
    }

    [Fact]
    public void DefaultConstruction_ConsoleAppCodeIsEmptyString()
    {
        var response = new StructuredResponse();

        response.ConsoleAppCode.Should().BeEmpty();
    }

    // ── JSON roundtrip ──────────────────────────────────────────────

    [Fact]
    public void JsonRoundtrip_PreservesAllFields()
    {
        var original = new StructuredResponse
        {
            Code = "Console.WriteLine();",
            Patch = "-old\n+new",
            NuGetPackages = new List<string> { "Newtonsoft.Json", "Dapper" },
            Explanation = "Added a print statement.",
            Chat = "Here is the code.",
            PowerShellScript = "Get-Process",
            ConsoleAppCode = "static void Main() { }",
        };

        var json = JsonConvert.SerializeObject(original);
        var deserialized = JsonConvert.DeserializeObject<StructuredResponse>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Code.Should().Be(original.Code);
        deserialized.Patch.Should().Be(original.Patch);
        deserialized.NuGetPackages.Should().BeEquivalentTo(original.NuGetPackages);
        deserialized.Explanation.Should().Be(original.Explanation);
        deserialized.Chat.Should().Be(original.Chat);
        deserialized.PowerShellScript.Should().Be(original.PowerShellScript);
        deserialized.ConsoleAppCode.Should().Be(original.ConsoleAppCode);
    }

    [Fact]
    public void JsonRoundtrip_EmptyResponse_Survives()
    {
        var original = new StructuredResponse();

        var json = JsonConvert.SerializeObject(original);
        var deserialized = JsonConvert.DeserializeObject<StructuredResponse>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Code.Should().BeEmpty();
        deserialized.NuGetPackages.Should().BeEmpty();
    }

    [Fact]
    public void JsonDeserialization_MissingFields_DefaultsApplied()
    {
        var json = "{}";
        var deserialized = JsonConvert.DeserializeObject<StructuredResponse>(json);

        deserialized.Should().NotBeNull();
    }
}
