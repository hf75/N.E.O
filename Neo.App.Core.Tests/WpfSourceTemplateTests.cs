using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Neo.App;
using Xunit;

namespace Neo.App.Core.Tests;

/// <summary>
/// The program that wraps an app exported from the WPF host is C# source text as well, with the
/// error window of exported apps in it (Neo.ErrorSurfacing). Compile it as the export does: against
/// the installed .NET and its Windows Desktop runtime.
/// </summary>
public class WpfSourceTemplateTests
{
    [Fact]
    public void Export_program_compiles_against_the_installed_WPF()
    {
        if (!OperatingSystem.IsWindows()) return;

        var core = DotNetRuntimeFinder.GetHighestRuntimePath(DotNetRuntimeType.NetCoreApp, Environment.Version.Major);
        var desktop = DotNetRuntimeFinder.GetHighestRuntimePath(DotNetRuntimeType.WindowsDesktopApp, Environment.Version.Major);
        core.Should().NotBeNull();
        desktop.Should().NotBeNull("WPF comes with the Windows Desktop runtime");

        // As CSharpCompileAgent does for an export: every managed DLL of both runtimes.
        var references = new[] { core!, desktop! }
            .SelectMany(directory => Directory.GetFiles(directory, "*.dll"))
            .Where(IsManaged)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "WpfTemplateTest",
            new[] { UserControlBaseCode.BaseCode, ExportWindowBaseCode.CreateBaseCodeForExport("Template test") }.Select(s => CSharpSyntaxTree.ParseText(s)),
            references,
            new CSharpCompilationOptions(OutputKind.WindowsApplication, allowUnsafe: true, mainTypeName: "Neo.App.App"));

        compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())
            .Should().BeEmpty();
    }

    private static bool IsManaged(string path)
    {
        try
        {
            System.Reflection.AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}
