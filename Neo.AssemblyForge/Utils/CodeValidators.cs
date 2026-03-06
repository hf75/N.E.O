using System;
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
