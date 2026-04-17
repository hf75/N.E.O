using Neo.App.WebApp.Services.Ai;
using Neo.AssemblyForge;

namespace Neo.App.WebApp.Tests;

/// <summary>
/// The web orchestrator relies on two things to do patch-first iterations:
/// the AI's StructuredResponse must carry a `patch` field, and the source-linked
/// UnifiedDiffPatcher must be able to apply it against the previous code.
/// These tests exercise both pieces without needing an AI call.
/// </summary>
public class PatchWorkflowTests
{
    [Fact]
    public void StructuredResponse_ParsesPatchField()
    {
        var raw = """
            {
              "patch": "--- a/GeneratedApp.cs\n+++ b/GeneratedApp.cs\n@@ -3,1 +3,1 @@\n-Hello\n+Hi there",
              "explanation": "greeting shortened"
            }
            """;
        var r = StructuredResponseParser.Parse(raw);
        Assert.NotNull(r);
        Assert.Null(r!.Code);
        Assert.NotNull(r.Patch);
        Assert.Contains("@@", r.Patch!);
    }

    [Fact]
    public void UnifiedDiffPatcher_AppliesSimpleHunk()
    {
        var original = "line one\nline two\nline three\n";
        var patch =
            "--- a/GeneratedApp.cs\n" +
            "+++ b/GeneratedApp.cs\n" +
            "@@ -1,3 +1,3 @@\n" +
            " line one\n" +
            "-line two\n" +
            "+line TWO modified\n" +
            " line three\n";
        var result = UnifiedDiffPatcher.TryApply(original, patch, "GeneratedApp.cs", expectedClassNameForFallback: null);
        Assert.True(result.Success, $"expected success, got: {result.ErrorMessage}");
        Assert.Contains("line TWO modified", result.PatchedText);
        Assert.Contains("line one", result.PatchedText);
        Assert.Contains("line three", result.PatchedText);
    }

    [Fact]
    public void UnifiedDiffPatcher_ReportsFailure_OnMalformedPatch()
    {
        var original = "anything\n";
        var patch = "this is not a patch";
        var result = UnifiedDiffPatcher.TryApply(original, patch, "GeneratedApp.cs", expectedClassNameForFallback: null);
        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [Fact]
    public void StructuredResponse_AcceptsBothCodeAndPatch()
    {
        // When both are present the orchestrator prefers Patch; the parser
        // shouldn't drop either.
        var raw = """
            {"code": "class X{}", "patch": "--- a/GeneratedApp.cs\n+++ b/GeneratedApp.cs\n@@ -1,1 +1,1 @@\n-old\n+new"}
            """;
        var r = StructuredResponseParser.Parse(raw);
        Assert.NotNull(r);
        Assert.NotNull(r!.Code);
        Assert.NotNull(r.Patch);
    }
}
