using Neo.App.WebApp.Services.Ai;
using Neo.AssemblyForge;
using Xunit;

namespace Neo.App.WebApp.Tests;

public class NumberRuleTests
{
    [Fact]
    public void The_prompt_of_the_browser_says_how_to_read_numbers()
    {
        Assert.Contains(NumberGuidance.Rule, SystemPrompts.AvaloniaBrowser);
    }
}
