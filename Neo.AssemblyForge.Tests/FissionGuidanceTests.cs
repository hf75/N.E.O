using FluentAssertions;
using Neo.AssemblyForge;
using Xunit;

namespace Neo.AssemblyForge.Tests;

/// <summary>What the model is told when a part is torn out of a running program, and what the new program is called.</summary>
public class FissionGuidanceTests
{
    [Theory]
    [InlineData("expenseChart", null, "Border", "Expense chart")]
    [InlineData("_totals_panel", null, "Border", "Totals panel")]
    [InlineData(null, "Expenses by category:", "Border", "Expenses by category")]
    [InlineData(null, null, "ListBox", "ListBox")]
    [InlineData("", "  ", "Canvas", "Canvas")]
    public void The_new_program_is_named_by_the_part_or_what_it_shows(string? name, string? text, string type, string expected)
    {
        FissionGuidance.NameFor(name, text, type).Should().Be(expected);
    }

    [Fact]
    public void A_long_text_names_nothing()
    {
        FissionGuidance.NameFor(null, new string('x', 41), "Border").Should().Be("Border");
    }

    [Fact]
    public void The_program_it_came_from_gives_the_part_away_and_offers_its_data()
    {
        var text = FissionGuidance.ForTheSource("Expenses", "Expense chart", "the Border named \"chart\"", "  chart");

        text.Should().Contain("Take that part out of the UI");
        text.Should().Contain("[AppInfo(\"Expenses\"").And.Contain("[McpObservable(").And.Contain("[McpCallable(").And.Contain("Changes = true");
        text.Should().Contain("Keep everything else");
        text.Should().Contain("the Border named \"chart\"").And.Contain("  chart");
    }

    [Fact]
    public void The_new_program_is_the_part_alone_and_gets_its_data_from_the_other()
    {
        var text = FissionGuidance.ForTheNewProgram("Expenses", "Expense chart", "the Border named \"chart\"", "  chart", "class Old { }");

        text.Should().Contain("Write the program \"Expense chart\"");
        text.Should().Contain("Apps.Watch").And.Contain("Apps.CallAsync").And.Contain("RUNNING APPS");
        text.Should().Contain("[AppInfo(\"Expense chart\"");
        text.Should().Contain("class Old { }", "it sees how the part looked and what it did");
        text.Should().Contain("While \"Expenses\" is not running, say so");
    }

    [Fact]
    public void The_chat_names_the_new_program()
    {
        FissionGuidance.ChatPrompt("Expense chart").Should().Be("Tear the part \"Expense chart\" out of this app, as a program of its own.");
    }
}
