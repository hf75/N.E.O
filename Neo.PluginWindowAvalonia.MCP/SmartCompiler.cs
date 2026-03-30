using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Neo.Agents;
using Neo.AssemblyForge;
using Neo.AssemblyForge.Utils;

namespace Neo.PluginWindowAvalonia.MCP;

/// <summary>
/// Embedded Roslyn compiler that runs inside the PluginWindow process.
/// Compiles C# source code to DLL bytes for hot-reload — no MCP server needed.
/// </summary>
internal sealed class SmartCompiler
{
    private IReadOnlyList<string>? _referenceAssemblyDirs;
    private IReadOnlyList<string>? _additionalDlls;
    private string? _lastCompiledCode;

    public string? LastCompiledCode => _lastCompiledCode;

    /// <summary>Seeds the last compiled code without compiling (for patching against MCP-compiled code).</summary>
    public void SeedCode(string code) => _lastCompiledCode = code;

    public record CompileResult(bool Success, byte[]? DllBytes, string? Error);

    public async Task<CompileResult> CompileAsync(string sourceCode, CancellationToken ct = default)
    {
        try
        {
            EnsureInitialized();

            var agent = new CSharpDllCompileAgent();
            agent.SetOption("CoreDllPath", _referenceAssemblyDirs!.ToList());

            var tempDir = Path.Combine(Path.GetTempPath(), "neo_smart", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var dllPath = Path.Combine(tempDir, "DynamicUserControl.dll");

            agent.SetInput("Code", new List<string> { sourceCode });
            agent.SetInput("DllOutputPath", dllPath);
            agent.SetInput("AssemblyName", "DynamicUserControl");
            agent.SetInput("NuGetDlls", new List<string>());
            agent.SetInput("AdditionalDlls", _additionalDlls!.ToList());

            await agent.ExecuteAsync(ct);

            var compiledPath = agent.GetOutput<string>("CompiledDllPath");
            if (string.IsNullOrWhiteSpace(compiledPath) || !File.Exists(compiledPath))
                return new CompileResult(false, null, "Compilation succeeded but no DLL was produced.");

            var bytes = await File.ReadAllBytesAsync(compiledPath, ct);
            _lastCompiledCode = sourceCode;
            return new CompileResult(true, bytes, null);
        }
        catch (Exception ex)
        {
            return new CompileResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Applies a unified diff patch to the last compiled code and compiles the result.
    /// </summary>
    public async Task<CompileResult> PatchAndCompileAsync(string patch, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_lastCompiledCode))
            return new CompileResult(false, null, "No previous code to patch.");

        var patchResult = UnifiedDiffPatcher.TryApply(
            _lastCompiledCode, patch, "./currentcode.cs", "DynamicUserControl");

        if (!patchResult.Success)
            return new CompileResult(false, null, $"Patch failed: {patchResult.ErrorMessage}");

        return await CompileAsync(patchResult.PatchedText!, ct);
    }

    private void EnsureInitialized()
    {
        if (_referenceAssemblyDirs != null) return;

        // Find .NET runtime assemblies
        var dirs = new List<string>();
        var dotnetMajor = Environment.Version.Major;

        var corePath = DotNetRuntimeFinder.GetHighestRuntimePath(DotNetRuntimeType.NetCoreApp, dotnetMajor);
        if (!string.IsNullOrWhiteSpace(corePath))
            dirs.Add(corePath);

        if (OperatingSystem.IsWindows())
        {
            var desktopPath = DotNetRuntimeFinder.GetHighestRuntimePath(DotNetRuntimeType.WindowsDesktopApp, dotnetMajor);
            if (!string.IsNullOrWhiteSpace(desktopPath))
                dirs.Add(desktopPath);
        }

        if (dirs.Count == 0)
            throw new InvalidOperationException("No .NET runtime found. Ensure .NET 9 runtime is installed.");

        _referenceAssemblyDirs = dirs;

        // Find Avalonia DLLs from our own directory
        var baseDir = AppContext.BaseDirectory;
        _additionalDlls = Directory.GetFiles(baseDir, "*.dll")
            .Where(f => !Path.GetFileName(f).StartsWith("Neo.PluginWindowAvalonia"))
            .ToList();
    }
}
