using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Neo.ErrorSurfacing;

/// <summary>
/// Makes the exceptions of a compiled app surface that would otherwise vanish without a trace,
/// so N.E.O.'s repair (and any error handling of the app) sees them like every other exception:
/// <list type="bullet">
///   <item>a task the code drops — <c>LoadAsync();</c>, <c>Task.Run(…);</c>,
///         <c>Dispatcher.UIThread.InvokeAsync(…);</c>, <c>_ = …;</c>, the body of a void lambda
///         such as <c>(s, e) => LoadAsync()</c> — fails, and nothing ever looks at it;</item>
///   <item>a handler that an API runs without letting its exceptions out: Avalonia's
///         <c>DispatcherTimer</c> runs each tick through <c>Dispatcher.InvokeAsync</c>, which keeps
///         the exception in a task nobody awaits, and the timer stops; <c>System.Timers.Timer</c>
///         catches it. WPF's <c>DispatcherTimer</c> lets it out, but stops all the same.</item>
/// </list>
/// The trees are rewritten to hand such a task or handler to a small runtime class that is added
/// to the app (<see cref="RuntimeSource"/>). It posts the exception to the UI thread and throws it
/// there, as the runtime does with the exception of an async void method; a timer keeps ticking.
///
/// <para>Rewriting is deterministic and keeps every line where it was: compile errors and stack
/// traces point at the code as written, and hot reload rewrites each generation the same way
/// (Neo.HotReload compares and patches the rewritten code).</para>
/// </summary>
public static class LostExceptions
{
    /// <summary>The file name of the runtime class in a compilation.</summary>
    public const string RuntimeFileName = "NeoRuntime.LostExceptions.cs";

    /// <summary>The runtime class every instrumented app is compiled with.</summary>
    public static string RuntimeSource { get; } = EmbeddedSource.Read(RuntimeFileName);

    /// <summary>
    /// The compilation with every tree of the app rewritten where needed and the runtime class
    /// added (unless it is there already). Rewritten trees keep their path, options, encoding and
    /// checksum algorithm.
    /// </summary>
    /// <param name="runtimePath">Path of the runtime tree; hot reload needs a fixed one, the others none.</param>
    public static CSharpCompilation Instrument(CSharpCompilation compilation, string? runtimePath = null)
    {
        var result = compilation;
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (IsRuntime(tree)) continue;
            if (Rewrite(compilation.GetSemanticModel(tree)) is { } text)
            {
                var source = tree.GetText();
                result = result.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(
                    SourceText.From(text, source.Encoding, source.ChecksumAlgorithm), (CSharpParseOptions)tree.Options, tree.FilePath));
            }
        }

        if (!compilation.SyntaxTrees.Any(IsRuntime))
        {
            var first = compilation.SyntaxTrees.FirstOrDefault();
            var firstText = first?.GetText();
            result = result.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                SourceText.From(RuntimeSource, firstText?.Encoding ?? new UTF8Encoding(false), firstText?.ChecksumAlgorithm ?? SourceHashAlgorithm.Sha256),
                (CSharpParseOptions?)first?.Options ?? CSharpParseOptions.Default,
                runtimePath ?? RuntimeFileName));
        }
        return result;
    }

    /// <summary>
    /// The text of the tree <paramref name="model"/> belongs to, rewritten; null when nothing in it
    /// needs rewriting. For callers that keep their sources as documents (hot reload).
    /// </summary>
    public static string? Rewrite(SemanticModel model)
    {
        var rewriter = new SurfacingRewriter(model);
        var root = rewriter.Visit(model.SyntaxTree.GetRoot());
        return rewriter.Changed ? root.ToFullString() : null;
    }

    /// <summary>Whether <paramref name="tree"/> is the runtime class (the rewriter leaves it alone).</summary>
    public static bool IsRuntime(SyntaxTree tree) => Path.GetFileName(tree.FilePath) == RuntimeFileName;

    /// <summary>
    /// Whether <paramref name="invocation"/> is the guard the rewriter puts where a handler stood
    /// (<c>timer.Tick += Guard&lt;T&gt;(handler)</c>). The guard keeps the handler and hands back a
    /// wrapper that runs it when the event fires, never right away; code that asks when a handler runs
    /// (hot reload) reads a guarded subscription as the subscription it was written as.
    /// </summary>
    public static bool IsGuard(InvocationExpressionSyntax invocation) => SurfacingRewriter.IsGuard(invocation);
}
