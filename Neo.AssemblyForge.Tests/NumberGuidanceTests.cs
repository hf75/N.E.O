using System.Globalization;
using FluentAssertions;
using Xunit;

namespace Neo.AssemblyForge.Tests;

/// <summary>A tool runs with the number format of the PC it is on: every prompt says how to read numbers there.</summary>
public class NumberGuidanceTests
{
    [Theory]
    [InlineData(AssemblyForgeUiFramework.Wpf, false)]
    [InlineData(AssemblyForgeUiFramework.Wpf, true)]
    [InlineData(AssemblyForgeUiFramework.Avalonia, false)]
    public void Every_prompt_for_generated_code_says_how_to_read_numbers(AssemblyForgeUiFramework framework, bool useReact)
    {
        AssemblyForgeSystemMessages.GetSystemMessage(framework, useReact, usePython: false).Should().Contain(NumberGuidance.Rule);
        AssemblyForgeSystemMessages.GetExecutableSystemMessage(framework, useReact, usePython: false, mainTypeName: "Program").Should().Contain(NumberGuidance.Rule);
    }

    // What the rule says, step by step.
    private static double ReadTheWayTheRuleSays(string text)
    {
        int comma = text.LastIndexOf(','), point = text.LastIndexOf('.');
        if (comma >= 0 && point >= 0)
            text = comma > point ? text.Replace(".", "").Replace(',', '.') : text.Replace(",", "");
        else
            text = text.Replace(',', '.');
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("1,5", 1.5)]
    [InlineData("1.234,5", 1234.5)]
    [InlineData("1,234.5", 1234.5)]
    [InlineData("1.08", 1.08)]
    [InlineData("250", 250)]
    public void What_the_rule_promises_holds_on_every_PC(string text, double number)
    {
        foreach (var culture in new[] { "de-DE", "en-US", "fr-FR" })
        {
            var before = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            try { ReadTheWayTheRuleSays(text).Should().Be(number); }
            finally { CultureInfo.CurrentCulture = before; }
        }
    }

    [Fact]
    public void What_it_warns_of_is_true()
    {
        var german = CultureInfo.GetCultureInfo("de-DE");

        decimal.Parse("1.08", NumberStyles.Number, german).Should().Be(108, "what a tool built by the model did on a German PC");
        double.TryParse("1.08", NumberStyles.Float, german, out _).Should().BeFalse("the other way it went wrong");
    }
}
