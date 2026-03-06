using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Neo.App
{
    public static class DesignerIdInjector
    {
        private const string WpfBaseClass = "System.Windows.FrameworkElement";
        private const string AvaloniaBaseClass = "Avalonia.Controls.Control";
        private const string ExtensionMethodName = "RegisterDesignId";

        private static List<MetadataReference>? _cachedWpfRefs;
        private static List<MetadataReference>? _cachedAvaloniaRefs;
        private static readonly object _lockObj = new();

        public static bool TryInjectDesignIds(
            string code,
            bool useAvalonia,
            out string updatedCode,
            out int injectedCount,
            out string? error)
        {
            updatedCode = code ?? string.Empty;
            injectedCount = 0;
            error = null;

            if (string.IsNullOrWhiteSpace(updatedCode)) return true;

            updatedCode = LoopUnroller.UnrollLoops(updatedCode);

            try
            {
                var tree = CSharpSyntaxTree.ParseText(updatedCode);
                var root = tree.GetRoot();

                var references = GetMetadataReferences(useAvalonia);

                var compilation = CSharpCompilation.Create("DynamicAnalysis_" + Guid.NewGuid())
                    .AddReferences(references)
                    .AddSyntaxTrees(tree);

                var semanticModel = compilation.GetSemanticModel(tree);
                int nextId = FindMaxExistingId(root) + 1;

                var targetBaseType = useAvalonia ? AvaloniaBaseClass : WpfBaseClass;
                var rewriter = new ExtensionMethodRewriter(semanticModel, targetBaseType, nextId);

                var newRoot = rewriter.Visit(root);

                if (rewriter.InjectedCount > 0)
                {
                    updatedCode = newRoot.ToFullString();
                    injectedCount = rewriter.InjectedCount;
                }

                // Append Helper Classes mit der KORRIGIERTEN Logik f�r Overwrites
                updatedCode = AppendHelperClassesIfNeeded(updatedCode);

                return true;
            }
            catch (Exception ex)
            {
                error = $"Injection failed: {ex.Message}";
                return false;
            }
        }

        private static string AppendHelperClassesIfNeeded(string code)
        {
            var sb = new StringBuilder(code);

            bool hasExtensions = code.Contains("class DesignerExtensions") && code.Contains("RegisterDesignId");
            bool hasIds = code.Contains("class DesignerIds") && code.Contains("NamePrefix");

            if (!hasExtensions || !hasIds)
            {
                sb.AppendLine();
                sb.AppendLine("// --- Auto-Injected Helpers for Dynamic Compilation ---");
                sb.AppendLine("namespace Neo.App {");
            }

            if (!hasIds)
            {
                sb.AppendLine(@"
    public static class DesignerIds
    {
        public const string NamePrefix = ""__neo_"";
        public const string TagPrefix = ""__neo:id="";

        public static bool IsDesignIdValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value.StartsWith(NamePrefix, StringComparison.Ordinal) ||
                   value.StartsWith(TagPrefix, StringComparison.Ordinal);
        }

        public static bool TryParseDesignNumber(string value, out int number)
        {
            number = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string? suffix = null;
            if (value.StartsWith(NamePrefix, StringComparison.Ordinal))
                suffix = value.Substring(NamePrefix.Length);
            else if (value.StartsWith(TagPrefix, StringComparison.Ordinal))
                suffix = value.Substring(TagPrefix.Length);

            if (string.IsNullOrWhiteSpace(suffix)) return false;
            return int.TryParse(suffix, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number);
        }

        public static string CreateNameId(int number) => $""{NamePrefix}{number:D4}"";
        public static string CreateTagId(int number) => $""{TagPrefix}{number:D4}"";
    }");
            }

            if (!hasExtensions)
            {
                // HIER IST DER FIX DRIN:
                sb.AppendLine(@"
    public static class DesignerExtensions
    {
        public static T RegisterDesignId<T>(this T control, string id) where T : class
        {
            if (control == null) return null!;
            if (string.IsNullOrEmpty(id)) return control;
            try
            {
                dynamic d = control;
                string current = d.Name;
                // Fix: �berschreibe Name auch dann, wenn es eine generierte ID ist.
                // Damit gewinnt die ID an der Aufrufstelle (Call-Site) gegen�ber der ID in der Helper-Methode.
                if (string.IsNullOrEmpty(current) || current.StartsWith(DesignerIds.NamePrefix)) 
                {
                    d.Name = id;
                }
            }
            catch 
            {
                var prop = control.GetType().GetProperty(""Name"");
                if (prop != null && prop.CanWrite)
                {
                    var current = prop.GetValue(control) as string;
                    if (string.IsNullOrEmpty(current) || (current != null && current.StartsWith(DesignerIds.NamePrefix)))
                    {
                         prop.SetValue(control, id);
                    }
                }
            }
            return control;
        }
    }");
            }

            if (!hasExtensions || !hasIds)
            {
                sb.AppendLine("}");
            }

            return sb.ToString();
        }

        private class ExtensionMethodRewriter : CSharpSyntaxRewriter
        {
            private readonly SemanticModel _semanticModel;
            private readonly string _targetBaseType;
            private int _currentId;
            public int InjectedCount { get; private set; }

            public ExtensionMethodRewriter(SemanticModel semanticModel, string targetBaseType, int startId)
            {
                _semanticModel = semanticModel;
                _targetBaseType = targetBaseType;
                _currentId = startId;
            }

            public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
            {
                return InjectExtensionMethod(node, base.VisitObjectCreationExpression(node) as ExpressionSyntax);
            }

            public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                if (node.Expression is MemberAccessExpressionSyntax mae &&
                    mae.Name.Identifier.Text == ExtensionMethodName)
                {
                    return base.VisitInvocationExpression(node);
                }
                return InjectExtensionMethod(node, base.VisitInvocationExpression(node) as ExpressionSyntax);
            }

            private SyntaxNode InjectExtensionMethod(ExpressionSyntax originalNode, ExpressionSyntax? visitedNode)
            {
                if (visitedNode == null) return originalNode;

                var typeInfo = _semanticModel.GetTypeInfo(originalNode);
                var type = typeInfo.Type;

                if (type == null || type is IErrorTypeSymbol || !IsUiControl(type))
                    return visitedNode;

                if (HasDesignId(originalNode))
                    return visitedNode;

                var newId = DesignerIds.CreateNameId(_currentId++);
                InjectedCount++;

                var access = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ParenthesizedExpression(visitedNode),
                    SyntaxFactory.IdentifierName(ExtensionMethodName));

                var invocation = SyntaxFactory.InvocationExpression(access)
                    .WithArgumentList(
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(
                                    SyntaxFactory.LiteralExpression(
                                        SyntaxKind.StringLiteralExpression,
                                        SyntaxFactory.Literal(newId))))));

                return invocation
                    .WithLeadingTrivia(visitedNode.GetLeadingTrivia())
                    .WithTrailingTrivia(visitedNode.GetTrailingTrivia());
            }

            private bool HasDesignId(ExpressionSyntax node)
            {
                if (node is ObjectCreationExpressionSyntax oce && oce.Initializer != null)
                {
                    foreach (var expr in oce.Initializer.Expressions.OfType<AssignmentExpressionSyntax>())
                    {
                        if ((GetLeftName(expr.Left) == "Name" || GetLeftName(expr.Left) == "Tag") &&
                            expr.Right is LiteralExpressionSyntax lit &&
                            DesignerIds.IsDesignIdValue(lit.Token.ValueText))
                            return true;
                    }
                }
                if (node.Parent is MemberAccessExpressionSyntax mae && mae.Name.Identifier.Text == ExtensionMethodName)
                    return true;
                return false;
            }

            private string? GetLeftName(ExpressionSyntax expr)
            {
                if (expr is IdentifierNameSyntax id) return id.Identifier.Text;
                if (expr is MemberAccessExpressionSyntax mem) return mem.Name.Identifier.Text;
                return null;
            }

            private bool IsUiControl(ITypeSymbol? type)
            {
                while (type != null)
                {
                    if (string.Equals(type.ToString(), _targetBaseType, StringComparison.Ordinal)) return true;
                    type = type.BaseType;
                }
                return false;
            }
        }

        private const string HelperMarkerComment = "// --- Auto-Injected Helpers for Dynamic Compilation ---";

        public static bool TryRemoveDesignIds(
            string code,
            out string updatedCode,
            out int removedCount,
            out string? error)
        {
            updatedCode = code ?? string.Empty;
            removedCount = 0;
            error = null;

            if (string.IsNullOrWhiteSpace(updatedCode)) return true;

            try
            {
                var tree = CSharpSyntaxTree.ParseText(updatedCode);
                var root = tree.GetRoot();

                var remover = new DesignIdRemover();
                var newRoot = remover.Visit(root);
                removedCount = remover.RemovedCount;

                updatedCode = newRoot.ToFullString();

                // Remove auto-injected helper classes
                int markerIdx = updatedCode.IndexOf(HelperMarkerComment, StringComparison.Ordinal);
                if (markerIdx >= 0)
                {
                    // Also trim the preceding newline(s)
                    int cutStart = markerIdx;
                    while (cutStart > 0 && (updatedCode[cutStart - 1] == '\n' || updatedCode[cutStart - 1] == '\r'))
                        cutStart--;

                    updatedCode = updatedCode.Substring(0, cutStart).TrimEnd();
                    updatedCode += Environment.NewLine;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Design ID removal failed: {ex.Message}";
                return false;
            }
        }

        private class DesignIdRemover : CSharpSyntaxRewriter
        {
            public int RemovedCount { get; private set; }

            public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                // First visit children so nested calls are handled inside-out
                var visited = (InvocationExpressionSyntax)(base.VisitInvocationExpression(node) ?? node);

                if (visited.Expression is MemberAccessExpressionSyntax mae &&
                    mae.Name.Identifier.Text == ExtensionMethodName)
                {
                    // Unwrap: (expr).RegisterDesignId("...") → expr
                    var inner = mae.Expression;
                    if (inner is ParenthesizedExpressionSyntax paren)
                        inner = paren.Expression;

                    RemovedCount++;
                    return inner
                        .WithLeadingTrivia(visited.GetLeadingTrivia())
                        .WithTrailingTrivia(visited.GetTrailingTrivia());
                }

                return visited;
            }
        }

        private static int FindMaxExistingId(SyntaxNode root)
        {
            int max = 0;
            foreach (var token in root.DescendantTokens())
            {
                if (!token.IsKind(SyntaxKind.StringLiteralToken)) continue;
                if (DesignerIds.TryParseDesignNumber(token.ValueText, out var n))
                    max = Math.Max(max, n);
            }
            return max;
        }

        private static IEnumerable<MetadataReference> GetMetadataReferences(bool useAvalonia)
        {
            lock (_lockObj)
            {
                if (useAvalonia) return _cachedAvaloniaRefs ??= LoadAvaloniaRefs();
                return _cachedWpfRefs ??= LoadWpfRefs();
            }
        }

        private static List<MetadataReference> LoadAvaloniaRefs() => LoadRefsWithPattern("Avalonia*.dll", "System.Reactive.dll");
        private static List<MetadataReference> LoadWpfRefs() => LoadSystemRefs();

        private static List<MetadataReference> LoadRefsWithPattern(params string[] patterns)
        {
            var refs = new List<MetadataReference>();
            var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            LoadBaseRefs(refs, loaded);
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            if (Directory.Exists(dir))
            {
                foreach (var p in patterns)
                {
                    try { foreach (var f in Directory.GetFiles(dir, p)) AddRef(refs, loaded, f); } catch { /* skip inaccessible patterns */ }
                }
            }
            return refs;
        }

        private static List<MetadataReference> LoadSystemRefs()
        {
            var refs = new List<MetadataReference>();
            var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            LoadBaseRefs(refs, loaded);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location)) continue;
                var n = asm.GetName().Name;
                if (n != null && (n.StartsWith("System.Windows") || n.StartsWith("Presentation") || n.StartsWith("WindowsBase") || n.StartsWith("System.Xaml")))
                    AddRef(refs, loaded, asm.Location);
            }
            return refs;
        }

        private static void LoadBaseRefs(List<MetadataReference> refs, HashSet<string> loaded)
        {
            var objPath = typeof(object).Assembly.Location;
            AddRef(refs, loaded, objPath);
            var dir = Path.GetDirectoryName(objPath);
            if (dir != null)
            {
                var rt = Path.Combine(dir, "System.Runtime.dll"); if (File.Exists(rt)) AddRef(refs, loaded, rt);
                var ns = Path.Combine(dir, "netstandard.dll"); if (File.Exists(ns)) AddRef(refs, loaded, ns);
            }
        }

        private static void AddRef(List<MetadataReference> refs, HashSet<string> loaded, string path)
        {
            if (string.IsNullOrEmpty(path) || loaded.Contains(path)) return;
            try { refs.Add(MetadataReference.CreateFromFile(path)); loaded.Add(path); } catch { /* skip unloadable assemblies */ }
        }
    }
}
