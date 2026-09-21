using Neo.Agents;
using Neo.IPC;

namespace Neo.HotReload;

/// <summary>One source file of a generated app. The path is what ends up in the PDB.</summary>
public sealed record HotReloadSource(string Path, string Text);

/// <summary>
/// What the compile step has to hand over so that later code changes can be turned into deltas:
/// the debug-compiled assembly with its portable PDB (the exact bytes the plugin window loaded),
/// the sources they were compiled from, and the references used.
/// </summary>
public sealed record HotReloadBaseline(
    string AssemblyName,
    string AssemblyPath,
    string PdbPath,
    IReadOnlyList<HotReloadSource> Sources,
    IReadOnlyList<string> ReferencePaths)
{
    /// <summary>
    /// Reads what the compile agent left next to <paramref name="dllPath"/>
    /// (<see cref="HotReloadBaselineFile"/>). Null when the DLL was not built for hot reload.
    /// </summary>
    public static HotReloadBaseline? TryLoad(string dllPath)
    {
        var file = HotReloadBaselineFile.TryRead(dllPath);
        if (file == null || file.SourcePaths.Count != file.SourceTexts.Count) return null;
        if (!File.Exists(dllPath) || !File.Exists(file.PdbPath)) return null;

        return new HotReloadBaseline(file.AssemblyName, dllPath, file.PdbPath,
            file.SourcePaths.Zip(file.SourceTexts, (path, text) => new HotReloadSource(path, text)).ToList(),
            file.ReferencePaths);
    }
}

public enum HotReloadStatus
{
    /// <summary>Deltas are ready; send <see cref="HotReloadUpdate.Request"/> to the plugin window.</summary>
    Ready,

    /// <summary>The new source compiles to the same program (whitespace, comments). Nothing to do.</summary>
    NoChanges,

    /// <summary>The change cannot be patched into the running app; do a full reload. See <see cref="HotReloadUpdate.Reason"/>.</summary>
    NotPossible,
}

/// <param name="Request">The IPC payload for the plugin window (only for <see cref="HotReloadStatus.Ready"/>).</param>
/// <param name="Reason">Human-readable, goes into the host's log and into the MCP tool result.</param>
public sealed record HotReloadUpdate(HotReloadStatus Status, HotReloadRequest? Request, string? Reason)
{
    public static HotReloadUpdate NotPossible(string reason) => new(HotReloadStatus.NotPossible, null, reason);
}

public sealed record HotReloadOptions
{
    /// <summary>Class name of the control the plugin window instantiates.</summary>
    public string RootTypeName { get; init; } = "DynamicUserControl";
}
