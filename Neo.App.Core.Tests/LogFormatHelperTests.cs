using FluentAssertions;
using Neo.App;
using Xunit;

namespace Neo.App.Core.Tests;

public class LogFormatHelperTests
{
    // ── StructuredResponseToText ────────────────────────────────────

    [Fact]
    public void StructuredResponseToText_ResponseWithCode_WrapsInCsharpFence()
    {
        var response = new StructuredResponse { Code = "Console.WriteLine();" };

        var result = LogFormatHelper.StructuredResponseToText(response);

        result.Should().Contain("```csharp");
        result.Should().Contain("Console.WriteLine();");
        result.Should().Contain("```");
    }

    [Fact]
    public void StructuredResponseToText_ResponseWithPatch_WrapsInDiffFence()
    {
        var response = new StructuredResponse { Patch = "-old\n+new" };

        var result = LogFormatHelper.StructuredResponseToText(response);

        result.Should().Contain("```diff");
        result.Should().Contain("-old");
    }

    [Fact]
    public void StructuredResponseToText_BothPatchAndCode_PatchWins()
    {
        var response = new StructuredResponse
        {
            Code = "code here",
            Patch = "-old\n+new"
        };

        var result = LogFormatHelper.StructuredResponseToText(response);

        result.Should().Contain("```diff");
        result.Should().NotContain("```csharp");
    }

    [Fact]
    public void StructuredResponseToText_WithNuGetPackages_Appended()
    {
        var response = new StructuredResponse
        {
            Code = "x",
            NuGetPackages = new List<string> { "Newtonsoft.Json", "Dapper" }
        };

        var result = LogFormatHelper.StructuredResponseToText(response);

        result.Should().Contain("nuget packages");
        result.Should().Contain("Newtonsoft.Json");
        result.Should().Contain("Dapper");
    }

    [Fact]
    public void StructuredResponseToText_WithExplanation_Appended()
    {
        var response = new StructuredResponse
        {
            Code = "x",
            Explanation = "This is the explanation."
        };

        var result = LogFormatHelper.StructuredResponseToText(response);

        result.Should().Contain("Explanation:");
        result.Should().Contain("This is the explanation.");
    }

    [Fact]
    public void StructuredResponseToText_NullResponse_ReturnsEmptyString()
    {
        var result = LogFormatHelper.StructuredResponseToText(null!);

        result.Should().BeEmpty();
    }

    [Fact]
    public void StructuredResponseToText_AllFieldsDefaultEmpty_NoCodeOrPatchSection()
    {
        // StructuredResponse defaults all string fields to string.Empty (not null),
        // and NuGetPackages to an empty list. The method only adds code/patch sections
        // when they are non-whitespace, but NuGetPackages (empty list) and Explanation
        // (empty string) still get appended because the null-checks pass.
        var response = new StructuredResponse();

        var result = LogFormatHelper.StructuredResponseToText(response);

        // No code or patch section should be present.
        result.Should().NotContain("```csharp");
        result.Should().NotContain("```diff");
    }
}

public class IndentHelperTests
{
    // ── NormalizeIndentation ─────────────────────────────────────────

    [Fact]
    public void NormalizeIndentation_FourSpaceIndent_Removed()
    {
        var code = "    line1\n    line2\n    line3";

        var result = IndentHelper.NormalizeIndentation(code);

        result.Should().StartWith("line1");
        result.Should().NotStartWith(" ");
    }

    [Fact]
    public void NormalizeIndentation_MixedIndentLevels_MinimumRemovedRelativePreserved()
    {
        var code = "    base\n        nested\n    other";

        var result = IndentHelper.NormalizeIndentation(code);

        // The minimum indent of 4 is removed, so "nested" still has 4 spaces relative.
        var lines = result.Split(Environment.NewLine);
        lines[0].Should().Be("base");
        lines[1].Should().StartWith("    ");
        lines[2].Should().Be("other");
    }

    [Fact]
    public void NormalizeIndentation_EmptyLinesPreserved()
    {
        var code = "    line1\n\n    line2";

        var result = IndentHelper.NormalizeIndentation(code);

        var lines = result.Split(Environment.NewLine);
        lines.Should().HaveCount(3);
        lines[1].Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NormalizeIndentation_NullOrEmpty_ReturnedAsIs(string? input)
    {
        var result = IndentHelper.NormalizeIndentation(input!);

        result.Should().Be(input);
    }

    [Fact]
    public void NormalizeIndentation_NoIndent_Unchanged()
    {
        var code = "line1\nline2";

        var result = IndentHelper.NormalizeIndentation(code);

        result.Should().Contain("line1");
        result.Should().Contain("line2");
    }

    [Fact]
    public void NormalizeIndentation_TabIndentation_Handled()
    {
        var code = "\tline1\n\t\tline2\n\tline3";

        var result = IndentHelper.NormalizeIndentation(code);

        var lines = result.Split(Environment.NewLine);
        lines[0].Should().Be("line1");
        lines[1].Should().StartWith("\t");
        lines[2].Should().Be("line3");
    }
}
