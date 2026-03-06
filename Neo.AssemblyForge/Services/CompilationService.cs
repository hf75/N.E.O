using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Neo.Agents;
using Neo.AssemblyForge.Utils;

namespace Neo.AssemblyForge.Services;

public interface ICompilationService
{
    Task<string> CompileToDllAsync(
        IReadOnlyList<string> sourceFiles,
        string dllOutputPath,
        IReadOnlyList<string> nugetDllPaths,
        IReadOnlyList<string> additionalDllPaths,
        string assemblyName,
        CancellationToken cancellationToken);

    Task<string> CompileToExeAsync(
        IReadOnlyList<string> sourceFiles,
        string outputDirectory,
        IReadOnlyList<string> nugetDllPaths,
        IReadOnlyList<string> additionalDllPaths,
        string assemblyName,
        string mainTypeName,
        string compileType,
        CancellationToken cancellationToken);
}

public sealed class CompilationService : ICompilationService
{
    private readonly IReadOnlyList<string> _referenceAssemblyDirectories;

    public CompilationService(IReadOnlyList<string> referenceAssemblyDirectories)
    {
        _referenceAssemblyDirectories = referenceAssemblyDirectories ?? Array.Empty<string>();
    }

    public async Task<string> CompileToDllAsync(
        IReadOnlyList<string> sourceFiles,
        string dllOutputPath,
        IReadOnlyList<string> nugetDllPaths,
        IReadOnlyList<string> additionalDllPaths,
        string assemblyName,
        CancellationToken cancellationToken)
    {
        if (sourceFiles is null || sourceFiles.Count == 0)
            throw new ArgumentException("Source file list cannot be empty.", nameof(sourceFiles));
        if (string.IsNullOrWhiteSpace(dllOutputPath))
            throw new ArgumentException("Value cannot be null/empty.", nameof(dllOutputPath));
        if (string.IsNullOrWhiteSpace(assemblyName))
            throw new ArgumentException("Value cannot be null/empty.", nameof(assemblyName));
        if (_referenceAssemblyDirectories.Count == 0)
            throw new InvalidOperationException("No reference assembly directories were configured.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dllOutputPath))!);

        var filteredSources = sourceFiles
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var agent = new CSharpDllCompileAgent();
        agent.SetOption("CoreDllPath", _referenceAssemblyDirectories.ToList());

        agent.SetInput("Code", filteredSources);
        agent.SetInput("DllOutputPath", dllOutputPath);
        agent.SetInput("AssemblyName", assemblyName);
        agent.SetInput("NuGetDlls", (nugetDllPaths ?? Array.Empty<string>()).ToList());
        agent.SetInput("AdditionalDlls", (additionalDllPaths ?? Array.Empty<string>()).ToList());

        await agent.ExecuteAsync(cancellationToken);

        var compiledPath = agent.GetOutput<string>("CompiledDllPath");
        if (string.IsNullOrWhiteSpace(compiledPath))
            throw new InvalidOperationException("Compilation succeeded but no DLL path was returned.");

        return compiledPath;
    }

    public async Task<string> CompileToExeAsync(
        IReadOnlyList<string> sourceFiles,
        string outputDirectory,
        IReadOnlyList<string> nugetDllPaths,
        IReadOnlyList<string> additionalDllPaths,
        string assemblyName,
        string mainTypeName,
        string compileType,
        CancellationToken cancellationToken)
    {
        if (sourceFiles is null || sourceFiles.Count == 0)
            throw new ArgumentException("Source file list cannot be empty.", nameof(sourceFiles));
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("Value cannot be null/empty.", nameof(outputDirectory));
        if (string.IsNullOrWhiteSpace(assemblyName))
            throw new ArgumentException("Value cannot be null/empty.", nameof(assemblyName));
        if (string.IsNullOrWhiteSpace(mainTypeName))
            throw new ArgumentException("Value cannot be null/empty.", nameof(mainTypeName));
        if (string.IsNullOrWhiteSpace(compileType))
            throw new ArgumentException("Value cannot be null/empty.", nameof(compileType));
        if (_referenceAssemblyDirectories.Count == 0)
            throw new InvalidOperationException("No reference assembly directories were configured.");

        var filteredSources = sourceFiles
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var outputDirFull = Path.GetFullPath(outputDirectory);
        var appHostPath = AppHostTemplates.EnsureExtracted(AssemblyForgeAppHostTemplate.WindowsExe);

        var agent = new CSharpCompileAgent();
        agent.SetOption("CoreDllPath", _referenceAssemblyDirectories.ToList());

        agent.SetInput("Code", filteredSources);
        agent.SetInput("OutputPath", outputDirFull);
        agent.SetInput("AssemblyName", assemblyName);
        agent.SetInput("MainTypeName", mainTypeName);
        agent.SetInput("CompileType", compileType);
        agent.SetInput("AppHostApp", appHostPath);
        agent.SetInput("NuGetDlls", (nugetDllPaths ?? Array.Empty<string>()).ToList());
        agent.SetInput("AdditionalDlls", (additionalDllPaths ?? Array.Empty<string>()).ToList());

        await agent.ExecuteAsync(cancellationToken);

        var compiledPath = agent.GetOutput<string>("CompiledPath");
        if (string.IsNullOrWhiteSpace(compiledPath))
            throw new InvalidOperationException("Compilation succeeded but no EXE path was returned.");

        return compiledPath;
    }
}
