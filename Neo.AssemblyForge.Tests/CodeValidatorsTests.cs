using FluentAssertions;
using Xunit;

namespace Neo.AssemblyForge.Tests;

public class CodeValidatorsTests
{
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
