using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace Neo.App
{
    public static class LoopUnroller
    {
        public static string UnrollLoops(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return code;

            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();

            var rewriter = new LoopUnrollRewriter();
            var newRoot = rewriter.Visit(root);

            return newRoot.NormalizeWhitespace().ToFullString();
        }

        private class LoopUnrollRewriter : CSharpSyntaxRewriter
        {
            public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
            {
                // 1. Versuchen, die Elemente zu finden (jetzt auch über Variablennamen!)
                var elements = GetCollectionElements(node, node.Expression);

                // Wenn keine Elemente gefunden wurden (z.B. Datenbankaufruf oder komplexer Code), Abbruch.
                if (elements == null || elements.Count == 0)
                {
                    return base.VisitForEachStatement(node);
                }

                // 2. Unrolling vorbereiten
                var loopBody = node.Statement;
                var loopVariable = node.Identifier.Text;
                var loopType = node.Type;

                var unrolledBlocks = new List<StatementSyntax>();

                foreach (var elementExpr in elements)
                {
                    // Erstelle: var feature = "Elegant Design";
                    var variableDeclarator = SyntaxFactory.VariableDeclarator(loopVariable)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(elementExpr));

                    var variableDeclaration = SyntaxFactory.VariableDeclaration(
                        loopType,
                        SyntaxFactory.SingletonSeparatedList(variableDeclarator));

                    var localDecl = SyntaxFactory.LocalDeclarationStatement(variableDeclaration);

                    // Baue einen neuen Scope-Block { var feature = ...; Body... }
                    var blockStatements = new List<StatementSyntax>();
                    blockStatements.Add(localDecl);

                    if (loopBody is BlockSyntax block)
                    {
                        blockStatements.AddRange(block.Statements);
                    }
                    else
                    {
                        blockStatements.Add(loopBody);
                    }

                    unrolledBlocks.Add(SyntaxFactory.Block(blockStatements));
                }

                // Ersetze die foreach-Schleife durch die Liste der Blöcke
                return SyntaxFactory.Block(unrolledBlocks)
                    .WithLeadingTrivia(node.GetLeadingTrivia())
                    .WithTrailingTrivia(node.GetTrailingTrivia());
            }

            private List<ExpressionSyntax>? GetCollectionElements(SyntaxNode originNode, ExpressionSyntax expression)
            {
                // Fall 1: Inline Array (new[] { "A", "B" })
                if (expression is ImplicitArrayCreationExpressionSyntax implicitArray)
                {
                    return implicitArray.Initializer?.Expressions.ToList();
                }

                // Fall 2: Explizites Array (new string[] { "A", "B" })
                if (expression is ArrayCreationExpressionSyntax explicitArray)
                {
                    return explicitArray.Initializer?.Expressions.ToList();
                }

                // Fall 3: Variable (foreach (var x in features))
                // Hier müssen wir rückwärts im Code suchen, wo "features" definiert wurde.
                if (expression is IdentifierNameSyntax identifier)
                {
                    var variableName = identifier.Identifier.Text;
                    var initializer = FindVariableInitializer(originNode, variableName);

                    if (initializer != null)
                    {
                        // Rekursiver Aufruf: Das gefundene Initializer-Element könnte wieder ein Array sein
                        return GetCollectionElements(originNode, initializer);
                    }
                }

                return null;
            }

            private ExpressionSyntax? FindVariableInitializer(SyntaxNode startNode, string variableName)
            {
                // Wir gehen den Syntax-Baum hoch (zum Block), um den Scope zu finden
                var current = startNode.Parent;
                while (current != null)
                {
                    if (current is BlockSyntax block)
                    {
                        // Suche in allen Statements VOR der Schleife in diesem Block
                        foreach (var statement in block.Statements)
                        {
                            // Wir stoppen, wenn wir beim StartNode (der Loop) angekommen sind
                            // (einfacher Check: Position im Source)
                            if (statement.SpanStart >= startNode.SpanStart) break;

                            if (statement is LocalDeclarationStatementSyntax decl)
                            {
                                foreach (var variable in decl.Declaration.Variables)
                                {
                                    if (variable.Identifier.Text == variableName && variable.Initializer != null)
                                    {
                                        return variable.Initializer.Value;
                                    }
                                }
                            }
                        }
                    }
                    current = current.Parent;
                }
                return null;
            }
        }
    }
}