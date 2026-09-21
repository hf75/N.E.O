using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;

namespace Neo.HotReload;

/// <summary>One edit-and-continue generation as produced by Roslyn.</summary>
public sealed record HotReloadDelta(byte[] MetadataDelta, byte[] IlDelta, byte[] PdbDelta);

/// <summary>
/// Thin wrapper around Roslyn's hot reload analysis — <c>WatchHotReloadService</c>, the engine
/// dotnet-watch drives. It decides what can be patched into a running assembly (and says why
/// not: "rude edits"), maps lambdas and closures of the old code onto the new code so existing
/// delegates keep working, and emits the metadata/IL/PDB deltas.
///
/// <para>The type is internal to Microsoft.CodeAnalysis.Features and only visible to
/// dotnet-watch, so it is reached through reflection. Doing the analysis ourselves is not an
/// option: a hand-written differ cannot map lambdas, and every unmapped lambda that is still
/// subscribed somewhere (a timer tick, CollectionChanged) throws HotReloadException after the
/// update. The members used here are looked up once and checked up front, so an incompatible
/// Roslyn version fails at session start with a clear message instead of somewhere in the middle.</para>
///
/// <para>Two shapes of the API are understood: Roslyn 5.x (<c>GetUpdatesAsync</c> with an explicit
/// <c>CommitUpdate</c>) and Roslyn 4.14 (<c>EmitSolutionUpdateAsync</c>, which commits on its own).</para>
/// </summary>
internal sealed class RoslynHotReloadService
{
    private const string FeaturesAssembly = "Microsoft.CodeAnalysis.Features";
    private const string ServiceTypeName = "Microsoft.CodeAnalysis.ExternalAccess.Watch.Api.WatchHotReloadService";
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly Lazy<Api> s_api = new(Api.Load);

    private readonly object _service;

    public RoslynHotReloadService(HostWorkspaceServices services, ImmutableArray<string> capabilities)
        => _service = s_api.Value.Constructor.Invoke([services, capabilities]);

    /// <summary>Which Roslyn the deltas come from — for logs and bug reports.</summary>
    public static string RoslynVersion => s_api.Value.Version;

    /// <summary>Locates the engine now (reflection over Roslyn's internals) instead of in the first update.</summary>
    public static void EnsureAvailable() => _ = s_api.Value;

    public Task StartSessionAsync(Solution solution, CancellationToken cancellationToken)
        => (Task)s_api.Value.StartSession.Invoke(_service, [solution, cancellationToken])!;

    /// <summary>
    /// Computes the deltas between the last committed solution and <paramref name="solution"/>.
    /// When deltas come back they are committed: <paramref name="solution"/> is Roslyn's new baseline.
    /// Errors in <c>Diagnostics</c> (compile errors, rude edits) mean nothing was committed.
    /// </summary>
    public async Task<(IReadOnlyList<HotReloadDelta> Deltas, IReadOnlyList<Diagnostic> Diagnostics)> EmitSolutionUpdateAsync(
        Solution solution, ProjectId runningProject, CancellationToken cancellationToken)
    {
        var api = s_api.Value;
        return api.GetUpdates != null
            ? await GetUpdatesAsync(api, solution, runningProject, cancellationToken).ConfigureAwait(false)
            : await EmitLegacyAsync(api, solution, cancellationToken).ConfigureAwait(false);
    }

    // Roslyn 5.x
    private async Task<(IReadOnlyList<HotReloadDelta>, IReadOnlyList<Diagnostic>)> GetUpdatesAsync(
        Api api, Solution solution, ProjectId runningProject, CancellationToken cancellationToken)
    {
        // ImmutableDictionary<ProjectId, RunningProjectInfo>: "this project is running, do not restart it for me"
        var dictionaryType = typeof(ImmutableDictionary<,>).MakeGenericType(typeof(ProjectId), api.RunningProjectInfo!);
        var running = dictionaryType.GetField("Empty", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        running = dictionaryType.GetMethod("Add", [typeof(ProjectId), api.RunningProjectInfo!])!
            .Invoke(running, [runningProject, Activator.CreateInstance(api.RunningProjectInfo!)])!;

        var task = (Task)api.GetUpdates!.Invoke(_service, [solution, running, cancellationToken])!;
        await task.ConfigureAwait(false);
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;

        var diagnostics = new List<Diagnostic>((ImmutableArray<Diagnostic>)Read(result, "CompilationDiagnostics"));
        foreach (var entry in (IEnumerable)Read(result, "RudeEdits"))
            diagnostics.AddRange((ImmutableArray<Diagnostic>)entry.GetType().GetField("Item2")!.GetValue(entry)!);

        var deltas = ReadDeltas((IEnumerable)Read(result, "ProjectUpdates"));
        var status = Read(result, "Status").ToString();

        if (status == "ReadyToApply" && deltas.Count > 0)
        {
            api.CommitUpdate!.Invoke(_service, null);
            return (deltas, diagnostics);
        }

        if (status == "Blocked")
        {
            try { api.DiscardUpdate?.Invoke(_service, null); } catch { /* nothing pending */ }
            if (!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                // Rude edits may be reported as warnings ("requires restart"); for us they block just the same.
                diagnostics = diagnostics.Select(d => d.Severity == DiagnosticSeverity.Error ? d
                    : Diagnostic.Create(d.Id, "EditAndContinue", d.GetMessage(), DiagnosticSeverity.Error, DiagnosticSeverity.Error,
                        isEnabledByDefault: true, warningLevel: 0, location: d.Location)).ToList();
                if (diagnostics.Count == 0)
                    diagnostics.Add(Diagnostic.Create("NEOHR001", "EditAndContinue", "Roslyn blocked the update without naming a reason.",
                        DiagnosticSeverity.Error, DiagnosticSeverity.Error, isEnabledByDefault: true, warningLevel: 0));
            }
            return ([], diagnostics);
        }

        return ([], diagnostics); // NoChangesToApply
    }

    // Roslyn 4.14
    private async Task<(IReadOnlyList<HotReloadDelta>, IReadOnlyList<Diagnostic>)> EmitLegacyAsync(
        Api api, Solution solution, CancellationToken cancellationToken)
    {
        var task = (Task)api.EmitSolutionUpdate!.Invoke(_service, [solution, cancellationToken])!;
        await task.ConfigureAwait(false);

        // Task<(ImmutableArray<Update> updates, ImmutableArray<Diagnostic> diagnostics)>
        var tuple = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var updates = (IEnumerable)tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
        var diagnostics = (ImmutableArray<Diagnostic>)tuple.GetType().GetField("Item2")!.GetValue(tuple)!;
        return (ReadDeltas(updates), diagnostics);
    }

    public void EndSession()
    {
        try { s_api.Value.EndSession.Invoke(_service, null); }
        catch { /* session already gone */ }
    }

    private static List<HotReloadDelta> ReadDeltas(IEnumerable updates)
    {
        var deltas = new List<HotReloadDelta>();
        foreach (var update in updates)
        {
            deltas.Add(new HotReloadDelta(
                ((ImmutableArray<byte>)Read(update, "MetadataDelta")).ToArray(),
                ((ImmutableArray<byte>)Read(update, "ILDelta")).ToArray(),
                ((ImmutableArray<byte>)Read(update, "PdbDelta")).ToArray()));
        }
        return deltas;
    }

    private static object Read(object target, string member)
    {
        var type = target.GetType();
        return type.GetField(member, All)?.GetValue(target) ?? type.GetProperty(member, All)?.GetValue(target)
            ?? throw new MissingMemberException(type.FullName, member);
    }

    private sealed record Api(
        string Version,
        ConstructorInfo Constructor,
        MethodInfo StartSession,
        MethodInfo EndSession,
        MethodInfo? GetUpdates, Type? RunningProjectInfo, MethodInfo? CommitUpdate, MethodInfo? DiscardUpdate,
        MethodInfo? EmitSolutionUpdate)
    {
        public static Api Load()
        {
            try
            {
                var assembly = Assembly.Load(FeaturesAssembly);
                var type = assembly.GetType(ServiceTypeName, throwOnError: true)!;

                var runningProjectInfo = type.GetNestedType("RunningProjectInfo", All);
                var getUpdates = runningProjectInfo == null ? null : type.GetMethod("GetUpdatesAsync", All,
                    [typeof(Solution), typeof(ImmutableDictionary<,>).MakeGenericType(typeof(ProjectId), runningProjectInfo), typeof(CancellationToken)]);
                var commit = type.GetMethod("CommitUpdate", All, Type.EmptyTypes);
                var emit = type.GetMethod("EmitSolutionUpdateAsync", All, [typeof(Solution), typeof(CancellationToken)]);

                if ((getUpdates == null || commit == null) && emit == null)
                    throw new MissingMethodException(ServiceTypeName, "GetUpdatesAsync/CommitUpdate or EmitSolutionUpdateAsync");

                bool modern = getUpdates != null && commit != null;
                return new Api(
                    assembly.GetName().Version?.ToString() ?? "?",
                    type.GetConstructor(All, [typeof(HostWorkspaceServices), typeof(ImmutableArray<string>)])
                        ?? throw new MissingMethodException(ServiceTypeName, ".ctor(HostWorkspaceServices, ImmutableArray<string>)"),
                    type.GetMethod("StartSessionAsync", All, [typeof(Solution), typeof(CancellationToken)])
                        ?? throw new MissingMethodException(ServiceTypeName, "StartSessionAsync"),
                    type.GetMethod("EndSession", All, Type.EmptyTypes)
                        ?? throw new MissingMethodException(ServiceTypeName, "EndSession"),
                    modern ? getUpdates : null, modern ? runningProjectInfo : null, modern ? commit : null,
                    type.GetMethod("DiscardUpdate", All, Type.EmptyTypes),
                    modern ? null : emit);
            }
            catch (Exception ex)
            {
                throw new NotSupportedException(
                    "Roslyn's hot reload service is not available in the shape N.E.O. expects. " +
                    "Neo.HotReload pins Microsoft.CodeAnalysis.CSharp.Features for this reason — " +
                    "if the package was updated, adapt RoslynHotReloadService. Cause: " + ex.Message, ex);
            }
        }
    }
}
