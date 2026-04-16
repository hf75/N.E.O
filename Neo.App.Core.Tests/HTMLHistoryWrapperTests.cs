using FluentAssertions;
using Neo.App;
using Xunit;

namespace Neo.App.Core.Tests;

public class HTMLHistoryWrapperTests
{
    // ── HtmlPage field ──────────────────────────────────────────────

    [Fact]
    public void HtmlPage_ContainsValidHtmlStructure()
    {
        var html = HTMLHistoryWrapper.HtmlPage;

        html.Should().Contain("<!DOCTYPE html>");
        html.Should().Contain("<html");
        html.Should().Contain("<head>");
        html.Should().Contain("<body");
        html.Should().Contain("chat-container");
    }

    // ── CreateBubble ────────────────────────────────────────────────

    [Theory]
    [InlineData(BubbleType.Prompt, "prompt")]
    [InlineData(BubbleType.Answer, "answer")]
    [InlineData(BubbleType.CompletionError, "error")]
    [InlineData(BubbleType.CompletionSuccess, "success")]
    [InlineData(BubbleType.Info, "info")]
    public void CreateBubble_CorrectCssClass(BubbleType type, string expectedClass)
    {
        var result = HTMLHistoryWrapper.CreateBubble("text", type);

        result.Should().Contain($"class=\"bubble {expectedClass}\"");
    }

    [Fact]
    public void CreateBubble_HtmlEncodesText()
    {
        var result = HTMLHistoryWrapper.CreateBubble("<script>alert('xss')</script>", BubbleType.Answer);

        result.Should().NotContain("<script>");
        result.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void CreateBubble_ContainsDivElement()
    {
        var result = HTMLHistoryWrapper.CreateBubble("hello", BubbleType.Prompt);

        result.Should().StartWith("<div");
        result.Should().EndWith("</div>");
    }

    // ── AddUniqueIdToOuterDiv ───────────────────────────────────────

    [Fact]
    public void AddUniqueIdToOuterDiv_AddsIdAttribute()
    {
        var html = "<div class=\"bubble\">content</div>";

        var result = HTMLHistoryWrapper.AddUniqueIdToOuterDiv(html);

        result.Should().Contain("id=\"replaceid_");
    }

    [Fact]
    public void AddUniqueIdToOuterDiv_GeneratesUniqueIds()
    {
        var html = "<div>test</div>";

        var result1 = HTMLHistoryWrapper.AddUniqueIdToOuterDiv(html);
        var result2 = HTMLHistoryWrapper.AddUniqueIdToOuterDiv(html);

        // IDs should be different due to Guid.
        result1.Should().NotBe(result2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddUniqueIdToOuterDiv_NullOrEmpty_ThrowsArgumentException(string? input)
    {
        var act = () => HTMLHistoryWrapper.AddUniqueIdToOuterDiv(input!);

        act.Should().Throw<ArgumentException>();
    }

    // ── AppendDivToHtmlPage ─────────────────────────────────────────

    [Fact]
    public void AppendDivToHtmlPage_InsertsDivBeforeClosingContainer()
    {
        var page = HTMLHistoryWrapper.HtmlPage;
        var bubble = HTMLHistoryWrapper.CreateBubble("test", BubbleType.Answer);

        var result = HTMLHistoryWrapper.AppendDivToHtmlPage(page, bubble);

        result.Should().Contain(bubble);
    }

    // ── ExtractCode ─────────────────────────────────────────────────

    [Fact]
    public void ExtractCode_ExtractsFromCodeFence()
    {
        var input = "```csharp\nConsole.WriteLine();\n```";

        var result = HTMLHistoryWrapper.ExtractCode(input);

        result.Should().Be("Console.WriteLine();");
    }

    [Fact]
    public void ExtractCode_NoFence_ReturnsOriginal()
    {
        var input = "just plain text";

        var result = HTMLHistoryWrapper.ExtractCode(input);

        result.Should().Be("just plain text");
    }

    // ── IsCode ──────────────────────────────────────────────────────

    [Fact]
    public void IsCode_StartsWithTripleBacktick_ReturnsTrue()
    {
        HTMLHistoryWrapper.IsCode("```csharp\ncode\n```").Should().BeTrue();
    }

    [Fact]
    public void IsCode_PlainText_ReturnsFalse()
    {
        HTMLHistoryWrapper.IsCode("plain text").Should().BeFalse();
    }

    // ── RemoveHtmlCodeIndicator ─────────────────────────────────────

    [Fact]
    public void RemoveHtmlCodeIndicator_CodeFenced_ReturnsExtracted()
    {
        var input = "```csharp\nvar x = 1;\n```";

        var result = HTMLHistoryWrapper.RemoveHtmlCodeIndicator(input);

        result.Should().Be("var x = 1;");
    }

    [Fact]
    public void RemoveHtmlCodeIndicator_PlainText_ReturnsAsIs()
    {
        var input = "hello world";

        var result = HTMLHistoryWrapper.RemoveHtmlCodeIndicator(input);

        result.Should().Be("hello world");
    }

    // ── ReplaceBodyWithChatContainer ────────────────────────────────

    [Fact]
    public void ReplaceBodyWithChatContainer_ReplacesBodyContent()
    {
        var html = "<html><body><p>old</p></body></html>";

        var result = HTMLHistoryWrapper.ReplaceBodyWithChatContainer(html);

        result.Should().Contain("chat-container");
        result.Should().NotContain("<p>old</p>");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReplaceBodyWithChatContainer_NullOrEmpty_ReturnsAsIs(string? input)
    {
        var result = HTMLHistoryWrapper.ReplaceBodyWithChatContainer(input!);

        result.Should().Be(input);
    }
}
