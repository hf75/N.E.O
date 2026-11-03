using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Neo.ErrorSurfacing;

/// <summary>
/// Rewrites one tree of an app for <see cref="LostExceptions"/>. Decisions come from the semantic
/// model of the code as written (visit the original node, build from the visited one); what it
/// inserts never contains a line break, so every line stays where it was.
/// </summary>
internal sealed class SurfacingRewriter : CSharpSyntaxRewriter
{
    private const string Runtime = "global::NeoRuntime.LostExceptions";

    // Where an API runs app code so that its exceptions vanish, found by trying it in the preview
    // windows (Neo.McpServer.Tests, LostExceptionProcessTests). Avalonia runs every DispatcherTimer
    // tick through Dispatcher.InvokeAsync, which keeps the exception in a task nobody awaits, and
    // the timer stops; System.Timers.Timer catches the exception and goes on.
    private static readonly (string Type, string Event)[] s_losingEvents =
    [
        ("Avalonia.Threading.DispatcherTimer", "Tick"),
        ("System.Timers.Timer", "Elapsed"),
    ];

    // Types whose constructors and methods take such handlers as delegates: DispatcherTimer's
    // constructor with a callback, DispatcherTimer.Run and DispatcherTimer.RunOnce.
    private static readonly string[] s_losingCallbackTypes = ["Avalonia.Threading.DispatcherTimer"];

    private static readonly HashSet<string> s_losingEventNames = s_losingEvents.Select(e => e.Event).ToHashSet();

    private readonly SemanticModel _model;

    public SurfacingRewriter(SemanticModel model) => _model = model;

    public bool Changed { get; private set; }

    /// <summary>The API types and events whose delegates get wrapped, for the tests.</summary>
    internal static IReadOnlyList<(string Type, string Event)> LosingEvents => s_losingEvents;
    internal static IReadOnlyList<string> LosingCallbackTypes => s_losingCallbackTypes;

    // ── Tasks the code drops ──

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        var visited = (ExpressionStatementSyntax)base.VisitExpressionStatement(node)!;

        // "_ = LoadAsync();" drops the task just as "LoadAsync();" does.
        if (node.Expression is AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.ValueText: "_" } } assignment
            && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && _model.GetOperation(assignment) is ISimpleAssignmentOperation { Target: IDiscardOperation })
        {
            return Dropped(assignment.Right, ((AssignmentExpressionSyntax)visited.Expression).Right) is { } forget
                ? visited.WithExpression(forget.WithTriviaFrom(visited.Expression))
                : visited;
        }

        return MayBeAwaitable(node.Expression) && Dropped(node.Expression, visited.Expression) is { } call
            ? visited.WithExpression(call.WithTriviaFrom(visited.Expression))
            : visited;
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
        => VisitLambda(node, (LambdaExpressionSyntax)base.VisitParenthesizedLambdaExpression(node)!);

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
        => VisitLambda(node, (LambdaExpressionSyntax)base.VisitSimpleLambdaExpression(node)!);

    // "(s, e) => LoadAsync()" as a void delegate drops the task too. Not in expression trees.
    private SyntaxNode VisitLambda(LambdaExpressionSyntax node, LambdaExpressionSyntax visited)
    {
        if (node.ExpressionBody is not { } body || !MayBeAwaitable(body)) return visited;
        if (_model.GetSymbolInfo(node).Symbol is not IMethodSymbol { ReturnsVoid: true }) return visited;
        if (_model.GetTypeInfo(node).ConvertedType is INamedTypeSymbol { Name: "Expression", ContainingNamespace: var ns }
            && ns.ToDisplayString() == "System.Linq.Expressions") return visited;

        return Dropped(body, visited.ExpressionBody!) is { } call
            ? visited.WithExpressionBody(call.WithTriviaFrom(visited.ExpressionBody!))
            : visited;
    }

    // "void Refresh() => LoadAsync();": methods, local functions, accessors that return nothing.
    public override SyntaxNode? VisitArrowExpressionClause(ArrowExpressionClauseSyntax node)
    {
        var visited = (ArrowExpressionClauseSyntax)base.VisitArrowExpressionClause(node)!;
        if (!MayBeAwaitable(node.Expression)) return visited;

        var owner = node.Parent switch
        {
            AccessorDeclarationSyntax accessor => _model.GetDeclaredSymbol(accessor),
            BaseMethodDeclarationSyntax method => _model.GetDeclaredSymbol(method),
            LocalFunctionStatementSyntax local => _model.GetDeclaredSymbol(local),
            _ => null,   // properties and indexers return their value
        };
        return owner is IMethodSymbol { ReturnsVoid: true } && Dropped(node.Expression, visited.Expression) is { } call
            ? visited.WithExpression(call.WithTriviaFrom(visited.Expression))
            : visited;
    }

    // Only these can have an awaitable type where a value is dropped; skips the semantic model for the rest.
    private static bool MayBeAwaitable(ExpressionSyntax expression) => expression is
        InvocationExpressionSyntax or AwaitExpressionSyntax or ConditionalAccessExpressionSyntax or BaseObjectCreationExpressionSyntax;

    /// <summary>The call that hands a dropped awaitable to the runtime; null when <paramref name="original"/> is not awaitable.</summary>
    private ExpressionSyntax? Dropped(ExpressionSyntax original, ExpressionSyntax visited)
    {
        if (IsRuntimeCall(original)) return null;
        var type = _model.GetTypeInfo(original).Type;
        if (type == null || type.TypeKind is TypeKind.Error or TypeKind.Dynamic) return null;

        var bare = visited.WithoutTrivia();
        ExpressionSyntax call;
        if (IsTaskLike(type))
        {
            // Task, Task<T>, ValueTask, ValueTask<T>: overloads of their own ("?." gives null for a Task)
            call = RuntimeCall("Forget(__neo)", bare);
        }
        else if (IsAwaitable(Unnullable(type), original.SpanStart))
        {
            // Anything else that "await" takes: by its awaiter, which "?." makes nullable
            call = RuntimeCall("Forget(__neo, static __neoAwaiter => __neoAwaiter.GetResult())", WithGetAwaiter(bare));
        }
        else return null;

        Changed = true;
        return call;
    }

    private static bool IsTaskLike(ITypeSymbol type)
    {
        for (var t = type as INamedTypeSymbol; t != null; t = t.BaseType)
        {
            var name = t.OriginalDefinition.ToDisplayString();
            if (name is "System.Threading.Tasks.Task" or "System.Threading.Tasks.Task<TResult>") return true;
        }
        var definition = type.OriginalDefinition.ToDisplayString();
        return definition is "System.Threading.Tasks.ValueTask" or "System.Threading.Tasks.ValueTask<TResult>";
    }

    // "x?.LoadAsync()" of a value-type awaitable is a Nullable<T>; its awaiter is reached inside the "?.".
    private static ITypeSymbol Unnullable(ITypeSymbol type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : type;

    /// <summary>The awaitable pattern of the language: a GetAwaiter() whose result has IsCompleted, GetResult() and INotifyCompletion.</summary>
    private bool IsAwaitable(ITypeSymbol type, int position)
    {
        foreach (var member in _model.LookupSymbols(position, type, "GetAwaiter", includeReducedExtensionMethods: true))
        {
            if (member is not IMethodSymbol { Parameters.Length: 0 } getAwaiter) continue;
            var awaiter = getAwaiter.ReturnType;
            if (!awaiter.AllInterfaces.Any(i => i.ToDisplayString() == "System.Runtime.CompilerServices.INotifyCompletion")) continue;
            if (!awaiter.GetMembers("IsCompleted").OfType<IPropertySymbol>().Any(p => p.Type.SpecialType == SpecialType.System_Boolean)) continue;
            if (!awaiter.GetMembers("GetResult").OfType<IMethodSymbol>().Any(m => m.Parameters.Length == 0)) continue;
            return true;
        }
        return false;
    }

    // "x.LoadAsync()" → "x.LoadAsync().GetAwaiter()"; "x?.LoadAsync()" → "x?.LoadAsync().GetAwaiter()".
    private static ExpressionSyntax WithGetAwaiter(ExpressionSyntax expression)
    {
        if (expression is ConditionalAccessExpressionSyntax conditional)
            return conditional.WithWhenNotNull(WithGetAwaiter(conditional.WhenNotNull));
        var receiver = expression is InvocationExpressionSyntax or MemberAccessExpressionSyntax or ElementAccessExpressionSyntax
            or MemberBindingExpressionSyntax or ElementBindingExpressionSyntax or IdentifierNameSyntax or ParenthesizedExpressionSyntax
            ? expression
            : SyntaxFactory.ParenthesizedExpression(expression);
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiver, SyntaxFactory.IdentifierName("GetAwaiter")));
    }

    // ── Handlers that APIs run without letting their exceptions out ──

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var visited = (AssignmentExpressionSyntax)base.VisitAssignmentExpression(node)!;
        if (!node.IsKind(SyntaxKind.AddAssignmentExpression) && !node.IsKind(SyntaxKind.SubtractAssignmentExpression)) return visited;
        if (!s_losingEventNames.Contains(LastName(node.Left))) return visited;

        // "+=" and "-=" both: the runtime hands out the same wrapper for the same handler.
        if (_model.GetOperation(node) is IEventAssignmentOperation { EventReference: IEventReferenceOperation { Event: var ev } }
            && LosesExceptions(ev) && !IsRuntimeCall(node.Right))
        {
            Changed = true;
            return visited.WithRight(Guard(visited.Right, ev.Type));
        }
        return visited;
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
        return node.ArgumentList != null && GuardArguments(node, node.ArgumentList, visited.ArgumentList!) is { } arguments
            ? visited.WithArgumentList(arguments)
            : visited;
    }

    public override SyntaxNode? VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        var visited = (ImplicitObjectCreationExpressionSyntax)base.VisitImplicitObjectCreationExpression(node)!;
        return GuardArguments(node, node.ArgumentList, visited.ArgumentList) is { } arguments ? visited.WithArgumentList(arguments) : visited;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        return GuardArguments(node, node.ArgumentList, visited.ArgumentList) is { } arguments ? visited.WithArgumentList(arguments) : visited;
    }

    /// <summary>The argument list with every delegate argument for one of the losing types wrapped; null when there is none.</summary>
    private ArgumentListSyntax? GuardArguments(ExpressionSyntax node, ArgumentListSyntax original, ArgumentListSyntax visited)
    {
        if (original.Arguments.Count == 0) return null;
        var (member, arguments) = _model.GetOperation(node) switch
        {
            IObjectCreationOperation creation => ((ISymbol?)creation.Constructor, creation.Arguments),
            IInvocationOperation invocation => (invocation.TargetMethod, invocation.Arguments),
            _ => (null, default(System.Collections.Immutable.ImmutableArray<IArgumentOperation>)),
        };
        if (member == null || !s_losingCallbackTypes.Contains(member.ContainingType.OriginalDefinition.ToDisplayString())) return null;

        var result = visited;
        foreach (var argument in arguments)
        {
            if (argument.ArgumentKind != ArgumentKind.Explicit || argument.Parameter?.Type.TypeKind != TypeKind.Delegate) continue;
            if (argument.Syntax is not ArgumentSyntax syntax || IsRuntimeCall(syntax.Expression)) continue;
            var index = original.Arguments.IndexOf(syntax);
            if (index < 0) continue;
            var current = result.Arguments[index];
            result = result.WithArguments(result.Arguments.Replace(current, current.WithExpression(Guard(current.Expression, argument.Parameter.Type))));
        }
        if (result == visited) return null;
        Changed = true;
        return result;
    }

    private static bool LosesExceptions(IEventSymbol ev)
    {
        var type = ev.ContainingType.OriginalDefinition.ToDisplayString();
        return s_losingEvents.Any(e => e.Type == type && e.Event == ev.Name);
    }

    private static ExpressionSyntax Guard(ExpressionSyntax handler, ITypeSymbol delegateType)
    {
        var type = delegateType.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return RuntimeCall($"Guard<{type}>(__neo)", handler.WithoutTrivia()).WithTriviaFrom(handler);
    }

    // ── Helpers ──

    /// <summary>A call of the runtime, parsed from a template whose first argument "__neo" becomes <paramref name="argument"/>.</summary>
    private static ExpressionSyntax RuntimeCall(string template, ExpressionSyntax argument)
    {
        var call = (InvocationExpressionSyntax)SyntaxFactory.ParseExpression($"{Runtime}.{template}");
        var first = call.ArgumentList.Arguments[0];
        return call.WithArgumentList(call.ArgumentList.WithArguments(call.ArgumentList.Arguments.Replace(first, first.WithExpression(argument))));
    }

    private static bool IsRuntimeCall(ExpressionSyntax expression)
        => expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: var runtime } }
           && runtime.ToString() == Runtime;

    private static string LastName(ExpressionSyntax expression) => expression switch
    {
        SimpleNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => "",
    };
}
