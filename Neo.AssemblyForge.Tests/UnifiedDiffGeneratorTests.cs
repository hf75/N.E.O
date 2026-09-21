using FluentAssertions;
using Neo.AssemblyForge;
using Xunit;

namespace Neo.AssemblyForge.Tests;

public class UnifiedDiffGeneratorTests
{
    // The desktop host and the MCP tools always diff the single virtual file './currentcode.cs'.
    private const string CurrentCodeFile = "./currentcode.cs";

    private static string CreatePatchForCurrentCode(string oldText, string newText, int contextLines = 3)
        => UnifiedDiffGenerator.CreatePatch(CurrentCodeFile, oldText, newText, contextLines);

    private static UnifiedDiffPatcher.ApplyResult ApplyToCurrentCode(string originalText, string patchText)
        => UnifiedDiffPatcher.TryApply(originalText, patchText, CurrentCodeFile);

    // ── No changes → "No changes." ──────────────────────────────────

    [Fact]
    public void CreatePatchForCurrentCode_NoChanges_ReturnsNoChanges()
    {
        var text = "line1\nline2\nline3";

        var result = CreatePatchForCurrentCode(text, text);

        result.Should().Be("No changes.");
    }

    // ── Single line added ───────────────────────────────────────────

    [Fact]
    public void CreatePatchForCurrentCode_SingleLineAdded_ContainsPlusLine()
    {
        var oldText = "line1\nline2";
        var newText = "line1\nline2\nline3";

        var result = CreatePatchForCurrentCode(oldText, newText);

        result.Should().Contain("+line3");
        result.Should().Contain("@@");
    }

    // ── Single line removed ─────────────────────────────────────────

    [Fact]
    public void CreatePatchForCurrentCode_SingleLineRemoved_ContainsMinusLine()
    {
        var oldText = "line1\nline2\nline3";
        var newText = "line1\nline3";

        var result = CreatePatchForCurrentCode(oldText, newText);

        result.Should().Contain("-line2");
    }

    // ── Single line modified ────────────────────────────────────────

    [Fact]
    public void CreatePatchForCurrentCode_SingleLineModified_ContainsBothDiffLines()
    {
        var oldText = "line1\noldline\nline3";
        var newText = "line1\nnewline\nline3";

        var result = CreatePatchForCurrentCode(oldText, newText);

        result.Should().Contain("-oldline");
        result.Should().Contain("+newline");
    }

    // ── Multiple hunks (changes far apart) ──────────────────────────

    [Fact]
    public void CreatePatchForCurrentCode_ChangesFarApart_ProducesMultipleHunks()
    {
        var lines = new List<string>();
        for (int i = 1; i <= 30; i++) lines.Add($"line{i}");
        var oldText = string.Join("\n", lines);

        lines[1] = "changed2";   // near the top
        lines[28] = "changed29"; // near the bottom
        var newText = string.Join("\n", lines);

        var result = CreatePatchForCurrentCode(oldText, newText, contextLines: 2);

        // Two separate @@ hunks expected because they are more than 2*contextLines apart.
        var hunkCount = result.Split("@@").Length - 1; // each hunk has an @@ pair
        // Minimum: at least 2 hunks (each has @@ ... @@ pattern = 4 @@ markers for 2 hunks)
        hunkCount.Should().BeGreaterThanOrEqualTo(4);
    }

    // ── Empty old + non-empty new (full addition) ───────────────────

    [Fact]
    public void CreatePatchForCurrentCode_EmptyOldNonEmptyNew_GeneratesFullAddition()
    {
        var result = CreatePatchForCurrentCode("", "line1\nline2");

        result.Should().Contain("+line1");
        result.Should().Contain("+line2");
    }

    // ── Non-empty old + empty new (full deletion) ───────────────────

    [Fact]
    public void CreatePatchForCurrentCode_NonEmptyOldEmptyNew_GeneratesFullDeletion()
    {
        var result = CreatePatchForCurrentCode("line1\nline2", "");

        result.Should().Contain("-line1");
        result.Should().Contain("-line2");
    }

    // ── Context lines parameter affects hunk size ───────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void CreatePatchForCurrentCode_ContextLinesParam_AffectsOutput(int contextLines)
    {
        var oldText = "a\nb\nc\nd\ne";
        var newText = "a\nb\nX\nd\ne";

        var result = CreatePatchForCurrentCode(oldText, newText, contextLines);

        result.Should().Contain("-c");
        result.Should().Contain("+X");
        // With 0 context lines, context lines " b" and " d" should not appear in the hunk body.
        if (contextLines == 0)
        {
            // Check specifically for context-line markers (space prefix followed by the line content).
            // Exclude the header "a/... b/..." which also contains " b".
            var hunkStart = result.IndexOf("@@");
            var hunkBody = result.Substring(result.IndexOf("@@", hunkStart + 2) + 2);
            hunkBody.Should().NotContain("\n b\n");
            hunkBody.Should().NotContain("\n d\n");
        }
    }

    // ── Null inputs treated as empty ────────────────────────────────

    [Fact]
    public void CreatePatchForCurrentCode_NullOldText_TreatedAsEmpty()
    {
        var result = CreatePatchForCurrentCode(null!, "hello");

        result.Should().Contain("+hello");
    }

    [Fact]
    public void CreatePatchForCurrentCode_NullNewText_TreatedAsEmpty()
    {
        var result = CreatePatchForCurrentCode("hello", null!);

        result.Should().Contain("-hello");
    }

    [Fact]
    public void CreatePatchForCurrentCode_BothNull_NoChanges()
    {
        var result = CreatePatchForCurrentCode(null!, null!);

        result.Should().Be("No changes.");
    }

    // ── Roundtrip: generate then apply ──────────────────────────────

    [Fact]
    public void CreatePatchForCurrentCode_Roundtrip_GeneratedPatchCanBeApplied()
    {
        var oldText = "using System;\nclass Foo\n{\n    void Bar() { }\n}";
        var newText = "using System;\nclass Foo\n{\n    void Bar() { Console.WriteLine(); }\n    void Baz() { }\n}";

        var patch = CreatePatchForCurrentCode(oldText, newText);

        patch.Should().NotBe("No changes.");

        var applyResult = ApplyToCurrentCode(oldText, patch);

        applyResult.Success.Should().BeTrue();
        applyResult.PatchedText.Should().Be(newText);
    }

    // ── Patch header contains currentcode.cs ────────────────────────

    [Fact]
    public void CreatePatchForCurrentCode_OutputContainsCurrentCodePath()
    {
        var result = CreatePatchForCurrentCode("a", "b");

        result.Should().Contain("currentcode.cs");
    }
}
