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
/// A private copy of the build the plugin window loaded — what a <see cref="HotReloadSession"/>
/// computes its deltas against. Taken synchronously, right after the load: the hosts compile
/// every version to the same path, so the next compile overwrites the original.
/// </summary>
public sealed class HotReloadSnapshot
{
    internal HotReloadSnapshot(HotReloadBaseline baseline, string directory, string assemblyPath)
    {
        Baseline = baseline; Directory = directory; AssemblyPath = assemblyPath;
    }

    public HotReloadBaseline Baseline { get; }
    internal string Directory { get; }
    internal string AssemblyPath { get; }
}

/// <summary>
/// The host's memory of one running generated app: the code it was loaded with and every
/// generation patched into it since. Given the next version of the source it produces what the
/// plugin window needs to turn the running app into that version without restarting it —
/// or says why that is not possible, in which case the host reloads the app as before.
///
/// <para>Lifetime: create after a full load (<see cref="TakeSnapshot"/> + <see cref="StartAsync(HotReloadSnapshot,string,HotReloadOptions?,CancellationToken)"/>),
/// ask for an update on every code change, dispose on the next full load / unload / child exit.
/// A session whose update the child failed to apply is out of sync with the running code and
/// must be disposed too.</para>
/// </summary>
public sealed class HotReloadSession : IDisposable
{
    private static readonly Lazy<MefHostServices> s_host = new(() => MefHostServices.Create(MefHostServices.DefaultAssemblies));
    private static readonly Encoding s_utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly AdhocWorkspace _workspace;
    private readonly RoslynHotReloadService _service;
    private readonly ProjectId _projectId;
    private readonly Dictionary<string, DocumentId> _documents;
    private readonly Dictionary<string, MetadataReference> _references;
    private readonly HotReloadOptions _options;
    private readonly string _snapshotDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Solution _committed;
    private IReadOnlyList<HotReloadSource> _lastSources;
    private IReadOnlyList<string> _lastReferencePaths;
    private int _generation;
    private bool _disposed;

    private HotReloadSession(AdhocWorkspace workspace, RoslynHotReloadService service, ProjectId projectId,
        Dictionary<string, DocumentId> documents, Dictionary<string, MetadataReference> references, Solution solution,
        HotReloadBaseline baseline, IReadOnlyList<string> referencePaths, HotReloadOptions options, string snapshotDirectory)
    {
        _workspace = workspace; _service = service; _projectId = projectId; _documents = documents; _references = references;
        _committed = solution; _lastSources = baseline.Sources; _lastReferencePaths = referencePaths;
        _options = options; _snapshotDirectory = snapshotDirectory;
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

    /// <summary>
    /// Pays the one-time costs (MEF composition of Roslyn's workspace services, locating the hot
    /// reload engine) on a background thread, so the first app a host loads is not delayed by them.
    /// </summary>
    public static Task PrewarmAsync() => Task.Run(() =>
    {
        try
        {
            _ = s_host.Value;
            RoslynHotReloadService.EnsureAvailable();
        }
        catch { /* reported properly by the first real session */ }
    });

    /// <summary>
    /// Copies the build the plugin window just loaded. Synchronous on purpose: call it before
    /// anything can compile over <see cref="HotReloadBaseline.AssemblyPath"/> again.
    /// </summary>
    public static HotReloadSnapshot TakeSnapshot(HotReloadBaseline baseline)
    {
        if (!File.Exists(baseline.AssemblyPath)) throw new FileNotFoundException("Baseline assembly not found.", baseline.AssemblyPath);

        // Roslyn reads the baseline lazily — the assembly and the PDB embedded in it — and keeps
        // it open, so it gets its own copy that nobody overwrites and that never blocks a compile.
        var directory = Path.Combine(Path.GetTempPath(), "neo_hotreload", Guid.NewGuid().ToString("N")[..12]);
        System.IO.Directory.CreateDirectory(directory);
        var assemblyPath = Path.Combine(directory, baseline.AssemblyName + ".dll");
        File.Copy(baseline.AssemblyPath, assemblyPath, overwrite: true);
        if (!HotReloadBaseline.HasEmbeddedPdb(assemblyPath))
        {
            TryDeleteDirectory(directory);
            throw new InvalidOperationException("The baseline assembly has no embedded PDB; it was not built for hot reload.");
        }
        return new HotReloadSnapshot(baseline, directory, assemblyPath);
    }

    /// <param name="runtimeCapabilities">What the plugin window's runtime can patch — it reports them in its Hello message.</param>
    public static Task<HotReloadSession> StartAsync(HotReloadBaseline baseline, string runtimeCapabilities,
        HotReloadOptions? options = null, CancellationToken cancellationToken = default)
        => StartAsync(TakeSnapshot(baseline), runtimeCapabilities, options, cancellationToken);

    /// <param name="runtimeCapabilities">What the plugin window's runtime can patch — it reports them in its Hello message.</param>
    public static async Task<HotReloadSession> StartAsync(HotReloadSnapshot snapshot, string runtimeCapabilities,
        HotReloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        var baseline = snapshot.Baseline;
        var workspace = new AdhocWorkspace(s_host.Value);
        try
        {
            var referencePaths = DistinctPaths(baseline.ReferencePaths);
            var references = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in referencePaths)
                references[path] = MetadataReference.CreateFromFile(path);

            var projectId = ProjectId.CreateNewId(baseline.AssemblyName);
            var project = ProjectInfo.Create(projectId, VersionStamp.Create(), baseline.AssemblyName, baseline.AssemblyName, LanguageNames.CSharp,
                    filePath: Path.Combine(snapshot.Directory, baseline.AssemblyName + ".csproj"),
                    outputFilePath: snapshot.AssemblyPath,
                    compilationOptions: CompilationOptions,
                    parseOptions: ParseOptions,
                    metadataReferences: referencePaths.Select(p => references[p]))
                .WithCompilationOutputInfo(default(CompilationOutputInfo).WithAssemblyPath(snapshot.AssemblyPath));

            var solution = workspace.CurrentSolution.AddProject(project);
            var documents = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in baseline.Sources)
                solution = AddDocument(solution, projectId, source, documents);

            var capabilities = runtimeCapabilities.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToImmutableArray();
            var service = new RoslynHotReloadService(workspace.Services, capabilities);
            await service.StartSessionAsync(solution, cancellationToken).ConfigureAwait(false);

            return new HotReloadSession(workspace, service, projectId, documents, references, solution,
                baseline, referencePaths, options ?? new HotReloadOptions(), snapshot.Directory);
        }
        catch
        {
            workspace.Dispose();
            TryDeleteDirectory(snapshot.Directory);
            throw;
        }
    }

    /// <summary>Convenience for callers that only change the text of the existing files, in order.</summary>
    public Task<HotReloadUpdate> CreateUpdateAsync(IReadOnlyList<string> newSourceTexts, CancellationToken cancellationToken = default)
    {
        if (newSourceTexts.Count != _lastSources.Count)
            return Task.FromResult(HotReloadUpdate.NotPossible("the number of source files changed"));
        var sources = _lastSources.Select((s, i) => new HotReloadSource(s.Path, newSourceTexts[i])).ToList();
        return CreateUpdateAsync(sources, _lastReferencePaths, cancellationToken);
    }

    /// <summary>
    /// Turns the next version of the app into a hot reload request. When this returns
    /// <see cref="HotReloadStatus.Ready"/>, the update is committed on this side: the plugin window
    /// has to apply it, and if it cannot, this session is no longer usable.
    /// </summary>
    /// <param name="sources">All source files of the new version (new files are fine, removed ones are not).</param>
    /// <param name="referencePaths">
    /// What the new version is compiled against. Added references (a new NuGet package) are
    /// patched in like any other change — the host has to hand the new assemblies to the plugin
    /// window before the update (<see cref="HotReloadUpdate.AddedReferences"/>). A reference whose
    /// version changed cannot be patched into running code.
    /// </param>
    public async Task<HotReloadUpdate> CreateUpdateAsync(IReadOnlyList<HotReloadSource> sources,
        IReadOnlyList<string> referencePaths, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var removed = _documents.Keys.Except(sources.Select(s => s.Path), StringComparer.OrdinalIgnoreCase).ToList();
            if (removed.Count > 0)
                return HotReloadUpdate.NotPossible("a source file was removed");

            referencePaths = DistinctPaths(referencePaths);
            bool sameReferences = new HashSet<string>(referencePaths, StringComparer.OrdinalIgnoreCase).SetEquals(_lastReferencePaths);
            if (SameSources(sources, _lastSources) && sameReferences)
                return new HotReloadUpdate(HotReloadStatus.NoChanges, null, "the source is unchanged");

            // The new version: file texts, new files, the reference set.
            var pristine = _committed;
            var addedDocuments = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                pristine = _documents.TryGetValue(source.Path, out var id)
                    ? pristine.WithDocumentText(id, ToSourceText(source.Text))
                    : AddDocument(pristine, _projectId, source, addedDocuments);
            }

            var addedReferences = referencePaths.Except(_lastReferencePaths, StringComparer.OrdinalIgnoreCase).ToList();
            var newReferences = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
            if (!sameReferences)
            {
                pristine = pristine.WithProjectMetadataReferences(_projectId, referencePaths.Select(path =>
                    _references.TryGetValue(path, out var existing) ? existing : newReferences[path] = MetadataReference.CreateFromFile(path)));
            }

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
            _lastSources = sources.ToList();
            _lastReferencePaths = referencePaths.ToList();
            foreach (var (path, id) in addedDocuments) _documents[path] = id;
            foreach (var (path, reference) in newReferences) _references[path] = reference;
            _generation++;

            var request = new HotReloadRequest(
                deltas.Select(d => new HotReloadDeltaMessage(
                    Convert.ToBase64String(d.MetadataDelta), Convert.ToBase64String(d.IlDelta), Convert.ToBase64String(d.PdbDelta))).ToList(),
                plan.Steps,
                plan.RebuildUi,
                plan.RebuildMethodName);
            return new HotReloadUpdate(HotReloadStatus.Ready, request, null) { AddedReferences = addedReferences };
        }
        finally
        {
            _gate.Release();
        }
    }

    // Hosts may list a reference twice (the same agent DLL from two settings). The compiler does
    // not mind; Roslyn's workspace rejects the project ("duplicate items") — and a session that
    // cannot start means every change reloads the app.
    private static List<string> DistinctPaths(IEnumerable<string> paths)
        => paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool SameSources(IReadOnlyList<HotReloadSource> a, IReadOnlyList<HotReloadSource> b)
        => a.Count == b.Count && a.Zip(b).All(p =>
            string.Equals(p.First.Path, p.Second.Path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.First.Text, p.Second.Text, StringComparison.Ordinal));

    private static Solution AddDocument(Solution solution, ProjectId projectId, HotReloadSource source, Dictionary<string, DocumentId> ids)
    {
        var id = DocumentId.CreateNewId(projectId, source.Path);
        ids[source.Path] = id;
        return solution.AddDocument(DocumentInfo.Create(id, Path.GetFileName(source.Path), filePath: source.Path,
            loader: TextLoader.From(TextAndVersion.Create(ToSourceText(source.Text), VersionStamp.Create(), source.Path))));
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

    internal static void TryDeleteDirectory(string path)
    {
        try { System.IO.Directory.Delete(path, recursive: true); } catch { /* Roslyn may still hold the baseline; temp folder */ }
    }
}
