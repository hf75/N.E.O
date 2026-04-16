using FluentAssertions;
using Neo.App;
using Xunit;

namespace Neo.App.Core.Tests;

public class RoslynCodeAnalyzerTests
{
    // ── ExtractSignatures ───────────────────────────────────────────

    [Fact]
    public void ExtractSignatures_EmptyCode_ReturnsEmptyList()
    {
        var result = RoslynCodeAnalyzer.ExtractSignatures("");

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractSignatures_NullCode_ReturnsEmptyList()
    {
        var result = RoslynCodeAnalyzer.ExtractSignatures(null!);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractSignatures_ClassDeclaration_ExtractsClassSignature()
    {
        var code = "public class MyClass { }";

        var result = RoslynCodeAnalyzer.ExtractSignatures(code);

        result.Should().Contain(s => s.Contains("class") && s.Contains("MyClass"));
    }

    [Fact]
    public void ExtractSignatures_MethodDeclaration_ExtractsMethodSignature()
    {
        var code = """
            public class MyClass {
                public void DoStuff(int x) { }
            }
            """;

        var result = RoslynCodeAnalyzer.ExtractSignatures(code);

        result.Should().Contain(s => s.Contains("DoStuff") && s.Contains("int x"));
    }

    [Fact]
    public void ExtractSignatures_PropertyDeclaration_ExtractsPropertySignature()
    {
        var code = """
            public class MyClass {
                public string Name { get; set; }
            }
            """;

        var result = RoslynCodeAnalyzer.ExtractSignatures(code);

        result.Should().Contain(s => s.Contains("string") && s.Contains("Name"));
    }

    [Fact]
    public void ExtractSignatures_ConstructorDeclaration_ExtractsConstructorSignature()
    {
        var code = """
            public class MyClass {
                public MyClass(int id) { }
            }
            """;

        var result = RoslynCodeAnalyzer.ExtractSignatures(code);

        result.Should().Contain(s => s.Contains("MyClass") && s.Contains("int id"));
    }

    [Fact]
    public void ExtractSignatures_ClassWithBaseType_IncludesBaseList()
    {
        var code = "public class DynamicUserControl : UserControl { }";

        var result = RoslynCodeAnalyzer.ExtractSignatures(code);

        result.Should().Contain(s => s.Contains("DynamicUserControl") && s.Contains("UserControl"));
    }

    [Fact]
    public void ExtractSignatures_MultipleMembers_ExtractsAll()
    {
        var code = """
            public class Foo {
                public int X { get; set; }
                public void Bar() { }
                public string Baz(double d) { return ""; }
            }
            """;

        var result = RoslynCodeAnalyzer.ExtractSignatures(code);

        // Class + 1 property + 2 methods = at least 4 signatures.
        result.Count.Should().BeGreaterThanOrEqualTo(4);
    }

    // ── ApplyPatches ────────────────────────────────────────────────

    [Fact]
    public void ApplyPatches_NullPatches_ReturnsOriginal()
    {
        var code = "class C { }";

        var result = RoslynCodeAnalyzer.ApplyPatches(code, null!);

        result.Should().Contain("class C");
    }

    [Fact]
    public void ApplyPatches_EmptyPatches_ReturnsOriginal()
    {
        var code = "class C { }";

        var result = RoslynCodeAnalyzer.ApplyPatches(code, new List<PatchOperation>());

        result.Should().Contain("class C");
    }

    [Fact]
    public void ApplyPatches_DeleteMethod_RemovesMethod()
    {
        var code = """
            public class MyClass
            {
                public void Keep() { }
                public void Remove() { }
            }
            """;

        var patches = new List<PatchOperation>
        {
            new PatchOperation
            {
                Operation = "DELETE",
                Signature = "public void Remove()",
            }
        };

        var result = RoslynCodeAnalyzer.ApplyPatches(code, patches);

        result.Should().Contain("Keep");
        result.Should().NotContain("Remove");
    }

    [Fact]
    public void ApplyPatches_ReplaceMethod_ReplacesMethodBody()
    {
        // The code must not have leading whitespace to avoid signature extraction differences.
        var code = @"public class MyClass
{
    public void Target() { var old = 1; }
}";

        // First extract the actual signature to ensure the test matches the analyzer's format.
        var signatures = RoslynCodeAnalyzer.ExtractSignatures(code);
        var targetSig = signatures.First(s => s.Contains("Target"));

        // NewContent must be parseable as a full class member by Roslyn.
        // Wrap it so CSharpSyntaxTree.ParseText can find a MethodDeclarationSyntax.
        var patches = new List<PatchOperation>
        {
            new PatchOperation
            {
                Operation = "REPLACE",
                Signature = targetSig,
                NewContent = @"class Wrapper { public void Target() { var newCode = 42; } }",
            }
        };

        var result = RoslynCodeAnalyzer.ApplyPatches(code, patches);

        result.Should().Contain("newCode");
    }

    [Fact]
    public void ApplyPatches_AddMember_AddsToClass()
    {
        var code = """
            public class MyClass
            {
                public void Existing() { }
            }
            """;

        var patches = new List<PatchOperation>
        {
            new PatchOperation
            {
                Operation = "ADD",
                ParentSignature = "public class MyClass",
                NewContent = "public int NewProp { get; set; }",
            }
        };

        var result = RoslynCodeAnalyzer.ApplyPatches(code, patches);

        result.Should().Contain("NewProp");
        result.Should().Contain("Existing");
    }

    // ── Signature format ────────────────────────────────────────────

    [Fact]
    public void ExtractSignatures_StaticMethod_IncludesStaticModifier()
    {
        var code = """
            public class C {
                public static void StaticMethod() { }
            }
            """;

        var result = RoslynCodeAnalyzer.ExtractSignatures(code);

        result.Should().Contain(s => s.Contains("static") && s.Contains("StaticMethod"));
    }

    [Fact]
    public void ExtractSignatures_PrivateMethod_IncludesPrivateModifier()
    {
        var code = """
            public class C {
                private int Secret() { return 0; }
            }
            """;

        var result = RoslynCodeAnalyzer.ExtractSignatures(code);

        result.Should().Contain(s => s.Contains("private") && s.Contains("Secret"));
    }
}
