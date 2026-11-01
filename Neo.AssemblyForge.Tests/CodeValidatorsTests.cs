using FluentAssertions;
using Xunit;

namespace Neo.AssemblyForge.Tests;

public class CodeValidatorsTests
{
    #region NewMethodsNothingCalls

    private const string Before = """
        public class DynamicUserControl : UserControl
        {
            public DynamicUserControl() { BuildUi(); }
            private void BuildUi() { }
            private void OldAndUnused() { }
        }
        """;

    // The control with this body added, the way a patch would.
    private static string With(string members) => Before.Replace("private void OldAndUnused() { }", "private void OldAndUnused() { }\n" + members);

    private static IReadOnlyList<string> Unused(string members, params string[] otherSources)
    {
        var after = With(members);
        return CodeValidators.NewMethodsNothingCalls(Before, after, otherSources.Append(after));
    }

    [Theory]
    [InlineData("private Control Chart() => new Canvas();")]
    [InlineData("Control Chart() => new Canvas();")]
    [InlineData("private static Control Chart() => new Canvas();")]
    [InlineData("private async Task Chart() { await Task.Delay(1); }")]
    public void NewMethodsNothingCalls_NewMethodOnlyThisCodeCouldCall_IsNamed(string member)
    {
        Unused(member).Should().Equal("Chart");
    }

    [Theory]
    [InlineData("private void Use() { Content = Chart(); }")]
    [InlineData("private void Use() { Content = this.Chart(); }")]
    [InlineData("private void Use() { button.Click += Chart; }")]
    [InlineData("private void Use() { Run(Chart); }")]
    [InlineData("private void Use() { Run(() => Chart()); }")]
    [InlineData("private void Use() { Log($\"{Chart()}\"); }")]
    [InlineData("private void Use() { Log(nameof(Chart)); }")]
    public void NewMethodsNothingCalls_AnyWayOfReferringToIt_CountsAsUsed(string user)
    {
        // Use itself may be unused here; the question is about Chart.
        Unused("private Control Chart() => null;\n" + user).Should().NotContain("Chart");
    }

    [Theory]
    [InlineData("public Control Chart() => null;")]
    [InlineData("protected Control Chart() => null;")]
    [InlineData("internal Control Chart() => null;")]
    [InlineData("public override void Chart(DrawingContext c) { }")]
    [InlineData("protected override void Chart(DrawingContext c) { }")]
    [InlineData("[McpCallable(\"chart\")] private Control Chart() => null;")]
    [InlineData("[RelayCommand] private void Chart() { }")]
    [InlineData("void ICommand.Chart(object p) { }")]
    [InlineData("partial void Chart();")]
    [InlineData("static void Main() { }")]
    [InlineData("interface IChart { void Chart(); }")]
    public void NewMethodsNothingCalls_WhatSomethingElseMayCall_DoesNotCount(string member)
    {
        Unused(member).Should().BeEmpty();
    }

    [Fact]
    public void NewMethodsNothingCalls_CalledFromAnotherFile_CountsAsUsed()
    {
        Unused("private Control Chart() => null;", "partial class DynamicUserControl { void Use() { Content = Chart(); } }")
            .Should().BeEmpty();
    }

    [Fact]
    public void NewMethodsNothingCalls_OnlyCallsItself_IsStillNamed()
    {
        Unused("private int Chart(int n) => n == 0 ? 0 : Chart(n - 1);").Should().Equal("Chart");
    }

    [Fact]
    public void NewMethodsNothingCalls_NamedOnlyInACommentOrAString_IsStillNamed()
    {
        Unused("private Control Chart() => null;\nprivate string Hint = \"Chart\"; // Chart()").Should().Equal("Chart");
    }

    [Fact]
    public void NewMethodsNothingCalls_UnusedBeforeTheChange_IsNotThisChangesBusiness()
    {
        CodeValidators.NewMethodsNothingCalls(Before, Before, new[] { Before }).Should().BeEmpty();
    }

    [Fact]
    public void NewMethodsNothingCalls_NewMethodUsedOnlyByAnotherUnusedOne_NamesTheOneToCall()
    {
        Unused("private Control Chart() => Slices();\nprivate Control Slices() => null;").Should().Equal("Chart");
    }

    #endregion

    #region ContainsNamedUserControl

    [Fact]
    public void ContainsNamedUserControl_ClassInheritsUserControl_ReturnsTrue()
    {
        var code = @"
using System.Windows.Controls;
namespace Neo.Dynamic
{
    public class DynamicUserControl : UserControl
    {
    }
}";
        CodeValidators.ContainsNamedUserControl(code, "DynamicUserControl").Should().BeTrue();
    }

    [Fact]
    public void ContainsNamedUserControl_QualifiedBaseType_ReturnsTrue()
    {
        var code = @"
namespace Neo.Dynamic
{
    public class DynamicUserControl : Avalonia.Controls.UserControl
    {
    }
}";
        CodeValidators.ContainsNamedUserControl(code, "DynamicUserControl").Should().BeTrue();
    }

    [Fact]
    public void ContainsNamedUserControl_WrongClassName_ReturnsFalse()
    {
        var code = @"
namespace Neo.Dynamic
{
    public class OtherControl : UserControl
    {
    }
}";
        CodeValidators.ContainsNamedUserControl(code, "DynamicUserControl").Should().BeFalse();
    }

    [Fact]
    public void ContainsNamedUserControl_NoBaseList_ReturnsFalse()
    {
        var code = @"
namespace Neo.Dynamic
{
    public class DynamicUserControl
    {
    }
}";
        CodeValidators.ContainsNamedUserControl(code, "DynamicUserControl").Should().BeFalse();
    }

    [Fact]
    public void ContainsNamedUserControl_NullCode_ReturnsFalse()
    {
        CodeValidators.ContainsNamedUserControl(null!, "DynamicUserControl").Should().BeFalse();
    }

    [Fact]
    public void ContainsNamedUserControl_EmptyCode_ReturnsFalse()
    {
        CodeValidators.ContainsNamedUserControl("", "DynamicUserControl").Should().BeFalse();
    }

    [Fact]
    public void ContainsNamedUserControl_NullClassName_ReturnsFalse()
    {
        CodeValidators.ContainsNamedUserControl("class Foo : UserControl {}", null!).Should().BeFalse();
    }

    [Fact]
    public void ContainsNamedUserControl_EmptyClassName_ReturnsFalse()
    {
        CodeValidators.ContainsNamedUserControl("class Foo : UserControl {}", "").Should().BeFalse();
    }

    [Fact]
    public void ContainsNamedUserControl_MultipleClasses_CorrectOneMatches_ReturnsTrue()
    {
        var code = @"
namespace Neo.Dynamic
{
    public class Helper { }
    public class DynamicUserControl : UserControl { }
    public class AnotherHelper { }
}";
        CodeValidators.ContainsNamedUserControl(code, "DynamicUserControl").Should().BeTrue();
    }

    [Fact]
    public void ContainsNamedUserControl_InheritsNonUserControl_ReturnsFalse()
    {
        var code = @"
namespace Neo.Dynamic
{
    public class DynamicUserControl : Panel
    {
    }
}";
        CodeValidators.ContainsNamedUserControl(code, "DynamicUserControl").Should().BeFalse();
    }

    #endregion

    #region ContainsEntrypoint

    [Fact]
    public void ContainsEntrypoint_StaticMainInCorrectNamespace_ReturnsTrue()
    {
        var code = @"
namespace Neo.Dynamic
{
    public class DynamicProgram
    {
        public static void Main(string[] args) { }
    }
}";
        CodeValidators.ContainsEntrypoint(code, "Neo.Dynamic.DynamicProgram").Should().BeTrue();
    }

    [Fact]
    public void ContainsEntrypoint_MainNotStatic_ReturnsFalse()
    {
        var code = @"
namespace Neo.Dynamic
{
    public class DynamicProgram
    {
        public void Main(string[] args) { }
    }
}";
        CodeValidators.ContainsEntrypoint(code, "Neo.Dynamic.DynamicProgram").Should().BeFalse();
    }

    [Fact]
    public void ContainsEntrypoint_WrongClassName_ReturnsFalse()
    {
        var code = @"
namespace Neo.Dynamic
{
    public class WrongName
    {
        public static void Main(string[] args) { }
    }
}";
        CodeValidators.ContainsEntrypoint(code, "Neo.Dynamic.DynamicProgram").Should().BeFalse();
    }

    [Fact]
    public void ContainsEntrypoint_WrongNamespace_ReturnsFalse()
    {
        var code = @"
namespace Wrong.Namespace
{
    public class DynamicProgram
    {
        public static void Main(string[] args) { }
    }
}";
        CodeValidators.ContainsEntrypoint(code, "Neo.Dynamic.DynamicProgram").Should().BeFalse();
    }

    [Fact]
    public void ContainsEntrypoint_NullCode_ReturnsFalse()
    {
        CodeValidators.ContainsEntrypoint(null!, "Neo.Dynamic.DynamicProgram").Should().BeFalse();
    }

    [Fact]
    public void ContainsEntrypoint_EmptyCode_ReturnsFalse()
    {
        CodeValidators.ContainsEntrypoint("", "Neo.Dynamic.DynamicProgram").Should().BeFalse();
    }

    [Fact]
    public void ContainsEntrypoint_NullMainTypeName_ReturnsFalse()
    {
        CodeValidators.ContainsEntrypoint("class Foo { static void Main() {} }", null!).Should().BeFalse();
    }

    [Fact]
    public void ContainsEntrypoint_EmptyMainTypeName_ReturnsFalse()
    {
        CodeValidators.ContainsEntrypoint("class Foo { static void Main() {} }", "").Should().BeFalse();
    }

    [Fact]
    public void ContainsEntrypoint_NamespaceTypeSplitsCorrectly()
    {
        var code = @"
namespace Neo
{
    public class App
    {
        public static void Main() { }
    }
}";
        CodeValidators.ContainsEntrypoint(code, "Neo.App").Should().BeTrue();
    }

    [Fact]
    public void ContainsEntrypoint_NoNamespaceInTypeName_MatchesAnyNamespace()
    {
        var code = @"
namespace AnyNamespace
{
    public class Program
    {
        public static void Main() { }
    }
}";
        CodeValidators.ContainsEntrypoint(code, "Program").Should().BeTrue();
    }

    [Fact]
    public void ContainsEntrypoint_ParameterlessMain_ReturnsTrue()
    {
        var code = @"
namespace Neo.Dynamic
{
    public class DynamicProgram
    {
        [STAThread]
        public static void Main() { }
    }
}";
        CodeValidators.ContainsEntrypoint(code, "Neo.Dynamic.DynamicProgram").Should().BeTrue();
    }

    #endregion
}
