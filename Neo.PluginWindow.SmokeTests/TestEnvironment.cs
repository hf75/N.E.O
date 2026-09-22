using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Neo.PluginWindow.SmokeTests;

/// <summary>Locates the built plugin window heads and compiles the tiny plugins the tests load.</summary>
internal static class TestEnvironment
{
    // <repo>/Neo.PluginWindow.SmokeTests/bin/<Configuration>/<tfm>-windows/
    private static readonly string TestBinDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private static readonly string WindowsTfm = Path.GetFileName(TestBinDir);
    private static readonly string Configuration = Path.GetFileName(Path.GetDirectoryName(TestBinDir)!);
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(TestBinDir, "..", "..", "..", ".."));

    private static string PortableTfm => WindowsTfm.Replace("-windows", "", StringComparison.OrdinalIgnoreCase);

    public static string HeadBinDir(string headProject)
    {
        var tfm = headProject.Contains("WPF", StringComparison.Ordinal) ? WindowsTfm : PortableTfm;
        return Path.Combine(RepoRoot, headProject, "bin", Configuration, tfm);
    }

    public static string HeadExe(string headProject) => Path.Combine(HeadBinDir(headProject), headProject + ".exe");

    /// <summary>The built desktop host: "Neo.App" (WPF) or "Neo.App.Avalonia".</summary>
    public static string HostExe(string hostProject)
    {
        var tfm = hostProject == "Neo.App" ? WindowsTfm : PortableTfm;
        return Path.Combine(RepoRoot, hostProject, "bin", Configuration, tfm, hostProject + ".exe");
    }

    private static readonly Lazy<string> AvaloniaPluginDll = new(() => Compile(
        "avalonia",
        """
        using Avalonia;
        using Avalonia.Controls;

        public class DynamicUserControl : UserControl
        {
            public DynamicUserControl()
            {
                Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Children =
                    {
                        new TextBlock { Name = "label", Text = "Plugin window smoke test (Avalonia)", FontSize = 20 },
                        new Button { Name = "okButton", Content = "OK" }
                    }
                };
            }
        }
        """,
        Directory.GetFiles(HeadBinDir("Neo.PluginWindowAvalonia"), "Avalonia*.dll")));

    /// <summary>Environment variable the web view plugin reads: where to write what the page sent back.</summary>
    public const string WebViewMarkerVariable = "NEO_SMOKE_WEBVIEW_MARKER";

    /// <summary>
    /// A plugin around the official <c>NativeWebView</c>: it renders a page from a string, the page
    /// calls back into C#, and the handler writes the message to the marker file. If that file
    /// appears, the whole chain works inside the collectible plugin context of a real child.
    /// </summary>
    public static string AvaloniaWebViewPlugin => AvaloniaWebViewPluginDll.Value;

    private static readonly Lazy<string> AvaloniaWebViewPluginDll = new(() => Compile(
        "avalonia_webview",
        """
        using System;
        using System.IO;
        using Avalonia.Controls;

        public class DynamicUserControl : UserControl
        {
            public DynamicUserControl()
            {
                var web = new NativeWebView();
                web.WebMessageReceived += (_, e) =>
                {
                    var marker = Environment.GetEnvironmentVariable("NEO_SMOKE_WEBVIEW_MARKER");
                    if (!string.IsNullOrEmpty(marker))
                        File.WriteAllText(marker, e.Body ?? "");
                };
                web.NavigateToString(
                    "<html><body><h1>N.E.O. web view smoke test</h1>" +
                    "<script>invokeCSharpAction('hello from the page');</script></body></html>");
                Content = web;
            }
        }
        """,
        Directory.GetFiles(HeadBinDir("Neo.PluginWindowAvalonia"), "Avalonia*.dll")));

    private static readonly Lazy<string> WpfPluginDll = new(() => Compile(
        "wpf",
        """
        using System.Windows;
        using System.Windows.Controls;

        public class DynamicUserControl : UserControl
        {
            public DynamicUserControl()
            {
                var panel = new StackPanel { Margin = new Thickness(16) };
                panel.Children.Add(new TextBlock { Name = "label", Text = "Plugin window smoke test (WPF)", FontSize = 20 });
                panel.Children.Add(new Button { Name = "okButton", Content = "OK" });
                Content = panel;
            }
        }
        """,
        Array.Empty<string>()));

    public static string PluginFor(string headProject)
        => headProject.Contains("WPF", StringComparison.Ordinal) ? WpfPluginDll.Value : AvaloniaPluginDll.Value;

    /// <summary>
    /// Compiles a plugin against this process' trusted platform assemblies (framework + WPF,
    /// thanks to UseWPF) plus the given extra references — the same kind of DLL the hosts
    /// produce with Roslyn at runtime.
    /// </summary>
    private static string Compile(string flavour, string source, IEnumerable<string> extraReferences)
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var references = tpa.Concat(extraReferences)
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => MetadataReference.CreateFromFile(g.First()));

        var compilation = CSharpCompilation.Create(
            assemblyName: "DynamicUserControl",
            syntaxTrees: new[] { CSharpSyntaxTree.ParseText(source) },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var outDir = Path.Combine(Path.GetTempPath(), "neo_smoke_plugins", flavour + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "DynamicUserControl.dll");

        using (var fs = File.Create(outPath))
        {
            var result = compilation.Emit(fs);
            if (!result.Success)
                throw new InvalidOperationException("Smoke-test plugin did not compile:\n" +
                    string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }
        return outPath;
    }
}
