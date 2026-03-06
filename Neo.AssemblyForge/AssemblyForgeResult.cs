using System;
using System.Collections.Generic;
using System.Linq;

namespace Neo.AssemblyForge;

public sealed class AssemblyForgeSessionState
{
    public string History { get; set; } = string.Empty;
    public string CurrentCode { get; set; } = string.Empty;

    public List<string> NuGetDlls { get; set; } = new();
    public Dictionary<string, string> PackageVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string LastErrorMessage { get; set; } = string.Empty;

    public AssemblyForgeSessionState Clone()
    {
        return new AssemblyForgeSessionState
        {
            History = History,
            CurrentCode = CurrentCode,
            NuGetDlls = NuGetDlls.ToList(),
            PackageVersions = new Dictionary<string, string>(PackageVersions, StringComparer.OrdinalIgnoreCase),
            LastErrorMessage = LastErrorMessage,
        };
    }
}

public sealed record AssemblyForgeResult
{
    public required AssemblyForgeStatus Status { get; init; }
    public AssemblyForgeArtifactKind ArtifactKind { get; init; } = AssemblyForgeArtifactKind.UserControlDll;

    public string? OutputDllPath { get; init; }
    public string? OutputExePath { get; init; }
    public string? OutputExeDirectory { get; init; }
    public IReadOnlyList<string> NuGetDllPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AdditionalDllPaths { get; init; } = Array.Empty<string>();

    public StructuredResponse? StructuredResponse { get; init; }

    public string ErrorMessage { get; init; } = string.Empty;
    public int AttemptsUsed { get; init; }
}
