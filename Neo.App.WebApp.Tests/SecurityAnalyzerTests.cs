using Neo.App.WebApp.Services.Compilation;

namespace Neo.App.WebApp.Tests;

public class SecurityAnalyzerTests
{
    [Fact]
    public void CleanCode_HasNoFindings()
    {
        var src = """
            using Avalonia.Controls;
            public class X : UserControl
            {
                public X() => Content = new TextBlock { Text = "hi" };
            }
            """;
        var findings = SecurityAnalyzer.Analyze(src);
        Assert.Empty(findings);
    }

    [Fact]
    public void File_IO_IsFlagged()
    {
        var src = """
            public class X { public X() { var s = System.IO.File.ReadAllText("/x"); } }
            """;
        var findings = SecurityAnalyzer.Analyze(src);
        Assert.Contains(findings, f => f.Rule == "ForbiddenType" && f.Message.Contains("System.IO.File"));
    }

    [Fact]
    public void Process_Start_IsFlagged()
    {
        var src = """
            public class X { public X() { System.Diagnostics.Process.Start("cmd.exe"); } }
            """;
        var findings = SecurityAnalyzer.Analyze(src);
        Assert.Contains(findings, f => f.Rule == "ForbiddenType");
    }

    [Fact]
    public void DllImport_IsFlagged()
    {
        var src = """
            using System.Runtime.InteropServices;
            public class X { [DllImport("kernel32")] static extern void Foo(); }
            """;
        var findings = SecurityAnalyzer.Analyze(src);
        Assert.Contains(findings, f => f.Rule == "PInvoke");
    }

    [Fact]
    public void UnsafeBlock_IsFlagged()
    {
        var src = """
            public class X { public X() { unsafe { int* p = null; } } }
            """;
        var findings = SecurityAnalyzer.Analyze(src);
        Assert.Contains(findings, f => f.Rule == "Unsafe");
    }

    [Fact]
    public void WpfNamespace_IsFlagged()
    {
        var src = """
            using System.Windows;
            public class X { }
            """;
        var findings = SecurityAnalyzer.Analyze(src);
        Assert.Contains(findings, f => f.Rule == "ForbiddenNamespace");
    }
}
