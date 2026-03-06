using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Neo.AssemblyForge;

public enum AssemblyForgeUiFramework
{
    Wpf,
    Avalonia,
}

public enum AssemblyForgeArtifactKind
{
    UserControlDll,
    Executable,
}

public enum AssemblyForgeStatus
{
    Success,
    ChatOnly,
    Rejected,
    Failed,
    PowerShellReady,
    ConsoleAppReady,
    PlanReady,
}

public enum AssemblyForgeReviewAction
{
    Accept,
    Reject,
    Regenerate,
    Cancel,
}

public sealed record AssemblyForgeWorkspace
{
    public required string NuGetPackageDirectory { get; init; }
    public required string OutputDllPath { get; init; }
    public string? OutputExeDirectory { get; init; }
    public required IReadOnlyList<string> ReferenceAssemblyDirectories { get; init; }

    public string TargetFramework { get; init; } = $"net{Environment.Version.Major}.0-windows";
    public string AssemblyName { get; init; } = "DynamicUserControl";
    public IReadOnlyList<string> AdditionalReferenceDllPaths { get; init; } = Array.Empty<string>();
}

public sealed record AssemblyForgePipelineOptions
{
    public int MaxAttempts { get; init; } = 5;

    public bool ClearNuGetCacheOnCs0433 { get; init; } = true;
    public bool ClearNuGetStateOnCacheClear { get; init; } = true;

    public float Temperature { get; init; } = 0.1f;
    public float TopP { get; init; } = 0.9f;
}

public sealed record AssemblyForgeSessionOptions
{
    public AssemblyForgeArtifactKind ArtifactKind { get; init; } = AssemblyForgeArtifactKind.UserControlDll;

    public AssemblyForgeUiFramework UiFramework { get; init; } = AssemblyForgeUiFramework.Wpf;
    public bool UseReactUi { get; init; }
    public bool UsePython { get; init; }

    public string UserControlClassName { get; init; } = "DynamicUserControl";
    public string ExecutableMainTypeName { get; init; } = "Neo.Dynamic.DynamicProgram";
    public string ExecutableCompileType { get; init; } = "WINDOWS";
    public string MainFilePath { get; init; } = "./currentcode.cs";

    public string? InitialCode { get; init; }
    public string InitialHistoryPrefix { get; init; } = "Code:\n\n";

    public IReadOnlyDictionary<string, string> AdditionalSourceFiles { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? SystemMessageOverride { get; init; }
}

public delegate Task<AssemblyForgeReviewDecision> AssemblyForgeReviewCallback(
    AssemblyForgeReviewContext context,
    CancellationToken cancellationToken);

public sealed record AssemblyForgeReviewContext(
    string UserPrompt,
    string Patch,
    string ResultingCode,
    IReadOnlyList<string> NuGetPackages,
    string Explanation,
    int Attempt);

public sealed record AssemblyForgeReviewDecision(
    AssemblyForgeReviewAction Action,
    string? RegenerationInstruction = null);
