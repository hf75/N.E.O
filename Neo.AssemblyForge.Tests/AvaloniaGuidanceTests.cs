using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Neo.AssemblyForge.Tests;

/// <summary>
/// The Avalonia version lives in one place (NeoAvaloniaVersion in Directory.Build.props). These
/// tests make sure the build really stamps it into the assemblies and that every Avalonia prompt
/// names that version — and only that one.
/// </summary>
public class AvaloniaGuidanceTests
{
    [Fact]
    public void Version_comes_from_the_build()
    {
        AvaloniaGuidance.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+$",
            "the NeoAvaloniaVersion assembly metadata must be stamped by Directory.Build.props");
        int.Parse(AvaloniaGuidance.MajorVersion).Should().BeGreaterThanOrEqualTo(12);
    }

    [Fact]
    public void UserControl_prompt_pins_the_shipped_version_and_no_other()
    {
        var message = AssemblyForgeSystemMessages.GetSystemMessage(
            AssemblyForgeUiFramework.Avalonia, useReact: false, usePython: false);

        message.Should().Contain("you must always use version " + AvaloniaGuidance.Version);
        OtherAvaloniaVersionsIn(message).Should().BeEmpty();
    }

    [Fact]
    public void Executable_prompt_pins_the_shipped_version_and_no_other()
    {
        var message = AssemblyForgeSystemMessages.GetExecutableSystemMessage(
            AssemblyForgeUiFramework.Avalonia, useReact: false, usePython: false, mainTypeName: "Program");

        message.Should().Contain("Always use version " + AvaloniaGuidance.Version);
        OtherAvaloniaVersionsIn(message).Should().BeEmpty();
    }

    [Fact]
    public void Avalonia_prompts_explain_the_api_changes_and_the_web_view()
    {
        var message = AssemblyForgeSystemMessages.GetSystemMessage(
            AssemblyForgeUiFramework.Avalonia, useReact: false, usePython: false);

        message.Should().Contain("IsCheckedChanged");
        message.Should().Contain("PlaceholderText");
        message.Should().Contain("StorageProvider");
        message.Should().Contain("NativeWebView");
    }

    [Fact]
    public void Wpf_prompt_stays_free_of_Avalonia_guidance()
    {
        var message = AssemblyForgeSystemMessages.GetSystemMessage(
            AssemblyForgeUiFramework.Wpf, useReact: false, usePython: false);

        message.Should().NotContain("NativeWebView");
        message.Should().NotContain("IsCheckedChanged");
    }

    [Fact]
    public void Markdown_list_has_one_line_per_entry()
    {
        var markdown = AvaloniaGuidance.AsMarkdownList(["first", "second"]);
        markdown.Should().Be("- first\n- second\n");
    }

    // Version-looking numbers that are not the shipped one ("11.3.12" would be a leftover pin).
    // "Avalonia 11" without a patch level is fine: the notes talk about the previous major.
    private static IEnumerable<string> OtherAvaloniaVersionsIn(string text)
        => Regex.Matches(text, @"\b1[0-9]\.\d+\.\d+\b")
            .Select(m => m.Value)
            .Where(v => v != AvaloniaGuidance.Version)
            .Distinct();
}
