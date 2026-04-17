using System;

namespace Neo.App.WebApp.Tests;

/// <summary>
/// The retry loop in AppOrchestrator delegates to CodeIterationHelpers in
/// Neo.AssemblyForge.Core. The same helpers back the desktop/MCP
/// AssemblyForgeSession, so a regression here affects both surfaces.
/// </summary>
public class OrchestratorFollowUpTests
{
    private static string InvokeCompileFollowUp(string[] diagnostics)
        => Neo.AssemblyForge.CodeIterationHelpers.BuildCompileErrorFollowUp(diagnostics);

    private static string InvokeLoadFollowUp(Exception ex)
        => Neo.AssemblyForge.CodeIterationHelpers.BuildLoadErrorFollowUp(ex);

    [Fact]
    public void CompileFollowUp_IncludesAllDiagnostics()
    {
        var diags = new[]
        {
            "(5,17): error CS0246: The type or namespace name 'Foo' could not be found",
            "(9,33): error CS0103: The name 'Bar' does not exist in the current context",
        };
        var prompt = InvokeCompileFollowUp(diags);

        Assert.Contains("Foo", prompt);
        Assert.Contains("Bar", prompt);
        // The Core helper uses PascalCase (Code), matching the Forge wire format.
        Assert.Contains("`Code` field", prompt);
        Assert.Contains("FULL updated", prompt);
    }

    [Fact]
    public void CompileFollowUp_TruncatesAfter10()
    {
        var diags = new string[15];
        for (int i = 0; i < 15; i++) diags[i] = $"error-line-{i}";
        var prompt = InvokeCompileFollowUp(diags);

        // First 10 are included, later ones are dropped.
        for (int i = 0; i < 10; i++) Assert.Contains($"error-line-{i}", prompt);
        Assert.DoesNotContain("error-line-14", prompt);
    }

    [Fact]
    public void LoadFollowUp_MentionsNuGetAndWasmSandbox()
    {
        var ex = new InvalidOperationException("Could not load file or assembly 'SomePackage'");
        var prompt = InvokeLoadFollowUp(ex);

        Assert.Contains("runtime", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WASM sandbox", prompt);
        Assert.Contains("SomePackage", prompt);
        Assert.Contains("nuget", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadFollowUp_IncludesExceptionType()
    {
        var ex = new System.Reflection.TargetInvocationException(
            new InvalidCastException("can't cast"));
        // The orchestrator passes the UNWRAPPED inner, but the helper only
        // sees whatever it's handed — verify the type name round-trips.
        var prompt = InvokeLoadFollowUp(ex);
        Assert.Contains("TargetInvocationException", prompt);
    }
}
