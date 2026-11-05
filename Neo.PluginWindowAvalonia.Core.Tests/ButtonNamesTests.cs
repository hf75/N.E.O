using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using FluentAssertions;
using Xunit;

namespace Neo.PluginWindowAvalonia.Core.Tests;

/// <summary>
/// A button that shows a panel reaches UI Automation as "Avalonia.Controls.StackPanel" unless it gets a name: its tooltip,
/// else the texts it shows. What the app named, and a text the button shows itself, stay.
/// </summary>
public class ButtonNamesTests
{
    // The font colour button of a toolbar torn out of Word: an "A" over a red bar.
    private static Button ColourButton(string? tip)
    {
        var shown = new StackPanel();
        shown.Children.Add(new TextBlock { Text = "A" });
        shown.Children.Add(new Border { Height = 3, Width = 16, Background = Brushes.Red });
        var button = new Button { Content = shown };
        if (tip != null) ToolTip.SetTip(button, tip);
        return button;
    }

    [Fact]
    public void A_button_that_shows_a_panel_is_named_by_its_tooltip()
    {
        var button = ColourButton("Schriftfarbe Rot");
        ButtonNames.NameByWhatItShows(button);
        AutomationProperties.GetName(button).Should().Be("Schriftfarbe Rot");
    }

    [Fact]
    public void Without_a_tooltip_it_is_named_by_the_texts_it_shows()
    {
        var button = ColourButton(null);
        ButtonNames.NameByWhatItShows(button);
        AutomationProperties.GetName(button).Should().Be("A");
    }

    [Fact]
    public void A_name_the_app_gave_and_a_text_the_button_shows_stay()
    {
        var named = ColourButton("Schriftfarbe Rot");
        AutomationProperties.SetName(named, "Font colour");
        ButtonNames.NameByWhatItShows(named);
        AutomationProperties.GetName(named).Should().Be("Font colour");

        var text = new Button { Content = "Book out" };
        ToolTip.SetTip(text, "Books the amount out");
        ButtonNames.NameByWhatItShows(text);
        AutomationProperties.GetName(text).Should().BeNullOrEmpty("its text names it already");
    }

    [Fact]
    public void Buttons_that_show_the_same_letter_differ_by_their_help_text()
    {
        Button Letter(string tip)
        {
            var button = new Button { Content = "A" };
            ToolTip.SetTip(button, tip);
            ButtonNames.NameByWhatItShows(button);
            return button;
        }

        var effects = Letter("Texteffekte und Typografie");
        var colour = Letter("Schriftfarbe Rot");

        AutomationProperties.GetHelpText(effects).Should().Be("Texteffekte und Typografie");
        AutomationProperties.GetHelpText(colour).Should().Be("Schriftfarbe Rot");

        var described = Letter("Schriftfarbe Rot");
        AutomationProperties.SetHelpText(described, "Font colour");
        ButtonNames.NameByWhatItShows(described);
        AutomationProperties.GetHelpText(described).Should().Be("Font colour", "a help text the app gave stays");
    }
}
