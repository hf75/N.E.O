using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Neo.Agents;

namespace Neo.AssemblyForge.Services;

public interface INuGetPackageService
{
    Task<NuGetResult> LoadPackagesAsync(
        IReadOnlyDictionary<string, string> packages,
        IEnumerable<string> existingDlls,
        CancellationToken cancellationToken);
}

public sealed record NuGetResult(
    IReadOnlyList<string> DllPaths,
    IReadOnlyDictionary<string, string> PackageVersions);

public sealed class NuGetPackageService : INuGetPackageService
{
    private readonly string _nuGetPackageDirectory;
    private readonly string _targetFramework;
    private readonly IReadOnlyList<string> _referenceAssemblyDirectories;

    public NuGetPackageService(
        string nuGetPackageDirectory,
        string targetFramework,
        IReadOnlyList<string> referenceAssemblyDirectories)
    {
        if (string.IsNullOrWhiteSpace(nuGetPackageDirectory))
            throw new ArgumentException("Value cannot be null/empty.", nameof(nuGetPackageDirectory));
        if (string.IsNullOrWhiteSpace(targetFramework))
            throw new ArgumentException("Value cannot be null/empty.", nameof(targetFramework));

        _nuGetPackageDirectory = nuGetPackageDirectory;
        _targetFramework = targetFramework;
        _referenceAssemblyDirectories = referenceAssemblyDirectories ?? Array.Empty<string>();

        Directory.CreateDirectory(_nuGetPackageDirectory);
    }

    public async Task<NuGetResult> LoadPackagesAsync(
        IReadOnlyDictionary<string, string> packages,
        IEnumerable<string> existingDlls,
        CancellationToken cancellationToken)
    {
        existingDlls ??= Enumerable.Empty<string>();

        if (packages is null || packages.Count == 0)
        {
            var existingList = existingDlls.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new NuGetResult(existingList, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        var loader = new NuGetPackageLoaderAgent();
        loader.SetInput("PackageNames", packages);
        loader.SetInput("OutputDirectory", _nuGetPackageDirectory);
        loader.SetInput("TargetFramework", _targetFramework);

        await loader.ExecuteAsync(cancellationToken);

        var newDllPaths = loader.GetOutput<List<string>>("DllPaths") ?? new List<string>();
        var newPackageVersions = loader.GetOutput<Dictionary<string, string>>("PackageVersions")
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var combined = existingDlls
            .Concat(newDllPaths)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        RemoveReferenceDllConflicts(combined);

        return new NuGetResult(combined, newPackageVersions);
    }

    private void RemoveReferenceDllConflicts(List<string> dllPaths)
    {
        if (dllPaths.Count == 0)
            return;

        var refDllNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in _referenceAssemblyDirectories)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                continue;

            foreach (var file in Directory.GetFiles(dir, "*.dll"))
                refDllNames.Add(Path.GetFileName(file));
        }

        dllPaths.RemoveAll(path => refDllNames.Contains(Path.GetFileName(path)));
    }
}
