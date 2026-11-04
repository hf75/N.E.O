using FluentAssertions;
using Neo.McpServer.Services;
using Neo.McpServer.Tools;
using Xunit;

namespace Neo.McpServer.Tests;

/// <summary>
/// Other programs on the path Claude Code takes: the interface Claude writes compiles in a preview whatever the
/// window ships (the helper goes in as source), and the list of open programs says which programs, not what is open in them.
/// </summary>
public class ForeignProgramToolTests
{
    private const string Interface = """
        using System;
        using System.Threading.Tasks;
        using Avalonia.Controls;
        using Neo.App;

        [AppInfo("Stockroom", "The stockroom program.", Program = "Stockroom2003")]
        public class DynamicUserControl : UserControl
        {
            private readonly ForeignApp _stockroom = ForeignApp.Attach("Stockroom2003", size: 260);

            [McpCallable("The stock of an item.")]
            public async Task<string> Stock(string item)
            {
                await _stockroom.SetAsync("Item no.:", item);
                await _stockroom.PressAsync("Look up");
                return await _stockroom.GetAsync("In stock:>Text");
            }

            [McpCallable("Books a quantity of an item out.", Changes = true)]
            public async Task<string> BookOut(string item, int quantity)
            {
                await _stockroom.SetAsync("Quantity:", quantity.ToString());
                await _stockroom.PressAsync("Book out");
                if (await _stockroom.DialogAsync(seconds: 1) is { } dialog) throw new InvalidOperationException(dialog.Text);
                return await _stockroom.GetAsync("StatusBar:1>Text");
            }
        }
        """;

    [Fact]
    public async Task An_interface_compiles_as_a_preview_with_the_helper_as_source()
    {
        var result = await new CompilationPipeline().CompileAsync(new[] { Interface }, framework: "avalonia");

        result.Errors.Should().BeEmpty();
        result.Success.Should().BeTrue();
    }

    [Fact]
    public void The_helper_goes_in_only_where_an_app_uses_it_and_does_not_bring_its_own()
    {
        CompilationPipeline.WithForeignApp([Interface]).Should().HaveCount(2).And.Contain(s => s.Contains("public sealed class ForeignApp"));
        CompilationPipeline.WithForeignApp(["public class DynamicUserControl { }"]).Should().ContainSingle("an app that does not use it gets nothing");
        var withOwn = CompilationPipeline.WithForeignApp([Interface]);
        CompilationPipeline.WithForeignApp(withOwn).Should().HaveCount(2, "the Claude bridge brings the helper itself");
    }

    [Theory]
    [InlineData("Bericht.txt - Editor", "Editor")]
    [InlineData("*C:\\Home\\scratch\\x-post.md - Notepad++", "Notepad++")]
    [InlineData("A private page - Claude and 27 more pages - Personal - Microsoft Edge", "Microsoft Edge")]
    [InlineData("Eine Seite - Claude und 27 weitere Seiten - Persönlich – Microsoft​ Edge", "Microsoft​ Edge")]
    [InlineData("Vorgarten – Datei-Explorer", "Datei-Explorer")]
    [InlineData("Stockroom 2003", "Stockroom 2003")]
    public void Of_a_window_title_only_the_program_goes_out(string title, string program)
    {
        ForeignProgramTools.ProgramPart(title).Should().Be(program);
    }
}
