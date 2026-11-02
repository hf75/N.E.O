using FluentAssertions;
using Neo.AssemblyForge;
using Neo.McpServer.Tools;
using Xunit;

namespace Neo.McpServer.Tests;

public class NumberRuleTests
{
    [Fact]
    public void Both_prompts_of_the_server_say_how_to_read_numbers()
    {
        AvaloniaPrompt.CreateAvaloniaApp("a tool").Should().Contain(NumberGuidance.Rule);
        AvaloniaPrompt.CreateWpfApp("a tool").Should().Contain(NumberGuidance.Rule);
    }
}