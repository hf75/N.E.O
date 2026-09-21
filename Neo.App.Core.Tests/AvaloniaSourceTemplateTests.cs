using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Neo.App;
using Xunit;

namespace Neo.App.Core.Tests;

/// <summary>
/// The desktop hosts carry Avalonia code as C# source text: the empty UserControl every session
/// starts from and the program that wraps an exported app. Roslyn compiles both on the user's
/// machine, so a break — an Avalonia upgrade that removes an API they use — would only show up
/// there. Compile them here, against the Avalonia the plugin window ships.
/// </summary>
public class AvaloniaSourceTemplateTests
{
    [Fact]
    public void Base_UserControl_compiles_against_the_shipped_Avalonia()
    {
        var errors = Compile(OutputKind.DynamicallyLinkedLibrary, UserControlBaseCodeAvalonia.BaseCode);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Export_program_compiles_against_the_shipped_Avalonia()
    {
        var errors = Compile(OutputKind.WindowsApplication,
            UserControlBaseCodeAvalonia.BaseCode,
            ExportWindowBaseCode.CreateBaseCodeForExportAvalonia("Template test"));

        errors.Should().BeEmpty();
    }

    private static List<string> Compile(OutputKind kind, params string[] sources)
    {
        var avaloniaDir = PluginWindowBinDir;
        Directory.Exists(avaloniaDir).Should().BeTrue($"the Avalonia plugin window has to be built: {avaloniaDir}");

        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var references = tpa
            .Concat(Directory.GetFiles(avaloniaDir, "Avalonia*.dll"))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => MetadataReference.CreateFromFile(g.First()));

        var compilation = CSharpCompilation.Create(
            "AvaloniaTemplateTest",
            sources.Select(s => CSharpSyntaxTree.ParseText(s)),
            references,
            new CSharpCompilationOptions(kind, allowUnsafe: true));

        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
    }

    // <repo>/Neo.App.Core.Tests/bin/<Configuration>/<tfm>/  →  <repo>/Neo.PluginWindowAvalonia/bin/<Configuration>/<tfm>/
    private static string PluginWindowBinDir
    {
        get
        {
            var testBin = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var tfm = Path.GetFileName(testBin);
            var configuration = Path.GetFileName(Path.GetDirectoryName(testBin)!);
            var repoRoot = Path.GetFullPath(Path.Combine(testBin, "..", "..", "..", ".."));
            return Path.Combine(repoRoot, "Neo.PluginWindowAvalonia", "bin", configuration, tfm);
        }
    }
}
