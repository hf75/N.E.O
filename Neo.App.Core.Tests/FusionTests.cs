using FluentAssertions;
using Neo.AssemblyForge;
using Xunit;

namespace Neo.App.Core.Tests;

/// <summary>What is said and decided when one program is dragged onto another. With real windows: FusionSmokeTests.</summary>
public class FusionTests
{
    [Fact]
    public void The_model_gets_the_source_and_what_must_stay_as_it_is()
    {
        var text = FusionGuidance.ForTheModel("Currency", "currency", "class Other { private int _rate; }", ["Humanizer|2.14.1"]);

        text.Should().Contain("class Other { private int _rate; }");
        text.Should().Contain("\"Currency\"");
        text.Should().Contain("keeps everything it can do");
        text.Should().Contain("connect them", "the point of putting exactly these two together");
        text.Should().Contain("under its exact name", "the running data moves over by name");
        text.Should().Contain("\"currency_\"", "the same prefix the data looks for");
        text.Should().Contain("Humanizer|2.14.1");
        text.Should().Contain("side by side", "the window grows by the other one's width, and neither part may lie over the other");
        text.Should().Contain("ClipToBounds");
        text.Should().Contain("must not", "a picture in a ScrollViewer has no size: a merged fractal stayed black");
        text.Should().Contain("reads them from the part it belongs to", "a merged fractal steered to a spot left of the click");
    }

    [Theory]
    [InlineData(816.4, 300.0, "in the proportion 816:300")]
    [InlineData(null, 300.0, "two columns of equal width")]
    [InlineData(816.0, 0.0, "two columns of equal width")]
    public void Each_part_keeps_the_room_it_has_when_the_widths_are_known(double? own, double other, string says)
    {
        FusionGuidance.ForTheModel("Palette", "palette", "code", [], own, other).Should().Contain(says);
    }

    [Fact]
    public void Without_packages_none_are_mentioned()
    {
        FusionGuidance.ForTheModel("Currency", "currency", "code", []).Should().NotContain("NuGet packages:");
    }

    [Fact]
    public void The_chat_says_what_happened_without_the_source()
    {
        FusionGuidance.ChatPrompt("Currency").Should().Be("Merge \"Currency\" into this app.");
    }

    [Theory]
    [InlineData("Currency", "currency")]
    [InlineData("Trip Split 2", "tripsplit")]
    [InlineData("Ein sehr langer Name für eine App", "einsehrlange")]
    [InlineData("???", "other")]
    [InlineData("Größe", "gre")]
    public void The_prefix_is_a_part_of_a_field_name(string name, string prefix)
    {
        FusionGuidance.PrefixFor(name).Should().Be(prefix);
    }

    [Theory]
    [InlineData("Currency", "", "Currency")]
    [InlineData("N.E.O. — Live Preview", "[AppInfo(\"Rates\", \"what it does\")] class A {}", "Rates")]
    [InlineData("N.E.O. — Live Preview", "class A {}", "the other program")]
    [InlineData("", "[AppInfo( \"Rates\" )] class A {}", "Rates")]
    public void A_program_goes_by_the_name_it_gave_itself(string published, string code, string name)
    {
        AppController.FusionName(published, code).Should().Be(name);
    }
}
