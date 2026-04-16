using FluentAssertions;
using Xunit;

namespace Neo.AssemblyForge.Tests;

public class AssemblyForgeSystemMessagesTests
{
    #region GetSystemMessage

    [Fact]
    public void GetSystemMessage_Wpf_ReturnsNonEmptyWpfMessage()
    {
        var message = AssemblyForgeSystemMessages.GetSystemMessage(
            AssemblyForgeUiFramework.Wpf, useReact: false, usePython: false);

        message.Should().NotBeNullOrWhiteSpace();
        message.Should().Contain("WPF");
    }

    [Fact]
    public void GetSystemMessage_Avalonia_ReturnsAvaloniaMessage()
    {
        var message = AssemblyForgeSystemMessages.GetSystemMessage(
            AssemblyForgeUiFramework.Avalonia, useReact: false, usePython: false);

        message.Should().NotBeNullOrWhiteSpace();
        message.Should().Contain("Avalonia");
    }

    [Fact]
    public void GetSystemMessage_AvaloniaWithReact_ThrowsNotSupportedException()
    {
        var act = () => AssemblyForgeSystemMessages.GetSystemMessage(
            AssemblyForgeUiFramework.Avalonia, useReact: true, usePython: false);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void GetSystemMessage_WpfWithReact_ReturnsReactMessage()
    {
        var message = AssemblyForgeSystemMessages.GetSystemMessage(
            AssemblyForgeUiFramework.Wpf, useReact: true, usePython: false);

        message.Should().NotBeNullOrWhiteSpace();
        message.Should().Contain("React");
        message.Should().Contain("WebView2");
    }

    [Fact]
    public void GetSystemMessage_UsePython_AddsPythonSection()
    {
        var message = AssemblyForgeSystemMessages.GetSystemMessage(
            AssemblyForgeUiFramework.Wpf, useReact: false, usePython: true);

        message.Should().Contain("pythonnet");
        message.Should().Contain("Python");
    }

    [Fact]
    public void GetSystemMessage_WpfNoPython_DoesNotContainPythonSection()
    {
        var message = AssemblyForgeSystemMessages.GetSystemMessage(
            AssemblyForgeUiFramework.Wpf, useReact: false, usePython: false);

        message.Should().NotContain("pythonnet");
    }

    [Fact]
    public void GetSystemMessage_ContainsJsonSchemaReference()
    {
        var message = AssemblyForgeSystemMessages.GetSystemMessage(
            AssemblyForgeUiFramework.Wpf, useReact: false, usePython: false);

        message.Should().Contain("JSON schema");
    }

    #endregion

    #region GetExecutableSystemMessage

    [Fact]
    public void GetExecutableSystemMessage_Wpf_ContainsMainTypeName()
    {
        var message = AssemblyForgeSystemMessages.GetExecutableSystemMessage(
            AssemblyForgeUiFramework.Wpf, useReact: false, usePython: false,
            mainTypeName: "Neo.Dynamic.DynamicProgram");

        message.Should().NotBeNullOrWhiteSpace();
        message.Should().Contain("Neo.Dynamic.DynamicProgram");
        message.Should().Contain("Main method");
    }

    [Fact]
    public void GetExecutableSystemMessage_Avalonia_ReturnsAvaloniaMessage()
    {
        var message = AssemblyForgeSystemMessages.GetExecutableSystemMessage(
            AssemblyForgeUiFramework.Avalonia, useReact: false, usePython: false,
            mainTypeName: "Neo.Dynamic.DynamicProgram");

        message.Should().NotBeNullOrWhiteSpace();
        message.Should().Contain("Avalonia");
    }

    [Fact]
    public void GetExecutableSystemMessage_AvaloniaWithReact_ThrowsNotSupportedException()
    {
        var act = () => AssemblyForgeSystemMessages.GetExecutableSystemMessage(
            AssemblyForgeUiFramework.Avalonia, useReact: true, usePython: false,
            mainTypeName: "Neo.Dynamic.DynamicProgram");

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void GetExecutableSystemMessage_UsePython_AddsPythonSection()
    {
        var message = AssemblyForgeSystemMessages.GetExecutableSystemMessage(
            AssemblyForgeUiFramework.Wpf, useReact: false, usePython: true,
            mainTypeName: "Neo.Dynamic.DynamicProgram");

        message.Should().Contain("pythonnet");
    }

    [Fact]
    public void GetExecutableSystemMessage_ContainsExeReferences()
    {
        var message = AssemblyForgeSystemMessages.GetExecutableSystemMessage(
            AssemblyForgeUiFramework.Wpf, useReact: false, usePython: false,
            mainTypeName: "MyApp.Main");

        message.Should().Contain("standalone");
        message.Should().Contain("Main method");
    }

    #endregion

    #region GetPatchReviewerSystemMessage

    [Fact]
    public void GetPatchReviewerSystemMessage_ReturnsNonEmptyString()
    {
        var message = AssemblyForgeSystemMessages.GetPatchReviewerSystemMessage();

        message.Should().NotBeNullOrWhiteSpace();
        message.Should().Contain("reviewer");
    }

    #endregion
}
