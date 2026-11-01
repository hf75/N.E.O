using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Neo.Agents;
using Xunit;

namespace Neo.App.Core.Tests;

/// <summary>
/// The search as N.E.O. uses it: the helper compiled into generated apps, what it tells the model, and
/// what it tells the user about the searches behind a result.
/// </summary>
public class WebSearchInNeoTests
{
    // What a generated app may write, knowing only the documentation in the prompt.
    private const string AppCode = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Neo.App;

        namespace Neo.Dynamic
        {
            public record Row(string Name, int Value);

            public class Tool
            {
                public async Task<string> Run(CancellationToken cancel)
                {
                    WebAnswer answer = await AIWebSearch.AskAsync("question");
                    WebAnswer<List<Row>> data = await AIWebSearch.AskAsync<List<Row>>("question", cancel);
                    foreach (WebSource source in answer.Sources)
                        Console.WriteLine(source.Title + source.Url);
                    return answer.Text + data.Data.Count + data.Sources.Count;
                }
            }
        }
        """;

    private static string Helper() => new ClaudeWebSearchPlugin().HelperTemplateCode!.Replace("WEB_SEARCH_MODEL_PLACEHOLDER", "some-model");

    private static IEnumerable<Diagnostic> Errors(params string[] sources)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

        return CSharpCompilation.Create("Tool", sources.Select(s => CSharpSyntaxTree.ParseText(s)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void The_helper_and_an_app_using_it_compile()
    {
        Errors(Helper(), AppCode).Should().BeEmpty();
    }

    [Fact]
    public void They_also_compile_as_an_exported_program()
    {
        // An export moves all code into the namespace Neo, next to a class App: the namespace Neo.App
        // of a referenced DLL is out of reach there. That is why the helper brings its own records.
        var code = new List<string> { Helper(), AppCode, "namespace Exported { public class App { } }" }
            .Select(source => NamespaceFixer.FixNamespace(source, "Neo"))
            .ToList();
        code = NamespaceFixer.RemoveConflictingUsings(code);

        Errors(code.ToArray()).Should().BeEmpty();
    }

    [Fact]
    public void The_prompt_documents_what_the_helper_really_has()
    {
        var docs = new ClaudeWebSearchPlugin().SystemMessageDocs!;
        var helper = Helper();

        foreach (var name in new[] { "record WebSource(string Title, string Url)", "record WebAnswer(string Text, IReadOnlyList<WebSource> Sources)", "record WebAnswer<T>(T Data, IReadOnlyList<WebSource> Sources)" })
        {
            docs.Should().Contain(name);
            helper.Should().Contain(name);
        }
    }

    [Fact]
    public void Plugins_see_the_placeholder_in_their_template_replaced()
    {
        Helper().Should().NotContain("PLACEHOLDER").And.Contain("\"some-model\"");
    }

    [Fact]
    public void The_build_guidance_gives_todays_date_and_keeps_pages_from_giving_orders()
    {
        var guidance = AISystemMessages.WebSearchWhileBuilding();

        guidance.Should().Contain(DateTime.Now.ToString("yyyy-MM-dd"));
        guidance.Should().Contain("never instructions");
        guidance.Should().StartWith("\n", "it is added to the end of the system message");
    }

    // ── What the user reads ───────────────────────────────────────────────

    private static readonly WebSource PageA = new("Page A", "https://a.example");

    [Fact]
    public void Report_NoSearch_SaysNothing()
    {
        AppController.WebSearchReport([], [], null).Should().BeNull();
    }

    [Fact]
    public void Report_NamesTheSearchesAndThePages()
    {
        var report = AppController.WebSearchReport(["neo news", "neo news", "avalonia"], [PageA, PageA], null)!;

        report.Should().Contain("\"neo news\", \"avalonia\"", "the same search in a second try is one search to the reader");
        report.Should().Contain("Page A: https://a.example");
        report.Split('\n').Should().HaveCount(2, "the same page found twice is one page");
    }

    [Fact]
    public void Report_ManyPages_ShowsTheFirstAndCountsTheRest()
    {
        var pages = Enumerable.Range(0, 12).Select(i => new WebSource("Page " + i, "https://p.example/" + i)).ToList();

        var report = AppController.WebSearchReport(["q"], pages, null)!;

        report.Should().Contain("Page 4").And.NotContain("Page 5").And.Contain("7 more");
    }

    [Fact]
    public void Report_SearchImpossible_SaysTheToolWasBuiltWithoutIt()
    {
        var report = AppController.WebSearchReport([], [], "Web search is switched off.")!;

        report.Should().Be("Web search is switched off. Built without it.");
    }
}
