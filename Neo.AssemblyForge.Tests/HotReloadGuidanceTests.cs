using FluentAssertions;
using Xunit;

namespace Neo.AssemblyForge.Tests;

/// <summary>Hot reload keeps the user's state only for code that follows its conventions; the prompts have to say so.</summary>
public class HotReloadGuidanceTests
{
    [Theory]
    [InlineData(AssemblyForgeUiFramework.Avalonia)]
    [InlineData(AssemblyForgeUiFramework.Wpf)]
    public void UserControl_prompts_teach_code_that_survives_live_updates(AssemblyForgeUiFramework framework)
    {
        var message = AssemblyForgeSystemMessages.GetSystemMessage(framework, useReact: false, usePython: false);

        message.Should().Contain("LIVE UPDATES");
        message.Should().Contain(HotReloadGuidance.PreferredUiMethod + "()");
        message.Should().Contain("InitializeComponents()", "the desktop base code builds its UI there");
    }

    [Theory]
    [InlineData(AssemblyForgeUiFramework.Avalonia)]
    [InlineData(AssemblyForgeUiFramework.Wpf)]
    public void UserControl_prompts_warn_about_looking_up_controls_by_name(AssemblyForgeUiFramework framework)
    {
        // Models write this to reach controls the UI method rebuilds. It finds nothing and throws nothing:
        // a countdown tool showed an empty window.
        var message = AssemblyForgeSystemMessages.GetSystemMessage(framework, useReact: false, usePython: false);

        message.Should().Contain("FindControl").And.Contain("FindName").And.Contain("null");
    }

    [Theory]
    [InlineData(AssemblyForgeUiFramework.Avalonia)]
    [InlineData(AssemblyForgeUiFramework.Wpf)]
    public void UserControl_prompts_say_where_an_addition_reaches_the_running_app(AssemblyForgeUiFramework framework)
    {
        // A shark added to a running aquarium was created in the method that had filled the tank at start. That method
        // never ran again, so the change arrived without its shark: 2 of 5 real changes showed it, 4 of 5 with this note.
        var message = AssemblyForgeSystemMessages.GetSystemMessage(framework, useReact: false, usePython: false);

        message.Should().Contain("field initializer").And.Contain("does not run again");
    }

    [Fact]
    public void Prompts_without_a_patched_UserControl_do_not_get_the_rules()
    {
        AssemblyForgeSystemMessages.GetSystemMessage(AssemblyForgeUiFramework.Wpf, useReact: true, usePython: false)
            .Should().NotContain("LIVE UPDATES", "a React UI lives in the web view");
        AssemblyForgeSystemMessages.GetExecutableSystemMessage(AssemblyForgeUiFramework.Avalonia, useReact: false, usePython: false, mainTypeName: "Program")
            .Should().NotContain("LIVE UPDATES", "a standalone executable is never patched while running");
    }
}
