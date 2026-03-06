using System;
using System.Collections.Generic;
using System.IO;
using Neo.AssemblyForge.Utils;

namespace Neo.AssemblyForge;

public static class AssemblyForgeTemplates
{
    public static string GetBaseCode(AssemblyForgeUiFramework uiFramework)
        => uiFramework switch
        {
            AssemblyForgeUiFramework.Avalonia => AvaloniaBaseCode,
            _ => WpfBaseCode,
        };

    public static string GetExecutableBaseCode(AssemblyForgeUiFramework uiFramework, string mainTypeName)
    {
        if (string.IsNullOrWhiteSpace(mainTypeName))
            mainTypeName = "Neo.Dynamic.DynamicProgram";

        var (ns, typeName) = SplitNamespaceAndType(mainTypeName);

        return uiFramework switch
        {
            AssemblyForgeUiFramework.Avalonia => BuildAvaloniaExecutableBaseCode(ns, typeName),
            _ => BuildWpfExecutableBaseCode(ns, typeName),
        };
    }

    private static (string Namespace, string TypeName) SplitNamespaceAndType(string mainTypeName)
    {
        var trimmed = (mainTypeName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return ("Neo.Dynamic", "DynamicProgram");

        var lastDot = trimmed.LastIndexOf('.');
        if (lastDot <= 0 || lastDot >= trimmed.Length - 1)
            return ("Neo.Dynamic", trimmed);

        return (trimmed.Substring(0, lastDot), trimmed.Substring(lastDot + 1));
    }

    private static string BuildWpfExecutableBaseCode(string ns, string typeName)
        => $@"
using System;
using System.Windows;
using System.Windows.Controls;

namespace {ns}
{{
    public static class {typeName}
    {{
        [STAThread]
        public static void Main()
        {{
            var app = new Application();

            var root = new Grid();

            var window = new Window
            {{
                Title = ""Dynamic App"",
                Width = 900,
                Height = 600,
                Content = root,
            }};

            app.Run(window);
        }}
    }}
}}";

    private static string BuildAvaloniaExecutableBaseCode(string ns, string typeName)
        => $@"
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace {ns}
{{
    public sealed class DynamicApp : Application
    {{
        public override void Initialize()
        {{
        }}

        public override void OnFrameworkInitializationCompleted()
        {{
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {{
                desktop.MainWindow = new Window
                {{
                    Title = ""Dynamic App"",
                    Width = 900,
                    Height = 600,
                    Content = new Grid(),
                }};
            }}

            base.OnFrameworkInitializationCompleted();
        }}
    }}

    public static class {typeName}
    {{
        public static void Main(string[] args)
        {{
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }}

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<DynamicApp>().UsePlatformDetect();
    }}
}}";

    public static readonly string WpfBaseCode = @"
using System;
using System.Windows;
using System.Windows.Controls;

namespace Neo.Dynamic
{
    public sealed class DynamicUserControl : UserControl
    {
        public DynamicUserControl()
        {
            Loaded += (_, _) => InitializeComponents();
        }

        public void InitializeComponents()
        {
            Content = new Grid();
        }
    }
}";

    public static readonly string AvaloniaBaseCode = @"
using System;
using Avalonia.Controls;

namespace Neo.Dynamic
{
    public sealed class DynamicUserControl : UserControl
    {
        public DynamicUserControl()
        {
            Initialized += OnInitializedOnce;
        }

        private void OnInitializedOnce(object? sender, EventArgs e)
        {
            Initialized -= OnInitializedOnce;
            InitializeComponents();
        }

        public void InitializeComponents()
        {
            Content = new Grid();
        }
    }
}";
}

public static class AssemblyForgeDefaults
{
    public static AssemblyForgeWorkspace CreateLocalAppDataWorkspace(
        string appName,
        AssemblyForgeUiFramework uiFramework,
        int dotNetMajor = 0,
        string? targetFramework = null,
        string outputDllFileName = "DynamicUserControl.dll",
        string outputExeDirectoryName = "ExeOutput",
        IReadOnlyList<string>? additionalReferenceDllPaths = null)
    {
        if (string.IsNullOrWhiteSpace(appName))
            throw new ArgumentException("Value cannot be null/empty.", nameof(appName));

        if (dotNetMajor == 0) dotNetMajor = Environment.Version.Major;
        targetFramework ??= $"net{dotNetMajor}.0-windows";

        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appName);

        var nugetDir = FileSystemHelper.EnsureDirectory(Path.Combine(baseDir, "NuGetPackages"));
        var outputDllPath = Path.Combine(baseDir, outputDllFileName);
        var outputExeDir = Path.Combine(baseDir, string.IsNullOrWhiteSpace(outputExeDirectoryName) ? "ExeOutput" : outputExeDirectoryName);

        var refs = new List<string>();

        var core = DotNetRuntimeFinder.GetHighestRuntimePath(DotNetRuntimeType.NetCoreApp, dotNetMajor);
        if (!string.IsNullOrWhiteSpace(core))
            refs.Add(core);

        var needsDesktop = uiFramework == AssemblyForgeUiFramework.Wpf ||
                           targetFramework.Contains("-windows", StringComparison.OrdinalIgnoreCase);
        if (needsDesktop)
        {
            var desktop = DotNetRuntimeFinder.GetHighestRuntimePath(DotNetRuntimeType.WindowsDesktopApp, dotNetMajor);
            if (!string.IsNullOrWhiteSpace(desktop))
                refs.Add(desktop);
        }

        if (refs.Count == 0)
            throw new InvalidOperationException("No suitable .NET runtime directories were found via 'dotnet --list-runtimes'.");

        return new AssemblyForgeWorkspace
        {
            NuGetPackageDirectory = nugetDir,
            OutputDllPath = outputDllPath,
            OutputExeDirectory = outputExeDir,
            ReferenceAssemblyDirectories = refs,
            TargetFramework = targetFramework,
            AdditionalReferenceDllPaths = additionalReferenceDllPaths ?? Array.Empty<string>(),
        };
    }
}
