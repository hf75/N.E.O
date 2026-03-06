using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Neo.App
{
    public static class DesignerCodeEditor
    {
        private const string ExtensionMethodName = "RegisterDesignId";

        public static bool TryApplyDesignerEdits(
            string code,
            bool useAvalonia,
            string designId,
            IReadOnlyDictionary<string, string> updates,
            out string updatedCode,
            out string? error)
        {
            updatedCode = code ?? string.Empty;
            error = null;

            if (string.IsNullOrWhiteSpace(updatedCode) || updates == null || updates.Count == 0) return true;

            var tree = CSharpSyntaxTree.ParseText(updatedCode);
            var root = tree.GetRoot();

            var registrationCall = FindRegistrationCall(root, designId);

            if (registrationCall == null)
            {
                error = $"Control with ID '{designId}' not found via RegisterDesignId.";
                return false;
            }

            if (registrationCall.Expression is not MemberAccessExpressionSyntax mae)
            {
                error = "Invalid syntax structure."; return false;
            }

            var controlExpression = mae.Expression;
            while (controlExpression is ParenthesizedExpressionSyntax pes)
                controlExpression = pes.Expression;

            var newRoot = root;

            if (controlExpression is ObjectCreationExpressionSyntax oce)
            {
                var newOce = ApplyUpdatesToInitializer(oce, updates, useAvalonia, out var parseError);
                if (parseError != null) { error = parseError; return false; }
                newRoot = newRoot.ReplaceNode(oce, newOce);
            }
            else
            {
                var statement = registrationCall.FirstAncestorOrSelf<StatementSyntax>();

                if (statement != null && statement is ExpressionStatementSyntax exprStmt)
                {
                    var varName = "__neo_edit_" + designId.Replace(DesignerIds.NamePrefix, "").Replace(":", "");

                    var declarator = SyntaxFactory.VariableDeclarator(varName)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(registrationCall));

                    var varDecl = SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(
                            SyntaxFactory.IdentifierName("var"),
                            SyntaxFactory.SingletonSeparatedList(declarator)));

                    var assignments = new List<StatementSyntax>();
                    foreach (var kv in updates)
                    {
                        if (TryCreateValueExpression(useAvalonia, kv.Key, kv.Value, out var valExpr, out var valErr))
                        {
                            assignments.Add(SyntaxFactory.ExpressionStatement(
                                SyntaxFactory.AssignmentExpression(
                                    SyntaxKind.SimpleAssignmentExpression,
                                    SyntaxFactory.MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        SyntaxFactory.IdentifierName(varName),
                                        SyntaxFactory.IdentifierName(kv.Key)),
                                    valExpr)));
                        }
                    }

                    var newStatementExpr = exprStmt.ReplaceNode(registrationCall, SyntaxFactory.IdentifierName(varName));

                    var blockNodes = new List<StatementSyntax> { varDecl };
                    blockNodes.AddRange(assignments);
                    blockNodes.Add(newStatementExpr);

                    var block = SyntaxFactory.Block(blockNodes).NormalizeWhitespace();
                    newRoot = newRoot.ReplaceNode(statement, block);
                }
                else
                {
                    error = "Cannot edit properties in this context.";
                    return false;
                }
            }

            updatedCode = newRoot.NormalizeWhitespace().ToFullString();
            return true;
        }

        private static InvocationExpressionSyntax? FindRegistrationCall(SyntaxNode root, string designId)
        {
            return root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(inv =>
                {
                    if (inv.Expression is MemberAccessExpressionSyntax mae &&
                        mae.Name.Identifier.Text == ExtensionMethodName)
                    {
                        var args = inv.ArgumentList.Arguments;
                        if (args.Count > 0 &&
                            args[0].Expression is LiteralExpressionSyntax lit &&
                            lit.Token.ValueText == designId)
                            return true;
                    }
                    return false;
                });
        }

        private static ObjectCreationExpressionSyntax ApplyUpdatesToInitializer(
            ObjectCreationExpressionSyntax oce,
            IReadOnlyDictionary<string, string> updates,
            bool useAvalonia,
            out string? error)
        {
            error = null;
            var initializer = oce.Initializer ?? SyntaxFactory.InitializerExpression(SyntaxKind.ObjectInitializerExpression);
            var newExpressions = initializer.Expressions.ToList();

            foreach (var kv in updates)
            {
                newExpressions.RemoveAll(e =>
                    e is AssignmentExpressionSyntax aes &&
                    GetAssignedMemberName(aes.Left) == kv.Key);

                if (TryCreateValueExpression(useAvalonia, kv.Key, kv.Value, out var valExpr, out var valErr))
                {
                    var assign = SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(kv.Key),
                        valExpr);
                    newExpressions.Add(assign);
                }
                else
                {
                    error = valErr;
                    return oce;
                }
            }
            return oce.WithInitializer(initializer.WithExpressions(SyntaxFactory.SeparatedList(newExpressions)));
        }

        private static string? GetAssignedMemberName(ExpressionSyntax left)
        {
            if (left is IdentifierNameSyntax ins) return ins.Identifier.ValueText;
            if (left is MemberAccessExpressionSyntax mae) return mae.Name.Identifier.ValueText;
            return null;
        }

        private static bool TryCreateValueExpression(bool useAvalonia, string propertyName, string valueText, out ExpressionSyntax expr, out string? error)
        {
            expr = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
            error = null;
            valueText = valueText?.Trim() ?? string.Empty;

            switch (propertyName)
            {
                case "Text":
                case "Content":
                case "Header":
                case "Title":
                case "Tag":
                    expr = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(valueText));
                    return true;

                case "Width":
                case "Height":
                case "FontSize":
                    if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var dVal))
                    {
                        error = "Invalid number"; return false;
                    }
                    expr = SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(dVal));
                    return true;

                case "FontFamily":
                    expr = CreateObject(useAvalonia ? "Avalonia.Media.FontFamily" : "System.Windows.Media.FontFamily",
                        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(valueText)));
                    return true;

                case "FontWeight":
                    if (!SyntaxFacts.IsValidIdentifier(valueText)) { error = "Invalid identifier"; return false; }
                    expr = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ParseTypeName(useAvalonia ? "Avalonia.Media.FontWeight" : "System.Windows.FontWeights"),
                        SyntaxFactory.IdentifierName(valueText));
                    return true;

                case "FontStyle":
                    if (!SyntaxFacts.IsValidIdentifier(valueText)) { error = "Invalid identifier"; return false; }
                    expr = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ParseTypeName(useAvalonia ? "Avalonia.Media.FontStyle" : "System.Windows.FontStyles"),
                        SyntaxFactory.IdentifierName(valueText));
                    return true;

                case "Foreground":
                case "Background":
                case "BorderBrush":
                case "Fill":
                case "Stroke":
                    if (!TryParseHexColor(valueText, out var a, out var r, out var g, out var b, out var cErr))
                    {
                        error = cErr; return false;
                    }
                    var colorType = useAvalonia ? "Avalonia.Media.Color" : "System.Windows.Media.Color";
                    var brushType = useAvalonia ? "Avalonia.Media.SolidColorBrush" : "System.Windows.Media.SolidColorBrush";

                    var colorCall = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.ParseTypeName(colorType), SyntaxFactory.IdentifierName("FromArgb")))
                        .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]{
                            SyntaxFactory.Argument(NumLit(a)), SyntaxFactory.Argument(NumLit(r)), SyntaxFactory.Argument(NumLit(g)), SyntaxFactory.Argument(NumLit(b))
                        })));

                    expr = CreateObject(brushType, colorCall);
                    return true;

                case "Margin":
                case "Padding":
                case "BorderThickness":
                    if (!TryParseThickness(valueText, out var t1, out var t2, out var t3, out var t4, out var tErr))
                    {
                        error = tErr; return false;
                    }
                    var thickType = useAvalonia ? "Avalonia.Thickness" : "System.Windows.Thickness";
                    expr = CreateObject(thickType, NumLit(t1), NumLit(t2), NumLit(t3), NumLit(t4));
                    return true;
            }
            error = $"Unsupported property '{propertyName}'";
            return false;
        }

        private static ObjectCreationExpressionSyntax CreateObject(string type, params ExpressionSyntax[] args)
        {
            var argList = args.Select(a => SyntaxFactory.Argument(a));
            return SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(type))
                .WithNewKeyword(SyntaxFactory.Token(SyntaxKind.NewKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(argList)));
        }

        private static LiteralExpressionSyntax NumLit(object val)
        {
            if (val is double d) return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(d));
            if (val is int i) return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(i));
            if (val is byte b) return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal((int)b));
            return SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0));
        }

        private static bool TryParseHexColor(string t, out byte a, out byte r, out byte g, out byte b, out string? e)
        {
            a = 255; r = g = b = 0; e = null; t = (t ?? "").Trim().TrimStart('#');
            if (t.Length == 6)
            {
                if (!byte.TryParse(t.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) ||
                   !byte.TryParse(t.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) ||
                   !byte.TryParse(t.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b)) { e = "Invalid hex"; return false; }
                return true;
            }
            if (t.Length == 8)
            {
                if (!byte.TryParse(t.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a) ||
                   !byte.TryParse(t.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) ||
                   !byte.TryParse(t.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) ||
                   !byte.TryParse(t.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b)) { e = "Invalid hex"; return false; }
                return true;
            }
            e = "Invalid format"; return false;
        }

        private static bool TryParseThickness(string t, out double v1, out double v2, out double v3, out double v4, out string? e)
        {
            v1 = v2 = v3 = v4 = 0; e = null; t = (t ?? "").Trim();
            var p = t.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 1 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { v1 = v2 = v3 = v4 = v; return true; }
            if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v_)) { v1 = v3 = h; v2 = v4 = v_; return true; }
            if (p.Length == 4 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out v1) && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v2) && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out v3) && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out v4)) return true;
            e = "Invalid thickness"; return false;
        }
    }
}
