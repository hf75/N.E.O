using FluentAssertions;
using Xunit;

namespace Neo.AssemblyForge.Tests;

public class AssemblyForgeTemplatesTests
{
    [Fact]
    public void GetBaseCode_Wpf_ContainsSystemWindows()
    {
        var code = AssemblyForgeTemplates.GetBaseCode(AssemblyForgeUiFramework.Wpf);

        code.Should().Contain("System.Windows");
    }

    [Fact]
    public void GetBaseCode_Wpf_ContainsDynamicUserControl()
    {
        var code = AssemblyForgeTemplates.GetBaseCode(AssemblyForgeUiFramework.Wpf);

        code.Should().Contain("DynamicUserControl");
        code.Should().Contain("UserControl");
    }

    [Fact]
    public void GetBaseCode_Avalonia_ContainsAvaloniaReferences()
    {
        var code = AssemblyForgeTemplates.GetBaseCode(AssemblyForgeUiFramework.Avalonia);

        code.Should().Contain("Avalonia");
        code.Should().Contain("DynamicUserControl");
    }

    [Fact]
    public void GetBaseCode_Avalonia_ContainsAvaloniaControls()
    {
        var code = AssemblyForgeTemplates.GetBaseCode(AssemblyForgeUiFramework.Avalonia);

        code.Should().Contain("Avalonia.Controls");
    }

    [Fact]
    public void GetExecutableBaseCode_Wpf_ContainsStaticMainAndNamespace()
    {
        var code = AssemblyForgeTemplates.GetExecutableBaseCode(
            AssemblyForgeUiFramework.Wpf, "Neo.App");

        code.Should().Contain("static void Main");
        code.Should().Contain("namespace Neo");
    }

    [Fact]
    public void GetExecutableBaseCode_Wpf_ContainsSystemWindows()
    {
        var code = AssemblyForgeTemplates.GetExecutableBaseCode(
            AssemblyForgeUiFramework.Wpf, "Neo.Dynamic.DynamicProgram");

        code.Should().Contain("System.Windows");
        code.Should().Contain("Window");
    }

    [Fact]
    public void GetExecutableBaseCode_Avalonia_ContainsAvaloniaReferences()
    {
        var code = AssemblyForgeTemplates.GetExecutableBaseCode(
            AssemblyForgeUiFramework.Avalonia, "Neo.App");

        code.Should().Contain("Avalonia");
        code.Should().Contain("static void Main");
    }

    [Fact]
    public void GetExecutableBaseCode_Avalonia_ContainsAppBuilder()
    {
        var code = AssemblyForgeTemplates.GetExecutableBaseCode(
            AssemblyForgeUiFramework.Avalonia, "Neo.Dynamic.DynamicProgram");

        code.Should().Contain("AppBuilder");
        code.Should().Contain("DynamicApp");
    }

    [Fact]
    public void GetExecutableBaseCode_NullMainTypeName_UsesDefault()
    {
        var code = AssemblyForgeTemplates.GetExecutableBaseCode(
            AssemblyForgeUiFramework.Wpf, null!);

        code.Should().Contain("DynamicProgram");
        code.Should().Contain("Neo.Dynamic");
    }

    [Fact]
    public void GetExecutableBaseCode_EmptyMainTypeName_UsesDefault()
    {
        var code = AssemblyForgeTemplates.GetExecutableBaseCode(
            AssemblyForgeUiFramework.Wpf, "");

        code.Should().Contain("DynamicProgram");
        code.Should().Contain("Neo.Dynamic");
    }

    [Fact]
    public void GetExecutableBaseCode_WhitespaceMainTypeName_UsesDefault()
    {
        var code = AssemblyForgeTemplates.GetExecutableBaseCode(
            AssemblyForgeUiFramework.Wpf, "   ");

        code.Should().Contain("DynamicProgram");
    }

    [Fact]
    public void GetExecutableBaseCode_CustomNamespace_UsesCustom()
    {
        var code = AssemblyForgeTemplates.GetExecutableBaseCode(
            AssemblyForgeUiFramework.Wpf, "MyApp.Launcher");

        code.Should().Contain("namespace MyApp");
        code.Should().Contain("class Launcher");
    }

    [Fact]
    public void WpfBaseCode_StaticField_IsNotEmpty()
    {
        AssemblyForgeTemplates.WpfBaseCode.Should().NotBeNullOrWhiteSpace();
        AssemblyForgeTemplates.WpfBaseCode.Should().Contain("UserControl");
    }

    [Fact]
    public void AvaloniaBaseCode_StaticField_IsNotEmpty()
    {
        AssemblyForgeTemplates.AvaloniaBaseCode.Should().NotBeNullOrWhiteSpace();
        AssemblyForgeTemplates.AvaloniaBaseCode.Should().Contain("UserControl");
    }
}
