using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Neo.AssemblyForge;
using Xunit;

namespace Neo.App.Core.Tests;

/// <summary>What the model is told for an app link and what comes back. With real windows: AppLinkSmokeTests.</summary>
public class LinkGuidanceTests
{
    private static readonly LinkProgram Dragged = new("Meter", "class Meter { private double _level; }", "Controls:\n  Slider:0 Value=10\n");
    private static readonly LinkProgram Target = new("Board", "class Board { private int _rows; }", "Controls:\n  TextBox:0 Text=\"\"\n");

    private static IEnumerable<Diagnostic> ErrorsOf(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

        return CSharpCompilation.Create("Communicator", [CSharpSyntaxTree.ParseText(source)], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void The_example_the_model_gets_compiles_as_a_block()
    {
        var source = LinkGuidance.CommunicatorSource([new LinkBlock("t", "d", LinkGuidance.ExampleCode)]);

        ErrorsOf(source).Should().BeEmpty();
        LinkGuidance.SystemMessage.Should().Contain(LinkGuidance.ExampleCode);
    }

    [Fact]
    public void A_block_can_use_all_a_side_offers_and_remember_things()
    {
        var code = """
            double rate = 1;
            b.OnField<double>("_rate", r => rate = r);
            a.OnChange<string>("TextBlock:0", "Text", t => b.Set("TextBox:0", "Text", (double.Parse(Regex.Match(t, @"\d+").Value, CultureInfo.InvariantCulture) * rate).ToString(CultureInfo.InvariantCulture)));
            a.OnClick("Button:0", () => { b.Press("Button:1"); b.SetField("_seen", a.Name); b.Call("Show", 1, "x"); });
            """;

        ErrorsOf(LinkGuidance.CommunicatorSource([new LinkBlock("t", "d", code)])).Should().BeEmpty();
    }

    [Fact]
    public void All_a_side_offers_is_in_what_the_model_is_told()
    {
        foreach (var method in typeof(Neo.App.ILinkSide).GetMethods().Where(m => !m.IsSpecialName))
            LinkGuidance.SystemMessage.Should().Contain("a." + method.Name, "the model only knows what it is told");
    }

    [Theory]
    [InlineData("never inside a callback")]
    [InlineData("No watch with an empty callback")]
    [InlineData("a.X only for what program a has")]
    [InlineData("two plain watches, one each way, without any guard")]
    public void The_model_is_told_what_it_wrote_when_it_was_not(string rule)
    {
        // Seen with the real model: a watch started again at every change, also to read a value once or to guard
        // against an echo the link already stops, and a watch on the program that has no such field.
        LinkGuidance.SystemMessage.Should().Contain(rule);
    }

    [Fact]
    public void Each_block_becomes_a_method_of_its_own_so_the_person_can_choose()
    {
        var source = LinkGuidance.CommunicatorSource([new LinkBlock("one", "", "a.Press(\"Button:0\");"), new LinkBlock("two", "", "b.Press(\"Button:1\");")]);

        source.Should().Contain("public static class " + LinkGuidance.CommunicatorClass);
        source.Should().Contain("Block0(ILinkSide a, ILinkSide b)").And.Contain("Block1(ILinkSide a, ILinkSide b)");
        ErrorsOf(source).Should().BeEmpty();
    }

    [Fact]
    public void The_model_reads_both_programs_and_which_is_which()
    {
        var text = LinkGuidance.ForTheModel(Dragged, Target);

        text.Should().Contain("Slider:0 Value=10").And.Contain("TextBox:0");
        text.Should().Contain("private double _level").And.Contain("private int _rows");
        text.Should().Contain("a, the program that was dragged: \"Meter\"");
        text.Should().Contain("b, the program it was dropped on: \"Board\"");
    }

    [Fact]
    public void A_wish_goes_with_what_was_suggested_before()
    {
        var text = LinkGuidance.ForTheModel(Dragged, Target, "also the colours", "{\"blocks\":[]}");

        text.Should().Contain("also the colours").And.Contain("{\"blocks\":[]}");
    }

    [Theory]
    [InlineData("""{"blocks":[{"title":"T","detail":"D","code":"a.Press(\"x\");"}],"note":"N"}""")]
    [InlineData("""
        Here it is:
        ```json
        {"blocks":[{"title":"T","detail":"D","code":"a.Press(\"x\");"}],"note":"N"}
        ```
        """)]
    public void An_answer_is_read_also_with_a_fence_around_it(string answer)
    {
        var proposal = LinkProposal.Parse(answer);

        proposal.Blocks.Should().ContainSingle();
        proposal.Blocks[0].Should().Be(new LinkBlock("T", "D", "a.Press(\"x\");"));
        proposal.Note.Should().Be("N");
    }

    [Fact]
    public void Two_programs_with_nothing_to_give_each_other_are_an_answer_too()
    {
        var proposal = LinkProposal.Parse("""{"blocks":[],"note":"Nothing fits."}""");

        proposal.Blocks.Should().BeEmpty();
        proposal.Note.Should().Be("Nothing fits.");
    }

    [Fact]
    public void A_block_without_a_title_or_without_code_is_no_block()
    {
        LinkProposal.Parse("""{"blocks":[{"title":"","detail":"","code":"x"},{"title":"T","detail":"","code":" "}],"note":""}""").Blocks.Should().BeEmpty();
    }

    [Theory]
    [InlineData("I cannot do that.")]
    [InlineData("{ not json }")]
    public void What_is_no_proposal_says_so(string answer)
    {
        FluentActions.Invoking(() => LinkProposal.Parse(answer)).Should().Throw<FormatException>();
    }

    [Fact]
    public void A_proposal_can_go_back_to_the_model_as_it_came()
    {
        var proposal = new LinkProposal([new LinkBlock("T", "D", "a.Press(\"x\");")], "N");

        var again = LinkProposal.Parse(proposal.ToJson());

        again.Blocks.Should().Equal(proposal.Blocks);
        again.Note.Should().Be("N");
    }

    [Fact]
    public void After_a_compile_error_the_model_sees_its_answer_and_the_error()
    {
        var text = LinkGuidance.AfterCompileErrors("the question", "the answer", "CS0103: rate");

        text.Should().Contain("the question").And.Contain("the answer").And.Contain("CS0103: rate");
    }
}
