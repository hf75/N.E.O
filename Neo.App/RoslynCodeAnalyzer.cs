// RoslynCodeAnalyzer.cs (KORRIGIERT & VERBESSERT)
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Neo.App
{
    public static class RoslynCodeAnalyzer
    {
        public static List<string> ExtractSignatures(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return new List<string>();
            }

            SyntaxTree tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();

            var visitor = new SignatureExtractor();
            visitor.Visit(root);

            return visitor.Signatures;
        }

        public static string ApplyPatches(string originalCode, List<PatchOperation> patches)
        {
            if (patches == null || !patches.Any())
            {
                return originalCode;
            }

            var tree = CSharpSyntaxTree.ParseText(originalCode);
            var root = tree.GetRoot();

            var rewriter = new CodePatcher(patches);
            var newRoot = rewriter.Visit(root);

            var workspace = new AdhocWorkspace();
            var formattedRoot = Formatter.Format(newRoot, workspace);

            return formattedRoot.ToFullString();
        }

        private class CodePatcher : CSharpSyntaxRewriter
        {
            private readonly List<PatchOperation> _patches;
            // VERBESSERT: Wir erstellen für jeden Aufruf eine neue Instanz, um Zustandsprobleme zu vermeiden.
            // private readonly SignatureExtractor _signatureExtractor = new SignatureExtractor();

            public CodePatcher(List<PatchOperation> patches)
            {
                _patches = patches;
            }

            private string GetSignature(SyntaxNode node)
            {
                // VERBESSERT: Immer eine neue, saubere Instanz verwenden.
                var extractor = new SignatureExtractor();
                extractor.Visit(node);
                return extractor.Signatures.FirstOrDefault()!;
            }

            // Die Visit-Methoden bleiben strukturell gleich, verlassen sich aber jetzt
            // auf die robustere GetSignature-Methode.

            public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
            {
                var signature = GetSignature(node);

                var deletePatch = _patches.FirstOrDefault(p => p.Operation == "DELETE" && p.Signature == signature);
                if (deletePatch != null)
                {
                    return null!;
                }

                var replacePatch = _patches.FirstOrDefault(p => p.Operation == "REPLACE" && p.Signature == signature);
                if (replacePatch != null)
                {
                    var newClassNode = CSharpSyntaxTree.ParseText(replacePatch.NewContent)
                                                       .GetRoot()
                                                       .DescendantNodes()
                                                       .OfType<ClassDeclarationSyntax>()
                                                       .FirstOrDefault();
                    // Wichtig: Den neuen Knoten weiter besuchen, falls es verschachtelte Änderungen gibt
                    return base.Visit(newClassNode ?? node)!;
                }

                var addPatches = _patches.Where(p => p.Operation == "ADD" && p.ParentSignature == signature).ToList();
                if (addPatches.Any())
                {
                    var newMembers = new List<MemberDeclarationSyntax>();
                    foreach (var patch in addPatches)
                    {
                        var member = CSharpSyntaxTree.ParseText(patch.NewContent)
                                                     .GetRoot()
                                                     .DescendantNodes()
                                                     .OfType<MemberDeclarationSyntax>()
                                                     .FirstOrDefault();
                        if (member != null)
                        {
                            newMembers.Add(member);
                        }
                    }
                    var updatedNode = node.AddMembers(newMembers.ToArray());
                    return base.VisitClassDeclaration(updatedNode)!;
                }

                return base.VisitClassDeclaration(node)!;
            }

            public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                var signature = GetSignature(node);

                var deletePatch = _patches.FirstOrDefault(p => p.Operation == "DELETE" && p.Signature == signature);
                if (deletePatch != null)
                {
                    return null!;
                }

                var replacePatch = _patches.FirstOrDefault(p => p.Operation == "REPLACE" && p.Signature == signature);
                if (replacePatch != null)
                {
                    var newMethodNode = CSharpSyntaxTree.ParseText(replacePatch.NewContent)
                                                        .GetRoot()
                                                        .DescendantNodes()
                                                        .OfType<MethodDeclarationSyntax>()
                                                        .FirstOrDefault();
                    // Hier kein base.Visit, da wir die Methode als atomare Einheit ersetzen.
                    return newMethodNode ?? node;
                }

                return base.VisitMethodDeclaration(node)!;
            }
        }

        // =================================================================================
        // HIER IST DIE ENTSCHEIDENDE KORREKTUR
        // =================================================================================
        private class SignatureExtractor : CSharpSyntaxWalker
        {
            public List<string> Signatures { get; } = new List<string>();

            // KORRIGIERT: Robuste Signatur-Erstellung aus semantischen Teilen
            public override void VisitClassDeclaration(ClassDeclarationSyntax node)
            {
                var sb = new StringBuilder();
                sb.Append(node.Modifiers.ToFullString().Trim());
                sb.Append(" class ");
                sb.Append(node.Identifier.ToString());
                if (node.BaseList != null)
                {
                    sb.Append(" ");
                    sb.Append(node.BaseList.ToString());
                }
                Signatures.Add(sb.ToString().Replace("  ", " ").Trim());
                base.VisitClassDeclaration(node);
            }

            // KORRIGIERT: Robuste Signatur-Erstellung aus semantischen Teilen
            public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
            {
                var sb = new StringBuilder();
                sb.Append(node.Modifiers.ToFullString().Trim());
                sb.Append(" ");
                sb.Append(node.Identifier.ToString());
                sb.Append(node.ParameterList.ToString());
                Signatures.Add(sb.ToString().Replace("  ", " ").Trim());
            }

            // KORRIGIERT: Robuste Signatur-Erstellung aus semantischen Teilen
            public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                var sb = new StringBuilder();
                sb.Append(node.Modifiers.ToFullString().Trim());
                sb.Append(" ");
                sb.Append(node.ReturnType.ToString());
                sb.Append(" ");
                sb.Append(node.Identifier.ToString());
                sb.Append(node.ParameterList.ToString());
                Signatures.Add(sb.ToString().Replace("  ", " ").Trim());
            }

            // KORRIGIERT: Robuste Signatur-Erstellung aus semantischen Teilen
            public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
            {
                var sb = new StringBuilder();
                sb.Append(node.Modifiers.ToFullString().Trim());
                sb.Append(" ");
                sb.Append(node.Type.ToString());
                sb.Append(" ");
                sb.Append(node.Identifier.ToString());
                Signatures.Add(sb.ToString().Replace("  ", " ").Trim());
            }
        }
    }
}