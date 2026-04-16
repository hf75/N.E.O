using FluentAssertions;
using Neo.App;
using Xunit;

namespace Neo.App.Core.Tests;

public class UnifiedDiffPatcherTests
{
    #region Helpers

    private static string MakePatch(string hunkBody, string file = "./currentcode.cs")
    {
        return $"""
            diff --git a/{file} b/{file}
            --- a/{file}
            +++ b/{file}
            {hunkBody}
            """;
    }

    #endregion

    // ── Simple single-hunk: add a line ──────────────────────────────

    [Fact]
    public void TryApplyToCurrentCode_AddLine_InsertsLine()
    {
        var original = "line1\nline2\nline3";
        var patch = MakePatch(
            "@@ -1,3 +1,4 @@\n line1\n line2\n+line2.5\n line3");

        var result = UnifiedDiffPatcher.TryApplyToCurrentCode(original, patch);

        result.Success.Should().BeTrue();
        result.PatchedText.Should().Contain("line2.5");
    }

    // ── Simple single-hunk: remove a line ───────────────────────────

    [Fact]
    public void TryApplyToCurrentCode_RemoveLine_RemovesLine()
    {
        var original = "line1\nline2\nline3";
        var patch = MakePatch(
            "@@ -1,3 +1,2 @@\n line1\n-line2\n line3");

        var result = UnifiedDiffPatcher.TryApplyToCurrentCode(original, patch);

        result.Success.Should().BeTrue();
        result.PatchedText.Should().NotContain("line2");
        result.PatchedText.Should().Contain("line1");
        result.PatchedText.Should().Contain("line3");
    }

    // ── Simple single-hunk: modify a line ───────────────────────────

    [Fact]
    public void TryApplyToCurrentCode_ModifyLine_ReplacesLine()
    {
        var original = "line1\nline2\nline3";
        var patch = MakePatch(
            "@@ -1,3 +1,3 @@\n line1\n-line2\n+lineModified\n line3");

        var result = UnifiedDiffPatcher.TryApplyToCurrentCode(original, patch);

        result.Success.Should().BeTrue();
        result.PatchedText.Should().Contain("lineModified");
        result.PatchedText.Should().NotContain("line2");
    }

    // ── Multi-hunk patch ────────────────────────────────────────────

    [Fact]
    public void TryApplyToCurrentCode_MultiHunkPatch_AppliesBothHunks()
    {
        var lines = new List<string>();
        for (int i = 1; i <= 20; i++) lines.Add($"line{i}");
        var original = string.Join("\n", lines);

        var patch = MakePatch(
            "@@ -1,3 +1,3 @@\n line1\n-line2\n+lineA\n line3\n" +
            "@@ -18,3 +18,3 @@\n line18\n-line19\n+lineB\n line20");

        var result = UnifiedDiffPatcher.TryApplyToCurrentCode(original, patch);

        result.Success.Should().BeTrue();
        result.PatchedText.Should().Contain("lineA");
        result.PatchedText.Should().Contain("lineB");
    }

    // ── Fuzzy matching (context lines shifted) ──────────────────────

    [Fact]
    public void TryApplyToCurrentCode_FuzzyMatch_AppliesWhenContextShifted()
    {
        // Original has an extra line at the top compared to what the hunk expects.
        var original = "extra\nline1\nline2\nline3";
        // Hunk says line1 is at position 1, but it is actually at position 2.
        var patch = MakePatch(
            "@@ -1,3 +1,3 @@\n line1\n-line2\n+lineChanged\n line3");

        var result = UnifiedDiffPatcher.TryApplyToCurrentCode(original, patch);

        result.Success.Should().BeTrue();
        result.PatchedText.Should().Contain("lineChanged");
    }

    // ── Empty patch ─────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryApplyToCurrentCode_EmptyOrWhitespacePatch_ReturnsError(string? patch)
    {
        var result = UnifiedDiffPatcher.TryApplyToCurrentCode("some code", patch!);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("empty");
    }

    // ── Null original text treated as empty ─────────────────────────

    [Fact]
    public void TryApplyToCurrentCode_NullOriginal_TreatedAsEmpty()
    {
        // A patch that adds a line to an empty file.
        var patch = MakePatch(
            "@@ -1,0 +1,1 @@\n+new line");

        var result = UnifiedDiffPatcher.TryApplyToCurrentCode(null!, patch);

        // The patcher should not throw; it should process with empty original.
        result.Should().NotBeNull();
    }

    // ── Patch with code fences stripped ──────────────────────────────

    [Fact]
    public void TryApplyToCurrentCode_PatchWithCodeFences_StripsAndApplies()
    {
        var original = "line1\nline2\nline3";
        var rawPatch = MakePatch(
            "@@ -1,3 +1,3 @@\n line1\n-line2\n+lineFixed\n line3");
        var fencedPatch = "```diff\n" + rawPatch + "\n```";

        var result = UnifiedDiffPatcher.TryApplyToCurrentCode(original, fencedPatch);

        result.Success.Should().BeTrue();
        result.PatchedText.Should().Contain("lineFixed");
    }

    // ── Patch with wrong file path → no hunks found ─────────────────

    [Fact]
    public void TryApplyToCurrentCode_WrongFilePath_ReturnsError()
    {
        var original = "line1\nline2";
        var patch = MakePatch(
            "@@ -1,2 +1,2 @@\n line1\n-line2\n+changed",
            file: "other_file.cs");

        var result = UnifiedDiffPatcher.TryApplyToCurrentCode(original, patch);

        result.Success.Should().BeFalse();
    }

    // ── Patch targeting currentcode.cs → hunks applied ──────────────

    [Fact]
    public void TryApplyToCurrentCode_TargetsCurrentCode_HunksApplied()
    {
        var original = "alpha\nbeta\ngamma";
        var patch = MakePatch(
            "@@ -1,3 +1,3 @@\n alpha\n-beta\n+BETA\n gamma",
            file: "./currentcode.cs");

        var result = UnifiedDiffPatcher.TryApplyToCurrentCode(original, patch);

        result.Success.Should().BeTrue();
        result.PatchedText.Should().Contain("BETA");
    }

    // ── Full code replacement fallback (standalone C# with DynamicUserControl) ──

    [Fact]
    public void TryApplyToCurrentCode_FullCodeReplacementFallback_ReturnsPatchAsCode()
    {
        var original = "old code";
        var replacement = "using System;\npublic class DynamicUserControl { }";

        var result = UnifiedDiffPatcher.TryApplyToCurrentCode(original, replacement);

        result.Success.Should().BeTrue();
        result.PatchedText.Should().Contain("DynamicUserControl");
    }

    // ── Add File: Codex-style patches ───────────────────────────────

    [Fact]
    public void TryApplyToCurrentCode_CodexAddFile_ExtractsContent()
    {
        var original = "old code";
        var patch = "*** Add File: ./currentcode.cs\n+using System;\n+class DynamicUserControl { }";

        var result = UnifiedDiffPatcher.TryApplyToCurrentCode(original, patch);

        result.Success.Should().BeTrue();
        result.PatchedText.Should().Contain("using System;");
        result.PatchedText.Should().Contain("class DynamicUserControl");
    }

    // ── Whitespace-only trailing difference → still matches (TrimEnd) ──

    [Fact]
    public void TryApplyToCurrentCode_TrailingWhitespaceDifference_StillMatches()
    {
        var original = "line1  \nline2\nline3";
        // Patch context has no trailing spaces on "line1".
        var patch = MakePatch(
            "@@ -1,3 +1,3 @@\n line1\n-line2\n+lineNew\n line3");

        var result = UnifiedDiffPatcher.TryApplyToCurrentCode(original, patch);

        result.Success.Should().BeTrue();
        result.PatchedText.Should().Contain("lineNew");
    }

    // ── CRLF vs LF handling ─────────────────────────────────────────

    [Fact]
    public void TryApplyToCurrentCode_CrLfOriginal_PreservesLineEndings()
    {
        var original = "line1\r\nline2\r\nline3";
        var patch = MakePatch(
            "@@ -1,3 +1,3 @@\n line1\n-line2\n+lineChanged\n line3");

        var result = UnifiedDiffPatcher.TryApplyToCurrentCode(original, patch);

        result.Success.Should().BeTrue();
        result.PatchedText.Should().Contain("\r\n");
        result.PatchedText.Should().Contain("lineChanged");
    }

    // ── Hunk application failure → returns Success=false ────────────

    [Fact]
    public void TryApplyToCurrentCode_ContextMismatch_ReturnsFailure()
    {
        var original = "aaa\nbbb\nccc";
        // Hunk expects "xxx" which does not exist in the original.
        var patch = MakePatch(
            "@@ -1,3 +1,3 @@\n xxx\n-yyy\n+zzz\n ccc");

        var result = UnifiedDiffPatcher.TryApplyToCurrentCode(original, patch);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    // ── Full code replacement does NOT trigger for actual diff text ──

    [Fact]
    public void TryApplyToCurrentCode_DiffTextWithDynamicUserControl_NotTreatedAsFullReplacement()
    {
        var original = "old code";
        // This looks like a diff, not standalone code, even though it mentions DynamicUserControl.
        var patchText = "diff --git a/other.cs b/other.cs\n--- a/other.cs\n+++ b/other.cs\n@@ -1,1 +1,1 @@\n-old\n+class DynamicUserControl {}";

        var result = UnifiedDiffPatcher.TryApplyToCurrentCode(original, patchText);

        // It has hunks but for the wrong file, so no hunks for currentcode.cs.
        result.Success.Should().BeFalse();
    }

    // ── Codex-style Add File with non-currentcode path → ignored ────

    [Fact]
    public void TryApplyToCurrentCode_CodexAddFileWrongPath_ReturnsError()
    {
        var original = "old code";
        var patch = "*** Add File: ./other.cs\n+using System;\n+class Other { }";

        var result = UnifiedDiffPatcher.TryApplyToCurrentCode(original, patch);

        result.Success.Should().BeFalse();
    }
}
