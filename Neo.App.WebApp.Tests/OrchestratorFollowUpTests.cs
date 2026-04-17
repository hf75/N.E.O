using System;
using System.Reflection;
using Neo.App.WebApp.Services;

namespace Neo.App.WebApp.Tests;

/// <summary>
/// The AppOrchestrator retry loop depends on two small helpers that build the
/// follow-up prompts it sends back to the AI. They are private static; the
/// tests reach them via reflection to keep the production surface minimal.
/// </summary>
public class OrchestratorFollowUpTests
{
    private static string InvokeCompileFollowUp(string[] diagnostics)
    {
        var m = typeof(AppOrchestrator).GetMethod("BuildCompileErrorFollowUp",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        return (string)m!.Invoke(null, new object[] { diagnostics })!;
    }

    private static string InvokeLoadFollowUp(Exception ex)
    {
        var m = typeof(AppOrchestrator).GetMethod("BuildLoadErrorFollowUp",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        return (string)m!.Invoke(null, new object[] { ex })!;
    }

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
        Assert.Contains("`code` field", prompt);
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
