using Neo.AssemblyForge.Services;
using Neo.AssemblyForge.Utils;

namespace Neo.McpServer.Services;

/// <summary>
/// Wraps CompilationService and NuGetPackageService from Neo.AssemblyForge
/// to provide a simple compile-from-source-code API for MCP tools.
/// Uses Avalonia DLLs from the PluginWindowAvalonia build output instead of NuGet
/// for fast compilation (~2 seconds vs ~2 minutes).
/// </summary>
public sealed class CompilationPipeline
{
    private IReadOnlyList<string>? _referenceAssemblyDirs;
    private IReadOnlyList<string>? _avaloniaAdditionalDlls;
    private string? _nugetCacheDir;

    public record CompilationResult(
        bool Success,
        string? DllPath,
        byte[]? DllBytes,
        IReadOnlyList<string> DependencyDllPaths,
        IReadOnlyList<string> Errors);

    /// <summary>
    /// Compiles C# source code into a DLL.
    /// Avalonia assemblies are resolved from the PluginWindowAvalonia build output (fast).
    /// Additional NuGet packages (non-Avalonia) are resolved via NuGetPackageService.
    /// </summary>
    public async Task<CompilationResult> CompileAsync(
        IReadOnlyList<string> sourceCode,
        IReadOnlyDictionary<string, string>? nugetPackages = null,
        CancellationToken ct = default)
    {
        var errors = new List<string>();

        try
        {
            EnsureInitialized();

            System.Diagnostics.Debug.WriteLine($"[CompilationPipeline] RefDirs: {_referenceAssemblyDirs!.Count}, AvaloniaAdditional: {_avaloniaAdditionalDlls!.Count}");

            var compilationService = new CompilationService(_referenceAssemblyDirs!);

            // Split packages: Avalonia ones are already available locally, only resolve others via NuGet
            var nonAvaloniaPackages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (nugetPackages != null)
            {
                foreach (var (name, version) in nugetPackages)
                {
                    if (!name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))
                        nonAvaloniaPackages[name] = version;
                }
            }

            System.Diagnostics.Debug.WriteLine($"[CompilationPipeline] NonAvaloniaNuGet: {nonAvaloniaPackages.Count}");

            var nugetDllPaths = new List<string>();
            if (nonAvaloniaPackages.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine("[CompilationPipeline] Resolving NuGet packages...");
                var nugetService = new NuGetPackageService(
                    _nugetCacheDir!,
                    "net9.0",
                    _referenceAssemblyDirs!);

                var nugetResult = await nugetService.LoadPackagesAsync(
                    nonAvaloniaPackages,
                    Enumerable.Empty<string>(),
                    ct);

                nugetDllPaths.AddRange(nugetResult.DllPaths);
                System.Diagnostics.Debug.WriteLine($"[CompilationPipeline] NuGet resolved: {nugetDllPaths.Count} DLLs");
            }

            // Create temp directory for output
            var tempDir = Path.Combine(Path.GetTempPath(), "neo_mcp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var dllOutputPath = Path.Combine(tempDir, "DynamicUserControl.dll");

            // Avalonia DLLs from PluginWindow output + any NuGet DLLs
            var allAdditionalDlls = _avaloniaAdditionalDlls!.Concat(nugetDllPaths).ToList();

            System.Diagnostics.Debug.WriteLine($"[CompilationPipeline] Compiling with {allAdditionalDlls.Count} additional DLLs...");

            var compiledPath = await compilationService.CompileToDllAsync(
                sourceCode,
                dllOutputPath,
                nugetDllPaths: Array.Empty<string>().ToList(),
                additionalDllPaths: allAdditionalDlls,
                assemblyName: "DynamicUserControl",
                ct);

            System.Diagnostics.Debug.WriteLine($"[CompilationPipeline] Compiled to: {compiledPath}");
            var dllBytes = await File.ReadAllBytesAsync(compiledPath, ct);

            // Dependencies to stream to PluginWindow: only NuGet DLLs (Avalonia is already in the child)
            return new CompilationResult(
                Success: true,
                DllPath: compiledPath,
                DllBytes: dllBytes,
                DependencyDllPaths: nugetDllPaths,
                Errors: errors);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            if (ex.InnerException != null)
                errors.Add(ex.InnerException.Message);

            return new CompilationResult(
                Success: false,
                DllPath: null,
                DllBytes: null,
                DependencyDllPaths: Array.Empty<string>(),
                Errors: errors);
        }
    }

    private void EnsureInitialized()
    {
        if (_referenceAssemblyDirs != null) return;

        _referenceAssemblyDirs = DiscoverReferenceAssemblyDirs();
        if (_referenceAssemblyDirs.Count == 0)
            throw new InvalidOperationException(
                "No .NET reference assemblies found. Ensure .NET 9 runtime is installed.");

        _avaloniaAdditionalDlls = DiscoverAvaloniaFromPluginWindow();

        _nugetCacheDir = Path.Combine(Path.GetTempPath(), "neo_mcp_nuget");
        Directory.CreateDirectory(_nugetCacheDir);
    }

    /// <summary>
    /// Finds Avalonia DLLs from the Neo.PluginWindowAvalonia build output.
    /// These are used as reference assemblies for compilation — no NuGet download needed.
    /// </summary>
    private static List<string> DiscoverAvaloniaFromPluginWindow()
    {
        var dlls = new List<string>();

        // 1. NEO_PLUGIN_PATH environment variable (set in MCP server config)
        var pluginPath = Environment.GetEnvironmentVariable("NEO_PLUGIN_PATH");
        if (!string.IsNullOrWhiteSpace(pluginPath) && Directory.Exists(pluginPath))
        {
            dlls.AddRange(Directory.GetFiles(pluginPath, "*.dll"));
            if (dlls.Count > 0) return dlls;
        }

        // 2. Relative to this executable (deployed side-by-side)
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidate = Path.Combine(baseDir, "Avalonia.Base.dll");
        if (File.Exists(candidate))
        {
            dlls.AddRange(Directory.GetFiles(baseDir, "*.dll"));
            return dlls;
        }

        // 3. Dev-time: sibling project output
        foreach (var config in new[] { "Debug", "Release" })
        {
            var devDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                "Neo.PluginWindowAvalonia", "bin", config, "net9.0"));
            if (Directory.Exists(devDir))
            {
                dlls.AddRange(Directory.GetFiles(devDir, "*.dll"));
                if (dlls.Count > 0) return dlls;
            }
        }

        return dlls;
    }

    /// <summary>
    /// Uses the same DotNetRuntimeFinder as Neo.App to locate runtime assemblies.
    /// This works with just the .NET runtime — no SDK required.
    /// </summary>
    private static List<string> DiscoverReferenceAssemblyDirs()
    {
        var dirs = new List<string>();
        var dotnetMajor = Environment.Version.Major;

        // .NET Core runtime (System.Runtime.dll, etc.)
        var corePath = DotNetRuntimeFinder.GetHighestRuntimePath(DotNetRuntimeType.NetCoreApp, dotnetMajor);
        if (!string.IsNullOrWhiteSpace(corePath))
            dirs.Add(corePath);

        // Windows Desktop runtime (WPF/WinForms — optional, only on Windows)
        if (OperatingSystem.IsWindows())
        {
            var desktopPath = DotNetRuntimeFinder.GetHighestRuntimePath(DotNetRuntimeType.WindowsDesktopApp, dotnetMajor);
            if (!string.IsNullOrWhiteSpace(desktopPath))
                dirs.Add(desktopPath);
        }

        return dirs;
    }
}
