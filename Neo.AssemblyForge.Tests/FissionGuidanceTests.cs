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

    // ── Out of another program ──

    [Fact]
    public void The_interface_offers_what_the_part_of_its_program_does_and_may_already_have_it()
    {
        var text = FissionGuidance.ForTheInterface("Stockroom", "Stockroom 2003", "Booking", "the Group \"Booking\"", "  Button \"Book out\"");

        text.Should().Contain("tore one part out of the program \"Stockroom 2003\"").And.Contain("\"Stockroom 2003\" itself stays as it is");
        text.Should().Contain("[McpCallable(").And.Contain("Changes = true");
        text.Should().Contain("Keep everything this interface offers now");
        text.Should().Contain("If it offers all of that already, change nothing");
        text.Should().Contain("the Group \"Booking\"").And.Contain("  Button \"Book out\"");
    }

    [Fact]
    public void The_small_program_uses_the_program_only_through_its_interface()
    {
        var text = FissionGuidance.ForTheProgramPart("Stockroom", "Stockroom 2003", "Booking", "the Group \"Booking\"", "  Button \"Book out\"");

        text.Should().Contain("Write the program \"Booking\"");
        text.Should().Contain("calling the interface \"Stockroom\" with Apps.CallAsync").And.Contain("RUNNING APPS");
        text.Should().Contain("it never touches \"Stockroom 2003\" itself");
        text.Should().Contain("While \"Stockroom\" is not running, say so");
        text.Should().Contain("[AppInfo(\"Booking\"").And.Contain("  Button \"Book out\"");
    }

    [Fact]
    public void The_small_program_shows_on_each_button_what_it_does_though_the_model_never_sees_the_part()
    {
        // In Word the font colour button is an "A" with a red bar; the model sees only "Button "Schriftfarbe Rot"".
        var text = FissionGuidance.ForTheProgramPart("Word", "WINWORD", "Schriftart", "the Group \"Schriftart\"", "  Button \"Schriftfarbe Rot\"");

        text.Should().Contain("You see the part only as the list below, not how it looks");
        text.Should().Contain("make every button show what it does as plainly as it does in \"WINWORD\"");
        text.Should().Contain("a button for a color shows that color");
        text.Should().NotContain("The picture", "a provider that reads no pictures gets none and is told of none");
    }

    [Fact]
    public void With_the_picture_of_the_part_the_small_program_is_built_to_look_like_it()
    {
        // Paint's palette: twenty colour fields whose names reach the model only as shapes; the picture shows the colours.
        var text = FissionGuidance.ForTheProgramPart("Paint", "mspaint", "Farbe", "the Group \"Farbe\"", "  ListItem ~\"Xxxxxxx\"", picture: true);

        text.Should().Contain("The picture shows the part as it looks in \"mspaint\"");
        text.Should().Contain("the same symbols, colors and arrangement");
        // Paint's eraser and colour picker have no emoji that looks like them: a syringe is no colour picker.
        text.Should().Contain("where none of your symbols looks like the one in the picture, write the button's name on it instead");
        text.Should().Contain("The list below gives the controls and their addresses");
        text.Should().NotContain("You see the part only as the list below");

        FissionGuidance.ForTheInterface("Paint", "mspaint", "Farbe", "the Group \"Farbe\"", "", picture: true)
            .Should().Contain("The picture shows the part as it looks now.");
        FissionGuidance.ForTheInterface("Paint", "mspaint", "Farbe", "the Group \"Farbe\"", "")
            .Should().NotContain("picture");
    }

    [Fact]
    public void The_chat_of_the_interface_names_the_part_and_the_program()
    {
        FissionGuidance.ChatPromptForProgram("Booking", "Stockroom 2003")
            .Should().Be("Tear the part \"Booking\" out of Stockroom 2003, as a small program of its own that uses Stockroom 2003 through this interface.");
    }
}
