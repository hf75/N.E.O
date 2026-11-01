using FluentAssertions;
using Xunit;

namespace Neo.App.Core.Tests;

/// <summary>The system prompt of the desktop hosts, beyond what Neo.AssemblyForge.Tests covers.</summary>
public class AISystemMessagesTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Small_tools_are_the_goal_and_extras_only_come_when_asked_for(bool avalonia, bool react)
    {
        var message = AISystemMessages.GetSystemMessage(avalonia, react);

        message.Should().Contain("be quick");
        message.Should().Contain("No extra features and no polishing unless the user asks for them");
        message.Should().Contain("visible label", "without it a tip calculator showed two boxes reading 10 and 1, and no word about which is which");
    }

    [Fact]
    public void The_rule_comes_last_so_that_nothing_after_it_asks_for_more()
    {
        var message = AISystemMessages.GetSystemMessage(true, false, false, null, ["Sales: records sales."]);

        message.TrimEnd().Should().EndWith(AISystemMessages.QuickToolRule.TrimEnd());
    }
}
