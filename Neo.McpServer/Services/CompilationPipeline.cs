using Neo.Agents;
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

    public record ExportResult(
        bool Success,
        string? ExePath,
        string? ExportDirectory,
        IReadOnlyList<string> Errors);

    /// <summary>
    /// Exports the given source code as a standalone Avalonia executable.
    /// The exported app includes the Avalonia wrapper (window, exception handler, assembly preloader).
    /// </summary>
    public async Task<ExportResult> ExportAsync(
        IReadOnlyList<string> sourceCode,
        string appName,
        string exportPath,
        string platform = "windows",
        IReadOnlyDictionary<string, string>? nugetPackages = null,
        CancellationToken ct = default)
    {
        var errors = new List<string>();

        try
        {
            EnsureInitialized();

            var exportDir = Path.Combine(exportPath, appName);
            Directory.CreateDirectory(exportDir);

            // Determine AppHost template and compile type based on platform
            var (template, compileType) = platform.ToLowerInvariant() switch
            {
                "windows" or "win" => (AssemblyForgeAppHostTemplate.WindowsExe, "WINDOWS"),
                "linux" => (AssemblyForgeAppHostTemplate.Linux, "CONSOLE"),
                "osx" or "macos" => (AssemblyForgeAppHostTemplate.Osx, "CONSOLE"),
                _ => (AssemblyForgeAppHostTemplate.WindowsExe, "WINDOWS"),
            };

            var appHostPath = AppHostTemplates.EnsureExtracted(template);

            // Build source code: user's code + Avalonia export wrapper
            var allCode = sourceCode.ToList();
            allCode.Add(GetAvaloniaExportWrapper(appName));

            // Resolve non-Avalonia NuGet packages
            var nonAvaloniaPackages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (nugetPackages != null)
            {
                foreach (var (name, version) in nugetPackages)
                {
                    if (!name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))
                        nonAvaloniaPackages[name] = version;
                }
            }

            var nugetDllPaths = new List<string>();
            if (nonAvaloniaPackages.Count > 0)
            {
                var nugetService = new NuGetPackageService(
                    _nugetCacheDir!, "net9.0", _referenceAssemblyDirs!);
                var nugetResult = await nugetService.LoadPackagesAsync(
                    nonAvaloniaPackages, Enumerable.Empty<string>(), ct);
                nugetDllPaths.AddRange(nugetResult.DllPaths);
            }

            // Compile to EXE
            var agent = new CSharpCompileAgent();
            agent.SetOption("CoreDllPath", _referenceAssemblyDirs!.ToList());

            agent.SetInput("Code", allCode);
            agent.SetInput("OutputPath", exportDir);
            agent.SetInput("AssemblyName", appName);
            agent.SetInput("MainTypeName", "Neo.Program");
            agent.SetInput("CompileType", compileType);
            agent.SetInput("ForceNetCoreRuntime", true);
            agent.SetInput("AppHostApp", appHostPath);
            agent.SetInput("NuGetDlls", nugetDllPaths);
            agent.SetInput("AdditionalDlls", _avaloniaAdditionalDlls!.ToList());

            await agent.ExecuteAsync(ct);

            var compiledPath = agent.GetOutput<string>("CompiledPath");
            if (string.IsNullOrWhiteSpace(compiledPath))
                return new ExportResult(false, null, null, ["Compilation succeeded but no EXE path was returned."]);

            // Copy Avalonia DLLs to export directory (the app needs them at runtime)
            foreach (var dll in _avaloniaAdditionalDlls!)
            {
                var destFile = Path.Combine(exportDir, Path.GetFileName(dll));
                if (!File.Exists(destFile))
                    File.Copy(dll, destFile, overwrite: false);
            }

            // Copy NuGet DLLs
            foreach (var dll in nugetDllPaths)
            {
                var destFile = Path.Combine(exportDir, Path.GetFileName(dll));
                if (!File.Exists(destFile))
                    File.Copy(dll, destFile, overwrite: false);
            }

            // Copy native libraries (libSkiaSharp, libHarfBuzzSharp) for target platform
            CopyNativeLibraries(exportDir, platform);

            return new ExportResult(true, compiledPath, exportDir, errors);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            if (ex.InnerException != null)
                errors.Add(ex.InnerException.Message);
            return new ExportResult(false, null, null, errors);
        }
    }

    /// <summary>
    /// Generates the Avalonia app wrapper code that turns a DynamicUserControl into a standalone application.
    /// This is a simplified version of ExportWindowBaseCode.CreateBaseCodeForExportAvalonia.
    /// </summary>
    private static string GetAvaloniaExportWrapper(string windowTitle)
    {
        var escapedTitle = windowTitle.Replace("\"", "\\\"");
        return $$"""
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;

namespace Neo
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            TaskScheduler.UnobservedTaskException += (s, e) => { e.SetObserved(); };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { };

            // Preload all DLLs from the app directory BEFORE any Avalonia types are touched
            string baseDir = AppContext.BaseDirectory;
            foreach (string dll in Directory.EnumerateFiles(baseDir, "*.dll"))
            {
                try
                {
                    var name = AssemblyName.GetAssemblyName(dll);
                    if (!AppDomain.CurrentDomain.GetAssemblies().Any(a =>
                        AssemblyName.ReferenceMatchesDefinition(name, a.GetName())))
                        Assembly.LoadFrom(dll);
                }
                catch { }
            }

            // Call Avalonia startup in a separate method so JIT doesn't resolve
            // Avalonia types until AFTER the preloader has run
            StartAvalonia(args);
        }

        // Must be a separate method — JIT resolves type references at method entry
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void StartAvalonia(string[] args)
        {
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace()
                .StartWithClassicDesktopLifetime(args);
        }
    }

    public class App : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new MainWindow();
            base.OnFrameworkInitializationCompleted();
        }
    }

    public class MainWindow : Window
    {
        public MainWindow()
        {
            Title = "{{escapedTitle}}";
            Width = 1024;
            Height = 768;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Find DynamicUserControl in any namespace
            var controlType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.Name == "DynamicUserControl" && typeof(UserControl).IsAssignableFrom(t));

            if (controlType != null)
            {
                var control = (UserControl)Activator.CreateInstance(controlType)!;
                control.HorizontalAlignment = HorizontalAlignment.Stretch;
                control.VerticalAlignment = VerticalAlignment.Stretch;
                Content = control;
            }
            else
            {
                Content = new TextBlock
                {
                    Text = "Error: DynamicUserControl not found.",
                    FontSize = 24,
                    Foreground = Brushes.Red,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
        }
    }
}
""";
    }

    /// <summary>
    /// Copies native libraries (SkiaSharp, HarfBuzz) from the PluginWindow runtimes directory.
    /// </summary>
    private void CopyNativeLibraries(string exportDir, string platform)
    {
        var rid = platform.ToLowerInvariant() switch
        {
            "windows" or "win" => "win-x64",
            "linux" => "linux-x64",
            "osx" or "macos" => "osx",
            _ => "win-x64",
        };

        var pluginPath = Environment.GetEnvironmentVariable("NEO_PLUGIN_PATH")
            ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Neo.PluginWindowAvalonia", "bin", "Debug", "net9.0");

        var nativeDir = Path.Combine(pluginPath, "runtimes", rid, "native");
        if (!Directory.Exists(nativeDir))
        {
            // Dev-time fallback
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var config in new[] { "Debug", "Release" })
            {
                var devDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                    "Neo.PluginWindowAvalonia", "bin", config, "net9.0", "runtimes", rid, "native"));
                if (Directory.Exists(devDir)) { nativeDir = devDir; break; }
            }
        }

        if (Directory.Exists(nativeDir))
        {
            foreach (var file in Directory.GetFiles(nativeDir))
            {
                var destFile = Path.Combine(exportDir, Path.GetFileName(file));
                if (!File.Exists(destFile))
                    File.Copy(file, destFile, overwrite: false);
            }
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
