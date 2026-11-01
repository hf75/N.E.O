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
    private readonly IReadOnlyList<string>? _hostDlls;

    /// <param name="hostDlls">
    /// What the host ships, for a caller that does not list it among the existing DLLs (the MCP
    /// server's plugin window). Never downloaded, also not as a dependency of an add-on.
    /// </param>
    public NuGetPackageService(
        string nuGetPackageDirectory,
        string targetFramework,
        IReadOnlyList<string> referenceAssemblyDirectories,
        IReadOnlyList<string>? hostDlls = null)
    {
        if (string.IsNullOrWhiteSpace(nuGetPackageDirectory))
            throw new ArgumentException("Value cannot be null/empty.", nameof(nuGetPackageDirectory));
        if (string.IsNullOrWhiteSpace(targetFramework))
            throw new ArgumentException("Value cannot be null/empty.", nameof(targetFramework));

        _nuGetPackageDirectory = nuGetPackageDirectory;
        _targetFramework = targetFramework;
        _referenceAssemblyDirectories = referenceAssemblyDirectories ?? Array.Empty<string>();
        _hostDlls = hostDlls;

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

        // The host's own assemblies (its Avalonia) win over whatever version was asked for,
        // and nothing it ships is downloaded.
        var existingPaths = existingDlls.ToList();
        var hostIndex = HostProvidedAssemblies.IndexOutside(existingPaths, _nuGetPackageDirectory)
            .Concat(HostProvidedAssemblies.Index(_hostDlls))
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        existingDlls = existingPaths;

        var provided = HostProvidedAssemblies.ProvidedPackages(packages, hostIndex);
        var toDownload = HostProvidedAssemblies.WithoutProvidedPackages(packages, hostIndex);
        var newDllPaths = new List<string>();
        var newPackageVersions = new Dictionary<string, string>(provided, StringComparer.OrdinalIgnoreCase);

        if (toDownload.Count > 0)
        {
            var loader = new NuGetPackageLoaderAgent();
            loader.SetInput("PackageNames", toDownload);
            loader.SetInput("HostAssemblies", hostIndex);
            loader.SetInput("OutputDirectory", _nuGetPackageDirectory);
            loader.SetInput("TargetFramework", _targetFramework);

            await loader.ExecuteAsync(cancellationToken);

            newDllPaths = loader.GetOutput<List<string>>("DllPaths") ?? newDllPaths;
            foreach (var package in loader.GetOutput<Dictionary<string, string>>("PackageVersions") ?? new Dictionary<string, string>())
                newPackageVersions[package.Key] = package.Value;

            HostProvidedAssemblies.RemoveShadowing(newDllPaths, hostIndex);
        }

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
