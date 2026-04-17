using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Neo.App.WebApp.Services.Compilation;

/// <summary>
/// Scans a syntax tree for APIs forbidden in the WASM sandbox or considered
/// risky for a local dev tool. Returns a list of diagnostic messages.
/// Empty list = pass.
/// </summary>
public static class SecurityAnalyzer
{
    private static readonly HashSet<string> ForbiddenTypes = new(System.StringComparer.Ordinal)
    {
        "System.IO.File",
        "System.IO.Directory",
        "System.IO.FileInfo",
        "System.IO.DirectoryInfo",
        "System.Diagnostics.Process",
        "System.Net.Sockets.Socket",
        "System.Reflection.Emit.AssemblyBuilder",
        "System.Reflection.Emit.TypeBuilder",
        "System.Runtime.InteropServices.Marshal",
    };

    private static readonly HashSet<string> ForbiddenNamespacePrefixes = new(System.StringComparer.Ordinal)
    {
        "System.Windows",          // WPF
        "Microsoft.Win32",          // Win32 registry / dialogs
    };

    public record Finding(string Rule, string Message, Location? Location);

    private static void FlagIfForbidden(string expr, Location loc, List<Finding> findings)
    {
        if (ForbiddenTypes.Contains(expr))
        {
            findings.Add(new Finding("ForbiddenType",
                $"'{expr}' is not allowed in generated code.", loc));
            return;
        }
        foreach (var ns in ForbiddenNamespacePrefixes)
        {
            if (expr.StartsWith(ns + ".", System.StringComparison.Ordinal) || expr == ns)
            {
                findings.Add(new Finding("ForbiddenNamespace",
                    $"Namespace '{ns}' is not available in the WASM sandbox.", loc));
                return;
            }
        }
    }

    public static IReadOnlyList<Finding> Analyze(string source)
    {
        var findings = new List<Finding>();
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();

        // 1. Forbid unsafe blocks.
        foreach (var u in root.DescendantNodes())
        {
            if (u is UnsafeStatementSyntax)
                findings.Add(new Finding("Unsafe",
                    "unsafe blocks are not allowed in the WASM sandbox.",
                    u.GetLocation()));
        }

        // 2. Forbid DllImport / P/Invoke.
        foreach (var attr in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            var name = attr.Name.ToString();
            if (name.EndsWith("DllImport") || name.EndsWith("LibraryImport"))
                findings.Add(new Finding("PInvoke",
                    "P/Invoke (DllImport/LibraryImport) is not allowed.",
                    attr.GetLocation()));
        }

        // 3. Forbid specific type references by name — lightweight syntactic
        // check (no semantic model). Catches both `using Foo = System.IO.File`
        // shaped references (QualifiedNameSyntax) and `System.IO.File.ReadAll…`
        // call sites (chains of MemberAccessExpressions).
        foreach (var q in root.DescendantNodes().OfType<QualifiedNameSyntax>())
        {
            FlagIfForbidden(q.ToString(), q.GetLocation(), findings);
        }

        foreach (var ma in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            // Only fire once per top-level chain (skip those whose parent is also a MemberAccess).
            if (ma.Parent is MemberAccessExpressionSyntax) continue;
            // Drop the last segment (method/field name) — we match on the type part.
            var chain = ma.Expression?.ToString();
            if (string.IsNullOrEmpty(chain)) continue;
            FlagIfForbidden(chain, ma.GetLocation(), findings);

            // Also catch a full chain reference like `System.Windows.Forms.MessageBox.Show(...)`.
            var full = ma.ToString();
            foreach (var ns in ForbiddenNamespacePrefixes)
            {
                if (full.StartsWith(ns + ".", System.StringComparison.Ordinal))
                    findings.Add(new Finding("ForbiddenNamespace",
                        $"Namespace '{ns}' is not available in the WASM sandbox.",
                        ma.GetLocation()));
            }
        }

        // 4. Forbid using directives of forbidden namespaces.
        foreach (var u in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (u.Name is null) continue;
            var n = u.Name.ToString();
            foreach (var ns in ForbiddenNamespacePrefixes)
            {
                if (n == ns || n.StartsWith(ns + ".", System.StringComparison.Ordinal))
                    findings.Add(new Finding("ForbiddenNamespace",
                        $"Namespace '{ns}' is not available in the WASM sandbox.",
                        u.GetLocation()));
            }
        }

        return findings;
    }
}
