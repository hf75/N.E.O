using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Neo.IPC;

namespace Neo.HotReload;

/// <summary>What has to happen around the deltas so that the running app really is the new app.</summary>
internal sealed class StateMigrationPlan
{
    /// <summary>Helper members to add to the new source before it goes to Roslyn: (type declaration, member text).</summary>
    public List<(TypeDeclarationSyntax Type, string MemberSource)> Injections { get; } = [];

    public List<HotReloadStateStep> Steps { get; } = [];

    public bool RebuildUi { get; set; }

    public string? RebuildMethodName { get; set; }

    /// <summary>Set when patching the running app would leave it half-updated; the host then reloads fully.</summary>
    public string? NotPossibleReason { get; set; }
}

/// <summary>
/// Edit-and-continue patches code, but it never runs a constructor or an initializer again for
/// objects that already exist — and the app instance surviving the update is the whole point.
/// This analysis finds what the surviving objects would be missing and plans how they get it:
///
/// <list type="bullet">
///   <item><b>Added fields with initializers</b> would be null/0 in existing objects (the classic
///         NullReferenceException right after a hot reload). A static helper that evaluates the
///         initializers is injected into the declaring type; the plugin window calls it once per
///         live object and stores the values.</item>
///   <item><b>Changed initializers of configuration-like fields</b> (readonly value types, strings,
///         brushes; static readonly anything) are re-applied the same way — "make the accent colour
///         blue" has to show. Mutable fields are user state and keep their value.</item>
///   <item><b>Statements added to the control's constructor</b> ("start the clock") are replayed once
///         on the live control. Any other constructor change cannot be replayed → full reload.</item>
///   <item><b>UI code</b>: the visual tree was built by the old code. If something that runs while
///         the UI is built has changed, the app's re-runnable build method is called again; an app
///         without such a method gets a full reload instead of a stale UI.</item>
/// </list>
/// </summary>
internal static class StateMigrationAnalyzer
{
    public const string HelperPrefix = "__neo_hr_";

    /// <summary>Names an app may give the method that (re)builds its visual tree from its fields.</summary>
    public static readonly string[] RebuildMethodNames =
    [
        "BuildUi", "RebuildUi", "BuildLayout", "BuildView", "BuildContent", "BuildInterface",
        "InitializeUi", "CreateUi", "SetupUi", "CreateContent", "InitializeComponent", "InitializeComponents",
    ];

    public static StateMigrationPlan Analyze(Compilation oldCompilation, Compilation newCompilation, string rootTypeName, int generation)
    {
        var plan = new StateMigrationPlan();
        var models = new Dictionary<SyntaxTree, SemanticModel>();
        SemanticModel Model(SyntaxTree tree) => models.TryGetValue(tree, out var m) ? m : models[tree] = newCompilation.GetSemanticModel(tree);

        var rootType = SourceTypes(newCompilation).FirstOrDefault(t => t.Name == rootTypeName && t.TypeKind == TypeKind.Class);

        // ── 1. state the surviving objects are missing ────────────────────────────────────────
        foreach (var newType in SourceTypes(newCompilation))
        {
            if (newType.TypeKind != TypeKind.Class || IsOrIsInsideGeneric(newType)) continue;
            if (oldCompilation.GetTypeByMetadataName(MetadataName(newType)) is not { } oldType) continue; // new type: nothing alive yet
            if (newType.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not TypeDeclarationSyntax declaration) continue;
            if (declaration.OpenBraceToken.IsMissing || declaration.OpenBraceToken.IsKind(SyntaxKind.None)) continue; // "record R(int X);"

            var instance = new List<(string Field, string Type, string Init)>();
            var statics = new List<(string Target, string Type, string Init, bool ReadOnly)>();

            foreach (var member in newType.GetMembers())
            {
                if (member.IsImplicitlyDeclared || member.Name.StartsWith(HelperPrefix, StringComparison.Ordinal)) continue;

                ExpressionSyntax? init; TypeSyntax? typeSyntax; ExpressionSyntax? oldInit; bool existed; bool readOnly; bool assignable;
                ITypeSymbol valueType; string storageName;

                if (member is IFieldSymbol { IsConst: false } field &&
                    field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax declarator)
                {
                    init = declarator.Initializer?.Value;
                    typeSyntax = (declarator.Parent as VariableDeclarationSyntax)?.Type;
                    var oldField = oldType.GetMembers(field.Name).OfType<IFieldSymbol>().FirstOrDefault();
                    existed = oldField != null;
                    oldInit = (oldField?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax)?.Initializer?.Value;
                    readOnly = field.IsReadOnly; assignable = true; valueType = field.Type; storageName = field.Name;
                }
                else if (member is IPropertySymbol property &&
                         property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is PropertyDeclarationSyntax propertyDeclaration)
                {
                    init = propertyDeclaration.Initializer?.Value;
                    typeSyntax = propertyDeclaration.Type;
                    var oldProperty = oldType.GetMembers(property.Name).OfType<IPropertySymbol>().FirstOrDefault();
                    existed = oldProperty != null;
                    oldInit = (oldProperty?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as PropertyDeclarationSyntax)?.Initializer?.Value;
                    readOnly = property.SetMethod == null || property.SetMethod.IsInitOnly;
                    assignable = !readOnly; valueType = property.Type;
                    storageName = $"<{property.Name}>k__BackingField";
                }
                else continue;

                if (init == null || typeSyntax == null) continue;

                bool initializerChanged = existed && (oldInit == null || !SyntaxFactory.AreEquivalent(oldInit, init));
                if (existed && !initializerChanged) continue;

                var initText = init is InitializerExpressionSyntax ? $"new {typeSyntax} {init}" : init.ToString(); // int[] a = { 1, 2 };

                if (member.IsStatic)
                {
                    // Statics rarely hold what the user did; a changed "static readonly" is a changed constant.
                    if (existed && !(readOnly || IsConfigurationLike(valueType))) continue;
                    if (member is IPropertySymbol && !assignable) continue; // get-only static auto-property: no way to reach the backing field from C#
                    statics.Add((member.Name, typeSyntax.ToString(), initText, readOnly && member is IFieldSymbol));
                }
                else
                {
                    // An existing mutable field is user state. Only configuration gets re-applied.
                    if (existed && !(readOnly && IsConfigurationLike(valueType))) continue;
                    instance.Add((storageName, typeSyntax.ToString(), initText));
                }
            }

            var typeName = MetadataName(newType);
            if (instance.Count > 0)
            {
                // Static on purpose: that is the context field initializers are bound in (no "this").
                var name = $"{HelperPrefix}init_{generation}";
                var locals = string.Concat(instance.Select((f, i) => $"    {f.Type} __v{i} = {f.Init};\n"));
                var values = string.Join(", ", instance.Select((_, i) => $"__v{i}"));
                plan.Injections.Add((declaration, $"private static object[] {name}()\n{{\n{locals}    return new object[] {{ {values} }};\n}}\n"));
                plan.Steps.Add(new HotReloadStateStep(typeName, name, instance.Select(f => f.Field).ToList(), IsStatic: false));
            }
            if (statics.Count > 0)
            {
                var name = $"{HelperPrefix}sinit_{generation}";
                var body = string.Concat(statics.Select((f, i) =>
                    $"    {f.Type} __s{i} = {f.Init};\n" +
                    (f.ReadOnly
                        ? $"    global::System.Runtime.CompilerServices.Unsafe.AsRef(in {f.Target}) = __s{i};\n"
                        : $"    {f.Target} = __s{i};\n")));
                plan.Injections.Add((declaration, $"private static void {name}()\n{{\n{body}}}\n"));
                plan.Steps.Add(new HotReloadStateStep(typeName, name, [], IsStatic: true));
            }
        }

        if (rootType == null)
        {
            plan.NotPossibleReason = $"the new code has no class named {rootTypeName}";
            return plan;
        }

        // ── 2. the control's constructor ──────────────────────────────────────────────────────
        var oldRoot = oldCompilation.GetTypeByMetadataName(MetadataName(rootType));
        if (oldRoot == null)
        {
            plan.NotPossibleReason = $"{rootTypeName} moved to another namespace";
            return plan;
        }

        var newCtors = ExplicitInstanceConstructors(rootType);
        var oldCtors = ExplicitInstanceConstructors(oldRoot);
        if (newCtors.Count == 1 && oldCtors.Count == 1)
        {
            if (!TryGetAddedStatements(oldCtors[0], newCtors[0], out var added))
            {
                plan.NotPossibleReason = $"the constructor of {rootTypeName} changed in a way that cannot be replayed on the running instance";
                return plan;
            }

            var hookNames = new HashSet<string>(RebuildMethodNames, StringComparer.OrdinalIgnoreCase);
            added = added.Where(s => !InvokesAny(s, hookNames)).ToList(); // the UI rebuild below takes care of those
            if (added.Count > 0 && rootType.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is TypeDeclarationSyntax rootDeclaration)
            {
                var name = $"{HelperPrefix}ctor_{generation}";
                var body = string.Concat(added.Select(s => "    " + s.ToString().Trim() + "\n"));
                plan.Injections.Add((rootDeclaration, $"private void {name}()\n{{\n{body}}}\n"));
                plan.Steps.Add(new HotReloadStateStep(MetadataName(rootType), name, [], IsStatic: false));
            }
        }
        else if (newCtors.Count != oldCtors.Count ||
                 newCtors.Zip(oldCtors).Any(p => !SameSetUp(p.First, p.Second)))
        {
            plan.NotPossibleReason = $"the constructors of {rootTypeName} changed";
            return plan;
        }

        // ── 3. does the UI have to be built again? ────────────────────────────────────────────
        var hook = rootType.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m =>
            m.MethodKind == MethodKind.Ordinary && !m.IsStatic && m.Parameters.Length == 0 && !m.IsGenericMethod &&
            RebuildMethodNames.Contains(m.Name, StringComparer.OrdinalIgnoreCase));

        var oldIndex = MemberIndex(oldCompilation, t => oldCompilation.GetSemanticModel(t));
        var changed = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var (key, (symbol, node)) in MemberIndex(newCompilation, Model))
        {
            if (!oldIndex.TryGetValue(key, out var old) || !BuildTimeEquivalent(old.Node, node))
                changed.Add(symbol);
        }

        var start = hook != null ? [hook] : rootType.InstanceConstructors.Cast<ISymbol>().ToList();
        var ranWhileBuilding = BuildTimeReachable(start, newCompilation, Model);
        bool uiCodeChanged = changed.Any(ranWhileBuilding.Contains);

        if (hook != null)
        {
            plan.RebuildMethodName = hook.Name;
            plan.RebuildUi = uiCodeChanged || plan.Steps.Count > 0;
        }
        else if (uiCodeChanged)
        {
            plan.NotPossibleReason =
                $"code that builds the UI changed, but {rootTypeName} has no re-runnable UI method " +
                $"(a parameterless {RebuildMethodNames[0]}() that the constructor calls)";
        }

        return plan;
    }

    // ── symbols ───────────────────────────────────────────────────────────────────────────────

    public static IEnumerable<INamedTypeSymbol> SourceTypes(Compilation compilation)
    {
        var pending = new Stack<INamespaceOrTypeSymbol>();
        pending.Push(compilation.Assembly.GlobalNamespace);
        while (pending.Count > 0)
        {
            foreach (var member in pending.Pop().GetMembers())
            {
                if (member is INamespaceSymbol ns) pending.Push(ns);
                else if (member is INamedTypeSymbol type) { yield return type; pending.Push(type); }
            }
        }
    }

    /// <summary>The name <c>Assembly.GetType</c> understands: <c>Namespace.Outer+Inner</c>.</summary>
    public static string MetadataName(INamedTypeSymbol type)
        => type.ContainingType != null
            ? MetadataName(type.ContainingType) + "+" + type.MetadataName
            : (type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." : "") + type.MetadataName;

    private static bool IsOrIsInsideGeneric(INamedTypeSymbol type)
    {
        for (var t = type; t != null; t = t.ContainingType)
            if (t.IsGenericType) return true;
        return false;
    }

    /// <summary>Values that describe how the app looks or is configured, as opposed to what the user did with it.</summary>
    private static bool IsConfigurationLike(ITypeSymbol type)
    {
        if (type.IsValueType || type.SpecialType == SpecialType.System_String) return true;
        var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        return ns.StartsWith("Avalonia.Media", StringComparison.Ordinal)
            || ns.StartsWith("System.Windows.Media", StringComparison.Ordinal);
    }

    private static List<ConstructorDeclarationSyntax> ExplicitInstanceConstructors(INamedTypeSymbol type)
        => type.InstanceConstructors
            .Where(c => !c.IsImplicitlyDeclared)
            .Select(c => c.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax())
            .OfType<ConstructorDeclarationSyntax>()
            .ToList();

    // ── constructor replay ────────────────────────────────────────────────────────────────────

    private static bool TryGetAddedStatements(ConstructorDeclarationSyntax oldCtor, ConstructorDeclarationSyntax newCtor, out List<StatementSyntax> added)
    {
        added = [];
        if (!SyntaxFactory.AreEquivalent(oldCtor.Initializer, newCtor.Initializer) ||
            !SyntaxFactory.AreEquivalent(oldCtor.ParameterList, newCtor.ParameterList))
            return false;

        var oldStatements = Statements(oldCtor);
        int matched = 0;
        foreach (var statement in Statements(newCtor))
        {
            if (matched < oldStatements.Count && SameSetUp(oldStatements[matched], statement)) matched++;
            else added.Add(statement);
        }
        return matched == oldStatements.Count;

        static List<StatementSyntax> Statements(ConstructorDeclarationSyntax ctor)
            => ctor.Body != null ? ctor.Body.Statements.ToList()
             : ctor.ExpressionBody != null ? [SyntaxFactory.ExpressionStatement(ctor.ExpressionBody.Expression)]
             : [];
    }

    private static bool InvokesAny(StatementSyntax statement, HashSet<string> methodNames)
        => statement.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => i.Expression switch
        {
            IdentifierNameSyntax id => methodNames.Contains(id.Identifier.ValueText),
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } ma => methodNames.Contains(ma.Name.Identifier.ValueText),
            _ => false,
        });

    // ── what runs while the UI is built ───────────────────────────────────────────────────────

    /// <summary>
    /// Equivalent as far as building the UI is concerned: bodies of lambdas that are only stored
    /// (<see cref="IsDeferred"/>) do not run then, and Roslyn patches them in place, so the
    /// controls and delegates that already exist pick the new code up without being rebuilt.
    /// </summary>
    private static bool BuildTimeEquivalent(SyntaxNode oldNode, SyntaxNode newNode)
        => SyntaxFactory.AreEquivalent(DeferredLambdaEraser.Instance.Visit(oldNode), DeferredLambdaEraser.Instance.Visit(newNode));

    /// <summary>
    /// The same statement as far as replaying a constructor is concerned. A lambda whose body
    /// changed ("Tick = () => …", "timer.Tick += …") is no reason to replay anything: Roslyn patches
    /// the lambda in place, so the delegate the constructor created back then already runs the new body.
    /// </summary>
    private static bool SameSetUp(SyntaxNode oldNode, SyntaxNode newNode)
        => SyntaxFactory.AreEquivalent(LambdaBodyEraser.Instance.Visit(oldNode), LambdaBodyEraser.Instance.Visit(newNode));

    private sealed class LambdaBodyEraser : CSharpSyntaxRewriter
    {
        public static readonly LambdaBodyEraser Instance = new();

        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => Placeholder;
        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => Placeholder;
        public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => Placeholder;

        private static ExpressionSyntax Placeholder => SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression);
    }

    private sealed class DeferredLambdaEraser : CSharpSyntaxRewriter
    {
        public static readonly DeferredLambdaEraser Instance = new();

        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
            => IsDeferred(node) ? Placeholder : base.VisitParenthesizedLambdaExpression(node);

        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
            => IsDeferred(node) ? Placeholder : base.VisitSimpleLambdaExpression(node);

        public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
            => IsDeferred(node) ? Placeholder : base.VisitAnonymousMethodExpression(node);

        private static ExpressionSyntax Placeholder => SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression);
    }

    /// <summary>
    /// A lambda that is only stored — subscribed (<c>x.Click += (_, _) => …</c>), assigned
    /// (<c>Tick = () => …</c>, also in object initializers) or used to initialise a field or variable —
    /// runs later, not while the code around it runs. A lambda passed as an argument may run right
    /// away (<c>items.Select(i => new TextBlock …)</c>) and counts as running code.
    /// </summary>
    private static bool IsDeferred(AnonymousFunctionExpressionSyntax lambda)
    {
        SyntaxNode node = lambda;
        while (node.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax)
            node = node.Parent;

        return node.Parent switch
        {
            AssignmentExpressionSyntax assignment => assignment.Right == node,
            EqualsValueClauseSyntax => true,
            _ => false,
        };
    }

    private static Dictionary<string, (ISymbol Symbol, SyntaxNode Node)> MemberIndex(Compilation compilation, Func<SyntaxTree, SemanticModel> model)
    {
        var index = new Dictionary<string, (ISymbol, SyntaxNode)>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                SyntaxNode? declaration = node switch
                {
                    BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax => node,
                    VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax } => node,
                    _ => null,
                };
                if (declaration == null) continue;
                if (model(tree).GetDeclaredSymbol(declaration) is not { ContainingType: not null } symbol) continue;
                if (symbol.Name.StartsWith(HelperPrefix, StringComparison.Ordinal)) continue;
                index[MetadataName(symbol.ContainingType) + "::" + symbol.ToDisplayString(s_memberKey)] = (symbol, declaration);
            }
        }
        return index;
    }

    private static readonly SymbolDisplayFormat s_memberKey = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeExplicitInterface | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static HashSet<ISymbol> BuildTimeReachable(IEnumerable<ISymbol> start, Compilation compilation, Func<SyntaxTree, SemanticModel> model)
    {
        var reached = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var pending = new Queue<ISymbol>(start);

        void Enqueue(ISymbol? symbol)
        {
            symbol = Normalize(symbol);
            if (symbol == null || !SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, compilation.Assembly)) return;
            if (reached.Add(symbol)) pending.Enqueue(symbol);
        }

        foreach (var s in pending.ToList()) reached.Add(s);

        while (pending.Count > 0)
        {
            var symbol = pending.Dequeue();

            // Constructing an object runs the initializers of its type.
            if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } ctor)
            {
                foreach (var member in ctor.ContainingType.GetMembers())
                    if (member is IFieldSymbol or IPropertySymbol && member.IsStatic == ctor.IsStatic) Enqueue(member);
            }

            foreach (var reference in symbol.DeclaringSyntaxReferences)
            {
                var root = reference.GetSyntax();
                var semantics = model(root.SyntaxTree);
                foreach (var node in root.DescendantNodes(descendIntoChildren: n => n is not AnonymousFunctionExpressionSyntax lambda || !IsDeferred(lambda)))
                {
                    if (node is not (SimpleNameSyntax or BaseObjectCreationExpressionSyntax or ConstructorInitializerSyntax)) continue;
                    if (node.Parent is AssignmentExpressionSyntax { Right: var right } subscription && right == node &&
                        (subscription.IsKind(SyntaxKind.AddAssignmentExpression) || subscription.IsKind(SyntaxKind.SubtractAssignmentExpression)) &&
                        semantics.GetSymbolInfo(node).Symbol is IMethodSymbol)
                        continue; // "x.Click += OnClick": wired now, runs later

                    var info = semantics.GetSymbolInfo(node);
                    Enqueue(info.Symbol ?? info.CandidateSymbols.FirstOrDefault());
                }
            }
        }
        return reached;

        static ISymbol? Normalize(ISymbol? symbol) => symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction } => null,
            IMethodSymbol { AssociatedSymbol: { } owner } => owner.OriginalDefinition,
            IMethodSymbol method => (method.ReducedFrom ?? method).OriginalDefinition,
            IPropertySymbol or IFieldSymbol or IEventSymbol => symbol.OriginalDefinition,
            _ => null,
        };
    }
}
