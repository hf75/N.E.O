using FluentAssertions;
using Xunit;

namespace Neo.AssemblyForge.Tests;

public class AssemblyForgeHistoryFormatterTests
{
    [Fact]
    public void StructuredResponseToText_WithPatch_WrapsInDiffBlock()
    {
        var response = new StructuredResponse
        {
            Patch = "--- a/file\n+++ b/file\n@@ -1,1 +1,1 @@\n-old\n+new",
        };

        var result = AssemblyForgeHistoryFormatter.StructuredResponseToText(response);

        result.Should().Contain("```diff");
        result.Should().Contain("Patch:");
        result.Should().Contain("```");
    }

    [Fact]
    public void StructuredResponseToText_WithCodeNoPatch_WrapsInCsharpBlock()
    {
        var response = new StructuredResponse
        {
            Code = "class Foo {}",
        };

        var result = AssemblyForgeHistoryFormatter.StructuredResponseToText(response);

        result.Should().Contain("```csharp");
        result.Should().Contain("Code:");
        result.Should().Contain("class Foo {}");
    }

    [Fact]
    public void StructuredResponseToText_WithBothPatchAndCode_PatchTakesPrecedence()
    {
        var response = new StructuredResponse
        {
            Patch = "@@ -1 +1 @@\n-old\n+new",
            Code = "class Foo {}",
        };

        var result = AssemblyForgeHistoryFormatter.StructuredResponseToText(response);

        result.Should().Contain("```diff");
        result.Should().NotContain("```csharp");
    }

    [Fact]
    public void StructuredResponseToText_WithNuGetPackages_AppendsPackages()
    {
        var response = new StructuredResponse
        {
            Code = "class Foo {}",
            NuGetPackages = new List<string> { "Newtonsoft.Json", "Humanizer" },
        };

        var result = AssemblyForgeHistoryFormatter.StructuredResponseToText(response);

        result.Should().Contain("Used nuget packages:");
        result.Should().Contain("Newtonsoft.Json");
        result.Should().Contain("Humanizer");
    }

    [Fact]
    public void StructuredResponseToText_WithExplanation_AppendsExplanation()
    {
        var response = new StructuredResponse
        {
            Code = "class Foo {}",
            Explanation = "Added a new class",
        };

        var result = AssemblyForgeHistoryFormatter.StructuredResponseToText(response);

        result.Should().Contain("Explanation:");
        result.Should().Contain("Added a new class");
    }

    [Fact]
    public void StructuredResponseToText_EmptyNuGetPackages_NotAppended()
    {
        var response = new StructuredResponse
        {
            Code = "class Foo {}",
            NuGetPackages = new List<string>(),
        };

        var result = AssemblyForgeHistoryFormatter.StructuredResponseToText(response);

        result.Should().NotContain("Used nuget packages:");
    }

    [Fact]
    public void StructuredResponseToText_EmptyExplanation_NotAppended()
    {
        var response = new StructuredResponse
        {
            Code = "class Foo {}",
            Explanation = "   ",
        };

        var result = AssemblyForgeHistoryFormatter.StructuredResponseToText(response);

        result.Should().NotContain("Explanation:");
    }

    [Fact]
    public void StructuredResponseToText_NullResponse_ReturnsEmptyString()
    {
        var result = AssemblyForgeHistoryFormatter.StructuredResponseToText(null!);

        result.Should().BeEmpty();
    }

    [Fact]
    public void StructuredResponseToText_IndentedCode_IsNormalized()
    {
        var response = new StructuredResponse
        {
            Code = "    line1\n    line2\n    line3",
        };

        var result = AssemblyForgeHistoryFormatter.StructuredResponseToText(response);

        result.Should().Contain("line1\nline2\nline3");
    }

    [Fact]
    public void StructuredResponseToText_EmptyCodeAndPatch_ReturnsOnlyAdditionalSections()
    {
        var response = new StructuredResponse
        {
            Explanation = "Some explanation",
        };

        var result = AssemblyForgeHistoryFormatter.StructuredResponseToText(response);

        result.Should().NotContain("```csharp");
        result.Should().NotContain("```diff");
        result.Should().Contain("Explanation:");
    }
}
