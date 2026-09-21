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

    [Fact]
    public void Prompts_without_a_patched_UserControl_do_not_get_the_rules()
    {
        AssemblyForgeSystemMessages.GetSystemMessage(AssemblyForgeUiFramework.Wpf, useReact: true, usePython: false)
            .Should().NotContain("LIVE UPDATES", "a React UI lives in the web view");
        AssemblyForgeSystemMessages.GetExecutableSystemMessage(AssemblyForgeUiFramework.Avalonia, useReact: false, usePython: false, mainTypeName: "Program")
            .Should().NotContain("LIVE UPDATES", "a standalone executable is never patched while running");
    }
}
