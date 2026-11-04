using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Neo.ErrorSurfacing.Tests;

/// <summary>
/// The error window of exported apps, as the export programs use it: inserted into their namespace
/// after their own types, <c>Install()</c> before the app runs, <c>MainWindow(…)</c> for the main
/// window. The programs themselves are compiled in Neo.App.Core.Tests (desktop hosts) and by the
/// export tests of Neo.McpServer.Tests, which also run it (ExportedErrorWindowTests).
/// </summary>
public class ExportErrorsTests
{
    [Fact]
    public void The_Avalonia_window_compiles_inside_a_program()
    {
        Errors(TestCompiler.Compilation(Program("global::Avalonia.Controls.Window", ExportErrors.AvaloniaSource))).Should().BeEmpty();
    }

    [Fact]
    public void The_WPF_window_compiles_inside_a_program()
    {
        if (TestCompiler.WindowsDesktopDirectory == null) return;   // WPF is there on Windows only

        Errors(TestCompiler.WpfCompilation(Program("global::System.Windows.Window", ExportErrors.WpfSource))).Should().BeEmpty();
    }

    [Fact]
    public void Both_windows_say_the_same()
    {
        // One wording for the person using an exported app, whichever framework it was built with.
        string[] texts =
        [
            "\"An unexpected error occurred\"",
            "\"The application may be able to continue after this error.\"",
            "\"The application cannot recover from this error and will now exit.\"",
            "\" more errors since this window opened.\"",
            "\"Close\"",
        ];
        foreach (var text in texts)
        {
            ExportErrors.AvaloniaSource.Should().Contain(text);
            ExportErrors.WpfSource.Should().Contain(text);
        }
    }

    private static string Program(string window, string errorWindow) => $$"""
        namespace ExportProgram
        {
            public static class Program
            {
                public static void Start()
                {
                    NeoErrorWindow.Install();
                    {{window}} main = NeoErrorWindow.MainWindow(() => new {{window}}());
                }
            }

        {{errorWindow}}
        }
        """;

    private static IEnumerable<string> Errors(CSharpCompilation compilation)
        => compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString());
}
