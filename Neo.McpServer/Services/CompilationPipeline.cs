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
    private IReadOnlyList<string>? _neoAppMcpRuntimeDlls;
    private IReadOnlyList<string>? _wpfAdditionalDlls;
    private string? _nugetCacheDir;

    /// <summary>Last successfully compiled source code files — used by save_session, and by patch_preview when the window is gone.</summary>
    public IReadOnlyList<string>? LastSourceCode { get; private set; }

    /// <summary>The source changed without passing through here: Smart Edit compiles inside the window.</summary>
    public void RememberSource(string sourceCode) => LastSourceCode = new[] { sourceCode };

    /// <summary>Last successfully used NuGet packages — used by patch_preview.</summary>
    public IReadOnlyDictionary<string, string>? LastNuGetPackages { get; private set; }

    /// <summary>Last framework used for compilation — used by patch_preview.</summary>
    public string? LastFramework { get; private set; }

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
        string framework = "avalonia",
        CancellationToken ct = default)
    {
        var errors = new List<string>();
        framework = (framework ?? "avalonia").ToLowerInvariant();

        try
        {
            EnsureInitialized();

            var frameworkDlls = framework == "wpf" ? _wpfAdditionalDlls : _avaloniaAdditionalDlls;
            System.Diagnostics.Debug.WriteLine($"[CompilationPipeline] RefDirs: {_referenceAssemblyDirs!.Count}, {framework}Additional: {frameworkDlls?.Count ?? 0}");

            var compilationService = new CompilationService(_referenceAssemblyDirs!);

            // Whatever the plugin window ships (Avalonia itself, the web view, …) is referenced from
            // there and never downloaded: that copy is what the code runs on. Everything else —
            // including Avalonia add-ons such as Avalonia.Controls.DataGrid — comes from NuGet.
            var hostIndex = HostProvidedAssemblies.Index(frameworkDlls);
            var nonFrameworkPackages = HostProvidedAssemblies.WithoutProvidedPackages(nugetPackages, hostIndex);

            System.Diagnostics.Debug.WriteLine($"[CompilationPipeline] NonFrameworkNuGet: {nonFrameworkPackages.Count}");

            var nugetDllPaths = new List<string>();
            if (nonFrameworkPackages.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine("[CompilationPipeline] Resolving NuGet packages...");
                var nugetService = new NuGetPackageService(
                    _nugetCacheDir!,
                    Neo.Shared.NeoBuildInfo.RuntimeTargetFramework,
                    _referenceAssemblyDirs!,
                    frameworkDlls);

                var nugetResult = await nugetService.LoadPackagesAsync(
                    nonFrameworkPackages,
                    Enumerable.Empty<string>(),
                    ct);

                nugetDllPaths.AddRange(nugetResult.DllPaths);
                // An add-on drags in the lowest Avalonia it allows; the host's copy has to win.
                HostProvidedAssemblies.RemoveShadowing(nugetDllPaths, hostIndex);
                System.Diagnostics.Debug.WriteLine($"[CompilationPipeline] NuGet resolved: {nugetDllPaths.Count} DLLs");
            }

            // Create temp directory for output
            var tempDir = Path.Combine(Path.GetTempPath(), "neo_mcp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var dllOutputPath = Path.Combine(tempDir, "DynamicUserControl.dll");

            // Framework DLLs from PluginWindow output + any NuGet DLLs
            var allAdditionalDlls = (frameworkDlls ?? Array.Empty<string>()).Concat(nugetDllPaths).ToList();

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

            // Remember source code and framework for patch_preview
            LastSourceCode = sourceCode.ToList();
            LastNuGetPackages = nugetPackages;
            LastFramework = framework;

            // Dependencies to stream to PluginWindow: only NuGet DLLs (framework DLLs are already in the child)
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
        bool mcpMode = false,
        CancellationToken ct = default)
    {
        var errors = new List<string>();

        try
        {
            EnsureInitialized();

            var exportDir = Path.Combine(exportPath, appName);
            Directory.CreateDirectory(exportDir);

            // Frozen-Mode wraps need to remain CONSOLE-attached on Windows so stdio
            // JSON-RPC works when launched with --mcp. WINDOWS subsystem detaches stdin/stdout
            // and breaks the JSON-RPC handshake. Linux/macOS already use CONSOLE.
            var (template, compileType) = (platform.ToLowerInvariant(), mcpMode) switch
            {
                ("windows" or "win", false) => (AssemblyForgeAppHostTemplate.WindowsExe, "WINDOWS"),
                ("windows" or "win", true)  => (AssemblyForgeAppHostTemplate.WindowsExe, "CONSOLE"),
                ("linux", _) => (AssemblyForgeAppHostTemplate.Linux, "CONSOLE"),
                ("osx" or "macos", _) => (AssemblyForgeAppHostTemplate.Osx, "CONSOLE"),
                _ => (AssemblyForgeAppHostTemplate.WindowsExe, mcpMode ? "CONSOLE" : "WINDOWS"),
            };

            var appHostPath = AppHostTemplates.EnsureExtracted(template);

            // Build source code: user's code + the appropriate wrapper.
            var allCode = sourceCode.ToList();
            allCode.Add(GetAvaloniaExportWrapper(appName, mcpMode));

            // Resolve the packages the plugin window does not already ship (same rule as CompileAsync)
            var hostIndex = HostProvidedAssemblies.Index(_avaloniaAdditionalDlls);
            var nonAvaloniaPackages = HostProvidedAssemblies.WithoutProvidedPackages(nugetPackages, hostIndex);

            var nugetDllPaths = new List<string>();
            if (nonAvaloniaPackages.Count > 0)
            {
                var nugetService = new NuGetPackageService(
                    _nugetCacheDir!, Neo.Shared.NeoBuildInfo.RuntimeTargetFramework, _referenceAssemblyDirs!, _avaloniaAdditionalDlls);
                var nugetResult = await nugetService.LoadPackagesAsync(
                    nonAvaloniaPackages, Enumerable.Empty<string>(), ct);
                nugetDllPaths.AddRange(nugetResult.DllPaths);
                HostProvidedAssemblies.RemoveShadowing(nugetDllPaths, hostIndex);
            }

            // Compile to EXE
            var agent = new CSharpCompileAgent();
            agent.SetOption("CoreDllPath", _referenceAssemblyDirs!.ToList());

            agent.SetInput("Code", allCode);
            agent.SetInput("OutputPath", exportDir);
            agent.SetInput("AssemblyName", appName);
            agent.SetInput("MainTypeName", ExportHostNamespace + ".Program");
            agent.SetInput("CompileType", compileType);
            agent.SetInput("ForceNetCoreRuntime", true);
            agent.SetInput("AppHostApp", appHostPath);
            agent.SetInput("NuGetDlls", nugetDllPaths);

            // The Frozen-Mode runtime (the bootstrap calls Neo.App.Mcp.NeoAppMcp.RunStdioAsync) carries
            // BCL assemblies that are newer than the shared framework (System.Text.Json 10, …). Only
            // the .NET host can prefer those over the framework copies, so they go into an active
            // deps.json ("HostResolvedDlls") instead of being preloaded by the bootstrap — once the
            // framework copy is loaded, loading the newer one fails.
            if (mcpMode)
            {
                if (_neoAppMcpRuntimeDlls == null || _neoAppMcpRuntimeDlls.Count == 0)
                    return new ExportResult(false, null, null,
                        ["mcpMode requested but the Neo.App.Mcp runtime closure is not on disk. " +
                         "Rebuild Neo.McpServer (its bin folder is the canonical source)."]);
                agent.SetInput("HostResolvedDlls", _neoAppMcpRuntimeDlls.ToList());
            }

            // Compile-only references: the Avalonia closure from the plugin window. Anything the
            // Frozen-Mode runtime already brings (Neo.App.Api.dll) must not be referenced twice.
            var hostResolvedNames = new HashSet<string>(
                mcpMode ? _neoAppMcpRuntimeDlls!.Select(d => Path.GetFileName(d)!) : [],
                StringComparer.OrdinalIgnoreCase);
            var compileReferences = _avaloniaAdditionalDlls!
                .Where(d => !hostResolvedNames.Contains(Path.GetFileName(d)))
                .ToList();
            agent.SetInput("AdditionalDlls", compileReferences);

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

            // Frozen-Mode runtime: ship Neo.App.Mcp + ModelContextProtocol + Hosting closure.
            if (mcpMode)
            {
                foreach (var dll in _neoAppMcpRuntimeDlls!)
                {
                    var destFile = Path.Combine(exportDir, Path.GetFileName(dll));
                    if (!File.Exists(destFile))
                        File.Copy(dll, destFile, overwrite: false);
                }
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

    // Namespace of the generated bootstrap program. It must not be 'Neo', and its Application
    // class must not be called 'App': a type Neo.App shadows the Neo.App namespace of Neo.App.Api,
    // and every exported app whose code says 'using Neo.App;' (Ai.Trigger, Live-MCP) stops compiling.
    internal const string ExportHostNamespace = "NeoExportHost";

    /// <summary>
    /// Source of the program that turns a <c>DynamicUserControl</c> into a standalone Avalonia
    /// application: preload the DLLs next to the exe, find the control by reflection, show it in
    /// a window.
    ///
    /// <para>With <paramref name="mcpMode"/> (Frozen-Mode, Phase 4B) the same program also understands
    /// <list type="bullet">
    ///   <item><c>--mcp-help</c>: dump the Live-MCP manifest to stderr via
    ///         <c>NeoAppMcp.DumpManifest</c> and exit before starting the GUI. Cheap CI smoke test.</item>
    ///   <item><c>--mcp</c>: start the embedded stdio MCP server on a background Task once the
    ///         UserControl exists. Cancellation is tied to the desktop lifetime so closing the window
    ///         tears down the JSON-RPC loop. <c>--headless</c> is intentionally not handled in v1.</item>
    /// </list>
    /// One template for both modes, so the bootstrap cannot drift apart between them.</para>
    /// </summary>
    internal static string GetAvaloniaExportWrapper(string windowTitle, bool mcpMode)
    {
        var escapedTitle = windowTitle.Replace("\"", "\\\"");

        var mcpUsings = mcpMode ? "using Neo.App.Mcp;" : string.Empty;

        var mcpHelpBranch = !mcpMode ? string.Empty : $$"""

            // --mcp-help: dump manifest before starting Avalonia. Cheap diagnostics path.
            if (args.Any(a => string.Equals(a, "--mcp-help", StringComparison.OrdinalIgnoreCase)))
                return DumpManifest();
""";

        var mcpHelpers = !mcpMode ? string.Empty : $$"""

        // Separate methods for the same reason as StartAvalonia: nothing that names a
        // Neo.App.Mcp or Avalonia type may be JIT-compiled before the preloader has run.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int DumpManifest()
        {
            var ctrl = CreateUserControl();
            if (ctrl == null)
            {
                Console.Error.WriteLine("DynamicUserControl not found in any loaded assembly.");
                return 1;
            }
            NeoAppMcp.DumpManifest(ctrl);
            return 0;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void StartMcpServer(UserControl ctrl, CancellationToken ct)
        {
            Console.Error.WriteLine("[{{escapedTitle}}] --mcp mode: starting embedded stdio MCP server.");
            _ = Task.Run(async () =>
            {
                try { await NeoAppMcp.RunStdioAsync(ctrl, options: null, ct); }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[{{escapedTitle}}] MCP host crashed: {ex}");
                }
            });
        }
""";

        var mcpStartBranch = !mcpMode ? string.Empty : $$"""

                if (ctrl != null && _args.Any(a => string.Equals(a, "--mcp", StringComparison.OrdinalIgnoreCase)))
                    Program.StartMcpServer(ctrl, _mcpCts.Token);
""";

        return $$"""
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
{{mcpUsings}}

namespace {{ExportHostNamespace}}
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            TaskScheduler.UnobservedTaskException += (s, e) => { e.SetObserved(); };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { };

            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            // Preload all DLLs from the app directory BEFORE any Avalonia / MCP types are touched.
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
{{mcpHelpBranch}}
            // Call Avalonia startup in a separate method so JIT doesn't resolve
            // Avalonia types until AFTER the preloader has run
            return StartAvalonia(args);
        }

        // Must be a separate method — JIT resolves type references at method entry
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int StartAvalonia(string[] args)
        {
            return AppBuilder.Configure(() => new ExportHostApp(args))
                .UsePlatformDetect()
                .LogToTrace()
                .StartWithClassicDesktopLifetime(args);
        }

        // Find DynamicUserControl in any namespace
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static UserControl? CreateUserControl()
        {
            var controlType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.Name == "DynamicUserControl" && typeof(UserControl).IsAssignableFrom(t));
            return controlType == null ? null : (UserControl)Activator.CreateInstance(controlType)!;
        }
{{mcpHelpers}}
    }

    public class ExportHostApp : Application
    {
        private readonly string[] _args;
        private readonly CancellationTokenSource _mcpCts = new();

        public ExportHostApp() : this(Array.Empty<string>()) { }
        public ExportHostApp(string[] args) { _args = args; }

        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var ctrl = Program.CreateUserControl();
                if (ctrl != null)
                {
                    ctrl.HorizontalAlignment = HorizontalAlignment.Stretch;
                    ctrl.VerticalAlignment = VerticalAlignment.Stretch;
                }

                desktop.MainWindow = new Window
                {
                    Title = "{{escapedTitle}}",
                    Width = 1024,
                    Height = 768,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Content = (Avalonia.Controls.Control?)ctrl ?? new TextBlock
                    {
                        Text = "Error: DynamicUserControl not found.",
                        FontSize = 24,
                        Foreground = Brushes.Red,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };

                desktop.Exit += (_, _) => _mcpCts.Cancel();
{{mcpStartBranch}}
            }
            base.OnFrameworkInitializationCompleted();
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

        // Same folder the managed Avalonia assemblies were discovered in (NEO_PLUGIN_PATH,
        // side-by-side deployment or the sibling project's build output).
        var pluginDir = _avaloniaAdditionalDlls is { Count: > 0 }
            ? Path.GetDirectoryName(_avaloniaAdditionalDlls[0])
            : null;
        var nativeDir = Path.Combine(pluginDir ?? AppDomain.CurrentDomain.BaseDirectory, "runtimes", rid, "native");

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
                $"No .NET reference assemblies found. Ensure the .NET {Neo.Shared.NeoBuildInfo.RuntimeMajor} runtime is installed.");

        _avaloniaAdditionalDlls = DiscoverAvaloniaFromPluginWindow();
        _neoAppMcpRuntimeDlls = DiscoverNeoAppMcpRuntime();

        // Discover WPF DLLs (only on Windows)
        if (OperatingSystem.IsWindows())
            _wpfAdditionalDlls = DiscoverWpfFromPluginWindow();

        _nugetCacheDir = PrepareNuGetFolder();
    }

    /// <summary>The folder this pipeline downloads NuGet packages into; null before the first compile.</summary>
    internal string? NuGetFolder => _nugetCacheDir;

    /// <summary>
    /// The package folder of this server. It starts empty, like the desktop hosts' one, or it only ever
    /// grows. One per process, because every Claude session has a server of its own; what servers that
    /// are gone left behind is removed in the background, a first compile need not wait for that.
    /// </summary>
    private static string PrepareNuGetFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "neo_mcp_nuget");
        var own = Path.Combine(root, Environment.ProcessId.ToString());
        Directory.CreateDirectory(own);

        _ = Task.Run(() =>
        {
            foreach (var entry in Directory.GetFileSystemEntries(root))
            {
                if (int.TryParse(Path.GetFileName(entry), out var pid) && IsRunning(pid)) continue;
                try
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                    else File.Delete(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Debug.WriteLine($"[CompilationPipeline] Left for the next server: {entry}: {ex.Message}");
                }
            }
        });

        return own;

        static bool IsRunning(int pid)
        {
            try { using var process = System.Diagnostics.Process.GetProcessById(pid); return !process.HasExited; }
            catch (ArgumentException) { return false; }
        }
    }

    /// <summary>
    /// Phase 4B: locate the Frozen-Mode runtime closure (Neo.App.Mcp + ModelContextProtocol +
    /// Microsoft.Extensions.{Hosting,Logging,DI,...}). Neo.McpServer pulls these in via a
    /// project reference to Neo.App.Mcp, so its own bin folder is the canonical source —
    /// see Neo.McpServer.csproj for the rationale.
    ///
    /// <para>McpServer's bin also contains Anthropic / OpenAI SDKs, Roslyn and other unrelated
    /// dependencies that must NOT land in a frozen TODO app, so the folder is not copied
    /// wholesale. The exact closure comes from the dependency graph in the <c>.deps.json</c> next
    /// to the running executable, starting at the Neo.App.Mcp library. That graph is the only
    /// reliable source: the MCP SDK also brings app-local BCL assemblies (System.Text.Json 10,
    /// System.IO.Pipelines, …) that are newer than the shared framework — a frozen app exported
    /// without them starts its window and then dies the moment the MCP server comes up.
    /// A file-name allow-list is the fallback for deployments without a deps.json.</para>
    /// </summary>
    internal static List<string> DiscoverNeoAppMcpRuntime()
    {
        var prefixes = new[]
        {
            "Neo.App.Mcp.", "Neo.App.Api.",
            "ModelContextProtocol.",
            "Microsoft.Extensions.AI.",
            "Microsoft.Extensions.Hosting.",
            "Microsoft.Extensions.Configuration.",
            "Microsoft.Extensions.DependencyInjection.",
            "Microsoft.Extensions.Logging.",
            "Microsoft.Extensions.Options.",
            "Microsoft.Extensions.Diagnostics.",
            "Microsoft.Extensions.FileProviders.",
            "Microsoft.Extensions.FileSystemGlobbing",
            "Microsoft.Extensions.Caching.",
            "Microsoft.Extensions.Primitives",
            "System.Diagnostics.DiagnosticSource.", "System.Diagnostics.EventLog.",
            "System.IO.Pipelines.", "System.Net.ServerSentEvents.",
            "System.Text.Encodings.Web.", "System.Text.Json."
        };

        // Production: McpServer's own bin (alongside the running exe). Dev-time fallback: walk up
        // to the repo root and use Neo.McpServer's bin output.
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidateDirs = new List<string> { baseDir };
        foreach (var config in Neo.Shared.NeoBuildInfo.DevBuildConfigurations)
            candidateDirs.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                "Neo.McpServer", "bin", config, Neo.Shared.NeoBuildInfo.TargetFramework)));

        foreach (var dir in candidateDirs)
        {
            var fromGraph = CollectFromDependencyGraph(dir);
            if (fromGraph.Count > 0) return fromGraph;
        }

        var dlls = new List<string>();
        foreach (var dir in candidateDirs)
        {
            CollectMatching(dir, prefixes, dlls);
            if (dlls.Count > 0) return dlls;
        }

        return dlls;

        // Walks "<anything>.deps.json" in the folder from the Neo.App.Mcp library downwards and
        // returns the runtime assemblies of that sub-graph which exist in the folder. Avalonia and
        // its own closure are skipped: they ship through the plugin-window path, including the
        // desktop backend and the native libraries this graph does not know about.
        static List<string> CollectFromDependencyGraph(string dir)
        {
            var result = new List<string>();
            if (!Directory.Exists(dir)) return result;

            foreach (var depsFile in Directory.GetFiles(dir, "*.deps.json"))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(depsFile));
                    if (!doc.RootElement.TryGetProperty("targets", out var targets)) continue;

                    foreach (var target in targets.EnumerateObject())
                    {
                        var libraries = target.Value.EnumerateObject()
                            .ToDictionary(l => l.Name.Split('/')[0], l => l.Value, StringComparer.OrdinalIgnoreCase);
                        if (!libraries.ContainsKey("Neo.App.Mcp")) continue;

                        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                        var queue = new Queue<string>();
                        queue.Enqueue("Neo.App.Mcp");

                        while (queue.Count > 0)
                        {
                            var name = queue.Dequeue();
                            if (!visited.Add(name) || IsAvaloniaClosure(name)) continue;
                            if (!libraries.TryGetValue(name, out var library)) continue;

                            if (library.TryGetProperty("runtime", out var runtime))
                                foreach (var asset in runtime.EnumerateObject())
                                    files.Add(Path.GetFileName(asset.Name));

                            if (library.TryGetProperty("dependencies", out var dependencies))
                                foreach (var dependency in dependencies.EnumerateObject())
                                    queue.Enqueue(dependency.Name);
                        }

                        result.AddRange(files.Select(f => Path.Combine(dir, f)).Where(File.Exists));
                        if (result.Count > 0) return result;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CompilationPipeline] deps.json not usable ({depsFile}): {ex.Message}");
                }
            }

            return result;
        }

        static bool IsAvaloniaClosure(string libraryName) =>
            libraryName.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) ||
            libraryName.StartsWith("MicroCom", StringComparison.OrdinalIgnoreCase) ||
            libraryName.StartsWith("SkiaSharp", StringComparison.OrdinalIgnoreCase) ||
            libraryName.StartsWith("HarfBuzzSharp", StringComparison.OrdinalIgnoreCase) ||
            libraryName.StartsWith("Tmds.DBus", StringComparison.OrdinalIgnoreCase);

        static void CollectMatching(string dir, string[] prefixes, List<string> sink)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var dll in Directory.GetFiles(dir, "*.dll"))
            {
                var name = Path.GetFileName(dll);
                if (prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    sink.Add(dll);
            }
        }
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

        // 2. Relative to this executable (deployed side-by-side with the plugin window).
        //    The marker must be a DLL only the plugin window brings: the server's own bin contains
        //    Avalonia.Base.dll too (via Neo.App.Mcp), but no desktop backend and no Fluent theme —
        //    taking it for a side-by-side deployment breaks export_app.
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidate = Path.Combine(baseDir, "Avalonia.Desktop.dll");
        if (File.Exists(candidate))
        {
            dlls.AddRange(Directory.GetFiles(baseDir, "*.dll"));
            return dlls;
        }

        // 3. Dev-time: sibling project output
        foreach (var config in Neo.Shared.NeoBuildInfo.DevBuildConfigurations)
        {
            var devDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                "Neo.PluginWindowAvalonia", "bin", config, Neo.Shared.NeoBuildInfo.TargetFramework));
            if (Directory.Exists(devDir))
            {
                dlls.AddRange(Directory.GetFiles(devDir, "*.dll"));
                if (dlls.Count > 0) return dlls;
            }
        }

        return dlls;
    }

    /// <summary>
    /// Finds WPF DLLs from the Neo.PluginWindowWPF.MCP build output.
    /// These are used as reference assemblies for WPF compilation.
    /// </summary>
    private static List<string> DiscoverWpfFromPluginWindow()
    {
        var dlls = new List<string>();

        // 1. NEO_PLUGIN_PATH_WPF environment variable
        var pluginPath = Environment.GetEnvironmentVariable("NEO_PLUGIN_PATH_WPF");
        if (!string.IsNullOrWhiteSpace(pluginPath) && Directory.Exists(pluginPath))
        {
            dlls.AddRange(Directory.GetFiles(pluginPath, "*.dll"));
            if (dlls.Count > 0) return dlls;
        }

        // 2. Relative to this executable (deployed side-by-side)
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidate = Path.Combine(baseDir, "PresentationFramework.dll");
        if (File.Exists(candidate))
        {
            dlls.AddRange(Directory.GetFiles(baseDir, "*.dll"));
            return dlls;
        }

        // 3. Dev-time: sibling project output (WPF uses the -windows TFM)
        foreach (var projectName in new[] { "Neo.PluginWindowWPF.MCP", "Neo.PluginWindowWPF" })
        {
            foreach (var config in Neo.Shared.NeoBuildInfo.DevBuildConfigurations)
            {
                var devDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                    projectName, "bin", config, Neo.Shared.NeoBuildInfo.TargetFrameworkWindows));
                if (Directory.Exists(devDir))
                {
                    dlls.AddRange(Directory.GetFiles(devDir, "*.dll"));
                    if (dlls.Count > 0) return dlls;
                }
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
