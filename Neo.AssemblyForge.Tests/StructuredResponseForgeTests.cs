using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace Neo.AssemblyForge.Tests;

public class StructuredResponseForgeTests
{
    [Fact]
    public void DefaultConstruction_AllFieldsAreDefaults()
    {
        var response = new StructuredResponse();

        response.Code.Should().Be(string.Empty);
        response.Patch.Should().Be(string.Empty);
        response.NuGetPackages.Should().BeEmpty();
        response.Explanation.Should().Be(string.Empty);
        response.Chat.Should().Be(string.Empty);
        response.PowerShellScript.Should().Be(string.Empty);
        response.ConsoleAppCode.Should().Be(string.Empty);
    }

    [Fact]
    public void JsonSerialization_Roundtrip_PreservesAllFields()
    {
        var original = new StructuredResponse
        {
            Code = "class X {}",
            Patch = "@@ -1 +1 @@",
            NuGetPackages = new List<string> { "Pkg1", "Pkg2" },
            Explanation = "Did stuff",
            Chat = "Hello",
            PowerShellScript = "Get-Process",
            ConsoleAppCode = "Console.WriteLine();",
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
    public void JsonDeserialization_MissingFields_UsesDefaults()
    {
        var json = """{"Code":"hello"}""";

        var response = JsonConvert.DeserializeObject<StructuredResponse>(json);

        response.Should().NotBeNull();
        response!.Code.Should().Be("hello");
        response.Patch.Should().Be(string.Empty);
        response.NuGetPackages.Should().BeEmpty();
        response.Explanation.Should().Be(string.Empty);
        response.Chat.Should().Be(string.Empty);
        response.PowerShellScript.Should().Be(string.Empty);
        response.ConsoleAppCode.Should().Be(string.Empty);
    }

    [Fact]
    public void JsonDeserialization_EmptyObject_AllDefaults()
    {
        var json = "{}";

        var response = JsonConvert.DeserializeObject<StructuredResponse>(json);

        response.Should().NotBeNull();
        response!.Code.Should().Be(string.Empty);
        response.Patch.Should().Be(string.Empty);
        response.NuGetPackages.Should().BeEmpty();
    }

    [Fact]
    public void AllSevenProperties_AreAccessible()
    {
        var response = new StructuredResponse
        {
            Code = "a",
            Patch = "b",
            NuGetPackages = new List<string> { "c" },
            Explanation = "d",
            Chat = "e",
            PowerShellScript = "f",
            ConsoleAppCode = "g",
        };

        response.Code.Should().Be("a");
        response.Patch.Should().Be("b");
        response.NuGetPackages.Should().ContainSingle("c");
        response.Explanation.Should().Be("d");
        response.Chat.Should().Be("e");
        response.PowerShellScript.Should().Be("f");
        response.ConsoleAppCode.Should().Be("g");
    }

    [Fact]
    public void JsonPropertyAttributes_MapCorrectly()
    {
        var json = """
        {
            "Code": "c1",
            "Patch": "p1",
            "NuGetPackages": ["n1"],
            "Explanation": "e1",
            "Chat": "ch1",
            "PowerShellScript": "ps1",
            "ConsoleAppCode": "ca1"
        }
        """;

        var response = JsonConvert.DeserializeObject<StructuredResponse>(json);

        response.Should().NotBeNull();
        response!.Code.Should().Be("c1");
        response.Patch.Should().Be("p1");
        response.NuGetPackages.Should().ContainSingle("n1");
        response.Explanation.Should().Be("e1");
        response.Chat.Should().Be("ch1");
        response.PowerShellScript.Should().Be("ps1");
        response.ConsoleAppCode.Should().Be("ca1");
    }
}
