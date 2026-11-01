using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Neo.AssemblyForge;

public static class CodeValidators
{
    public static bool ContainsNamedUserControl(string code, string className)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(className))
            return false;

        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();

        foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (!string.Equals(cls.Identifier.ValueText, className, StringComparison.Ordinal))
                continue;

            if (cls.BaseList == null)
                continue;

            foreach (var baseType in cls.BaseList.Types)
            {
                if (IsUserControlType(baseType.Type))
                    return true;
            }
        }

        return false;
    }

    public static bool ContainsEntrypoint(string code, string mainTypeName)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(mainTypeName))
            return false;

        var (expectedNamespace, expectedTypeName) = SplitNamespaceAndType(mainTypeName);

        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();

        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (!string.Equals(typeDecl.Identifier.ValueText, expectedTypeName, StringComparison.Ordinal))
                continue;

            if (!string.IsNullOrWhiteSpace(expectedNamespace))
            {
                var actualNamespace = GetContainingNamespace(typeDecl);
                if (!string.Equals(actualNamespace, expectedNamespace, StringComparison.Ordinal))
                    continue;
            }

            foreach (var method in typeDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                if (!string.Equals(method.Identifier.ValueText, "Main", StringComparison.Ordinal))
                    continue;

                if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                    continue;

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The methods <paramref name="after"/> adds to <paramref name="before"/> that nothing refers to in
    /// <paramref name="allSources"/>: code the app never runs, so a change made only of it changes nothing.
    /// Counts only what nothing else can call: not what is public, protected or internal, and not what has
    /// an attribute (the framework, a generator or another app may call those), nor the entry point.
    /// </summary>
    public static IReadOnlyList<string> NewMethodsNothingCalls(string before, string after, IEnumerable<string> allSources)
    {
        var existing = MethodsIn(before).Select(m => m.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        var candidates = MethodsIn(after)
            .Where(m => OnlyThisCodeCanCall(m) && !existing.Contains(m.Identifier.ValueText))
            .Select(m => m.Identifier.ValueText)
            .Distinct()
            .ToList();
        if (candidates.Count == 0)
            return candidates;

        var referenced = allSources
            .SelectMany(source => CSharpSyntaxTree.ParseText(source).GetRoot().DescendantTokens())
            .Where(t => t.IsKind(SyntaxKind.IdentifierToken) && candidates.Contains(t.ValueText))
            // The declaration itself is no reference, and neither is a method calling itself.
            .Where(t => !t.Parent!.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText == t.ValueText))
            .Select(t => t.ValueText)
            .ToHashSet(StringComparer.Ordinal);

        return candidates.Where(c => !referenced.Contains(c)).ToList();
    }

    private static IEnumerable<MethodDeclarationSyntax> MethodsIn(string code)
        => CSharpSyntaxTree.ParseText(code).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>();

    private static bool OnlyThisCodeCanCall(MethodDeclarationSyntax method)
        => method.AttributeLists.Count == 0
           && method.ExplicitInterfaceSpecifier == null
           && method.Parent is not InterfaceDeclarationSyntax
           && method.Identifier.ValueText != "Main"
           && !method.Modifiers.Any(m => m.Kind() is SyntaxKind.PublicKeyword or SyntaxKind.ProtectedKeyword or SyntaxKind.InternalKeyword or SyntaxKind.PartialKeyword);

    private static bool IsUserControlType(TypeSyntax typeSyntax)
    {
        var rightMost = GetRightMostIdentifier(typeSyntax);
        return string.Equals(rightMost, "UserControl", StringComparison.Ordinal);
    }

    private static (string Namespace, string TypeName) SplitNamespaceAndType(string mainTypeName)
    {
        var trimmed = mainTypeName.Trim();
        var lastDot = trimmed.LastIndexOf('.');
        if (lastDot <= 0 || lastDot >= trimmed.Length - 1)
            return (string.Empty, trimmed);

        return (trimmed.Substring(0, lastDot), trimmed.Substring(lastDot + 1));
    }

    private static string GetContainingNamespace(SyntaxNode node)
    {
        var parts = node.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Select(ns => ns.Name.ToString())
            .Reverse()
            .ToList();

        return parts.Count == 0 ? string.Empty : string.Join(".", parts);
    }

    private static string? GetRightMostIdentifier(TypeSyntax typeSyntax)
    {
        return typeSyntax switch
        {
            IdentifierNameSyntax ins => ins.Identifier.ValueText,
            GenericNameSyntax gns => gns.Identifier.ValueText,
            QualifiedNameSyntax qns => GetRightMostIdentifier(qns.Right),
            AliasQualifiedNameSyntax aqns => GetRightMostIdentifier(aqns.Name),
            _ => null
        };
    }
}
