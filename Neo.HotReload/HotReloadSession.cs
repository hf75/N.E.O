using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Neo.IPC;

namespace Neo.HotReload;

/// <summary>
/// The host's memory of one running generated app: the code it was loaded with and every
/// generation patched into it since. Given the next version of the source it produces what the
/// plugin window needs to turn the running app into that version without restarting it —
/// or says why that is not possible, in which case the host reloads the app as before.
///
/// <para>Lifetime: create after a full load (<see cref="StartAsync"/>), ask for an update on every
/// code change, dispose on the next full load / unload / child exit. A session whose update the
/// child failed to apply is out of sync with the running code and must be disposed too.</para>
/// </summary>
public sealed class HotReloadSession : IDisposable
{
    private static readonly Lazy<MefHostServices> s_host = new(() => MefHostServices.Create(MefHostServices.DefaultAssemblies));
    private static readonly Encoding s_utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly AdhocWorkspace _workspace;
    private readonly RoslynHotReloadService _service;
    private readonly ProjectId _projectId;
    private readonly IReadOnlyList<DocumentId> _documents;
    private readonly HotReloadOptions _options;
    private readonly string _snapshotDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Solution _committed;
    private IReadOnlyList<string> _lastSourceTexts;
    private int _generation;
    private bool _disposed;

    private HotReloadSession(AdhocWorkspace workspace, RoslynHotReloadService service, ProjectId projectId,
        IReadOnlyList<DocumentId> documents, Solution solution, IReadOnlyList<string> sourceTexts,
        HotReloadOptions options, string snapshotDirectory)
    {
        _workspace = workspace; _service = service; _projectId = projectId; _documents = documents;
        _committed = solution; _lastSourceTexts = sourceTexts; _options = options; _snapshotDirectory = snapshotDirectory;
    }

    /// <summary>Generations applied so far.</summary>
    public int Generation => _generation;

    /// <summary>The compilation settings a baseline has to be built with. The compile agents use these in hot reload mode.</summary>
    public static CSharpCompilationOptions CompilationOptions { get; } = new(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Debug,
        platform: Platform.AnyCpu,
        allowUnsafe: true);

    public static CSharpParseOptions ParseOptions { get; } = new();

    /// <param name="runtimeCapabilities">What the plugin window's runtime can patch — it reports them in its Hello message.</param>
    public static async Task<HotReloadSession> StartAsync(HotReloadBaseline baseline, string runtimeCapabilities,
        HotReloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(baseline.AssemblyPath)) throw new FileNotFoundException("Baseline assembly not found.", baseline.AssemblyPath);

        // Hosts compile every version to the same path. Roslyn reads the baseline lazily — the
        // assembly and the PDB embedded in it — and keeps it open, so it gets its own copy that
        // nobody overwrites and that never blocks the next compile.
        var snapshot = Path.Combine(Path.GetTempPath(), "neo_hotreload", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(snapshot);
        var assemblyPath = Path.Combine(snapshot, baseline.AssemblyName + ".dll");
        File.Copy(baseline.AssemblyPath, assemblyPath, overwrite: true);
        if (!HotReloadBaseline.HasEmbeddedPdb(assemblyPath))
        {
            TryDeleteDirectory(snapshot);
            throw new InvalidOperationException("The baseline assembly has no embedded PDB; it was not built for hot reload.");
        }

        var workspace = new AdhocWorkspace(s_host.Value);
        try
        {
            var projectId = ProjectId.CreateNewId(baseline.AssemblyName);
            var project = ProjectInfo.Create(projectId, VersionStamp.Create(), baseline.AssemblyName, baseline.AssemblyName, LanguageNames.CSharp,
                    filePath: Path.Combine(snapshot, baseline.AssemblyName + ".csproj"),
                    outputFilePath: assemblyPath,
                    compilationOptions: CompilationOptions,
                    parseOptions: ParseOptions,
                    metadataReferences: baseline.ReferencePaths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)))
                .WithCompilationOutputInfo(default(CompilationOutputInfo).WithAssemblyPath(assemblyPath));

            var solution = workspace.CurrentSolution.AddProject(project);
            var documents = new List<DocumentId>();
            foreach (var source in baseline.Sources)
            {
                var id = DocumentId.CreateNewId(projectId, source.Path);
                documents.Add(id);
                solution = solution.AddDocument(DocumentInfo.Create(id, Path.GetFileName(source.Path), filePath: source.Path,
                    loader: TextLoader.From(TextAndVersion.Create(ToSourceText(source.Text), VersionStamp.Create(), source.Path))));
            }

            var capabilities = runtimeCapabilities.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToImmutableArray();
            var service = new RoslynHotReloadService(workspace.Services, capabilities);
            await service.StartSessionAsync(solution, cancellationToken).ConfigureAwait(false);

            return new HotReloadSession(workspace, service, projectId, documents, solution,
                baseline.Sources.Select(s => s.Text).ToList(), options ?? new HotReloadOptions(), snapshot);
        }
        catch
        {
            workspace.Dispose();
            TryDeleteDirectory(snapshot);
            throw;
        }
    }

    /// <summary>
    /// Turns the next version of the source into a hot reload request. When this returns
    /// <see cref="HotReloadStatus.Ready"/>, the update is committed on this side: the plugin window
    /// has to apply it, and if it cannot, this session is no longer usable.
    /// </summary>
    public async Task<HotReloadUpdate> CreateUpdateAsync(IReadOnlyList<string> newSourceTexts, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (newSourceTexts.Count != _documents.Count)
                return HotReloadUpdate.NotPossible("the number of source files changed");
            if (newSourceTexts.SequenceEqual(_lastSourceTexts, StringComparer.Ordinal))
                return new HotReloadUpdate(HotReloadStatus.NoChanges, null, "the source is unchanged");

            var pristine = _committed;
            for (int i = 0; i < _documents.Count; i++)
                pristine = pristine.WithDocumentText(_documents[i], ToSourceText(newSourceTexts[i]));

            var oldCompilation = (await _committed.GetProject(_projectId)!.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
            var newCompilation = (await pristine.GetProject(_projectId)!.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
            if (FirstError(newCompilation) is { } compileError)
                return HotReloadUpdate.NotPossible("the new code does not compile: " + compileError);

            var plan = StateMigrationAnalyzer.Analyze(oldCompilation, newCompilation, _options.RootTypeName, _generation + 1);
            if (plan.NotPossibleReason != null)
                return HotReloadUpdate.NotPossible(plan.NotPossibleReason);

            var updated = await InjectAsync(pristine, plan, cancellationToken).ConfigureAwait(false);
            if (plan.Injections.Count > 0)
            {
                var injected = (await updated.GetProject(_projectId)!.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;
                if (FirstError(injected) is { } helperError)
                    return HotReloadUpdate.NotPossible("the state of the running app cannot be carried over (" + helperError + ")");
            }

            var (deltas, diagnostics) = await _service.EmitSolutionUpdateAsync(updated, _projectId, cancellationToken).ConfigureAwait(false);

            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
                return HotReloadUpdate.NotPossible(string.Join(" | ", errors.Select(Describe).Distinct()));
            if (deltas.Count == 0)
                return new HotReloadUpdate(HotReloadStatus.NoChanges, null, "the change does not affect the compiled code");

            _committed = updated;
            _lastSourceTexts = newSourceTexts.ToList();
            _generation++;

            var request = new HotReloadRequest(
                deltas.Select(d => new HotReloadDeltaMessage(
                    Convert.ToBase64String(d.MetadataDelta), Convert.ToBase64String(d.IlDelta), Convert.ToBase64String(d.PdbDelta))).ToList(),
                plan.Steps,
                plan.RebuildUi,
                plan.RebuildMethodName);
            return new HotReloadUpdate(HotReloadStatus.Ready, request, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Solution> InjectAsync(Solution solution, StateMigrationPlan plan, CancellationToken cancellationToken)
    {
        foreach (var byTree in plan.Injections.GroupBy(i => i.Type.SyntaxTree))
        {
            var documentId = solution.GetDocumentId(byTree.Key);
            if (documentId == null) continue;

            var membersByType = byTree.GroupBy(i => i.Type).ToDictionary(
                g => g.Key,
                g => g.Select(i => SyntaxFactory.ParseMemberDeclaration(i.MemberSource)!
                        .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)).ToArray());

            var root = await byTree.Key.GetRootAsync(cancellationToken).ConfigureAwait(false);
            var newRoot = root.ReplaceNodes(membersByType.Keys,
                (original, rewritten) => ((TypeDeclarationSyntax)rewritten).AddMembers(membersByType[(TypeDeclarationSyntax)original]));

            solution = solution.WithDocumentText(documentId, ToSourceText(newRoot.ToFullString()));
        }
        return solution;
    }

    private static SourceText ToSourceText(string text) => SourceText.From(text, s_utf8, SourceHashAlgorithm.Sha256);

    private static string? FirstError(Compilation compilation)
        => compilation.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error) is { } error ? Describe(error) : null;

    private static string Describe(Diagnostic diagnostic)
    {
        var line = diagnostic.Location.IsInSource ? $" (line {diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1})" : "";
        return $"{diagnostic.Id}: {diagnostic.GetMessage()}{line}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.EndSession();
        _workspace.Dispose();
        TryDeleteDirectory(_snapshotDirectory);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* Roslyn may still hold the baseline; temp folder */ }
    }
}
