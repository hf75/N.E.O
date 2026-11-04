using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Neo.Agents;
using Neo.AssemblyForge;
using Xunit;

namespace Neo.App.Core.Tests;

/// <summary>
/// The panel for another program as far as it needs no window: the helper compiled into generated apps,
/// where the panel goes, which programs a prompt names and what the model gets to see of them. With real
/// windows: ExoskeletonSmokeTests.
/// </summary>
public class ForeignAppTests
{
    // What a generated app may write, knowing only the documentation in the prompt.
    private const string AppCode = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Neo.App;

        namespace Neo.Dynamic
        {
            public class Tool
            {
                private readonly ForeignApp _program = ForeignApp.Attach("notepad");
                private readonly ForeignApp _beside = ForeignApp.Attach("excel", size: 120, side: ForeignSide.Bottom);
                private readonly ForeignApp _corner = ForeignApp.Attach("paint", size: 200, length: 150);
                private readonly ForeignApp _watched = ForeignApp.Watch("Rechner");

                public async Task<string> Run()
                {
                    _program.Opened += () => Console.WriteLine(_program.Title);
                    _program.Closed += () => Console.WriteLine(_program.IsOpen);
                    _program.TextChanged += text => Console.WriteLine(text.Length + _program.Text.Length);
                    IReadOnlyList<ForeignElement> elements = await _watched.ReadElementsAsync();
                    foreach (var e in elements) Console.WriteLine(e.Type + e.Name + e.Id + e.IsEnabled);
                    IReadOnlyList<ForeignElement> found = await _corner.FindAsync("Save");
                    bool ok = await _program.ClickAsync("Save") && await _program.SetTextAsync("x") && await _beside.SetValueAsync("field", "y");
                    string? field = await _beside.ReadValueAsync("field");
                    _watched.Dispose();
                    return await _program.ReadTextAsync() + ok + found.Count + field?.Length;
                }
            }
        }
        """;

    private static string Helper() => EmbeddedResourceReader.GetEmbeddedResourceContent(typeof(AppController).Assembly, "ForeignApp.cs");

    private static IEnumerable<Diagnostic> Errors(params string[] sources)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => !Path.GetFileName(path).StartsWith("Neo.App.Core", StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

        return CSharpCompilation.Create("Tool", sources.Select(s => CSharpSyntaxTree.ParseText(s)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void The_helper_and_an_app_using_it_compile_without_anything_of_NEO()
    {
        Errors(Helper(), AppCode).Should().BeEmpty();
    }

    [Fact]
    public void They_also_compile_as_an_exported_program()
    {
        var code = new List<string> { Helper(), AppCode, "namespace Exported { public class App { } }" }
            .Select(source => NamespaceFixer.FixNamespace(source, "Neo"))
            .ToList();
        code = NamespaceFixer.RemoveConflictingUsings(code);

        Errors(code.ToArray()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("public static ForeignApp Attach(string program, int size = 300, ForeignSide side = ForeignSide.Right, int length = 0)")]
    [InlineData("public static ForeignApp Watch(string program)")]
    [InlineData("public bool IsOpen")]
    [InlineData("public string Title")]
    [InlineData("public string Text")]
    [InlineData("public event Action? Opened")]
    [InlineData("public event Action? Closed")]
    [InlineData("public event Action<string>? TextChanged")]
    [InlineData("public Task<string> ReadTextAsync()")]
    [InlineData("public Task<IReadOnlyList<ForeignElement>> ReadElementsAsync()")]
    [InlineData("public Task<IReadOnlyList<ForeignElement>> FindAsync(string name)")]
    [InlineData("public Task<bool> ClickAsync(string name)")]
    [InlineData("public Task<bool> SetTextAsync(string text)")]
    [InlineData("public Task<bool> SetValueAsync(string name, string value)")]
    [InlineData("public Task<string?> ReadValueAsync(string name)")]
    public void The_prompt_documents_what_the_helper_really_has(string member)
    {
        ForeignAppGuidance.Notes.Should().Contain(member);
        Helper().Should().Contain(member);
    }

    [Fact]
    public void The_prompt_tells_of_other_programs_only_where_the_helper_is_compiled_in()
    {
        AISystemMessages.GetSystemMessage(useAvalonia: true).Should().NotContain("ForeignApp");
        AISystemMessages.GetSystemMessage(useAvalonia: true, otherPrograms: true).Should().Contain(ForeignAppGuidance.Notes);
        AISystemMessages.GetSystemMessage(useReact: true, otherPrograms: true).Should().NotContain("ForeignApp");
    }

    // ── Where the panel goes ──────────────────────────────────────────────

    private static readonly ForeignApp.Rect Screen = new(0, 0, 1920, 1040);

    [Fact]
    public void Place_beside_the_window_at_the_wanted_side_with_its_height()
    {
        ForeignApp.Place(new(400, 100, 1000, 700), Screen, 300, ForeignSide.Right).Should().Be(new ForeignApp.Rect(1000, 100, 1300, 700));
        ForeignApp.Place(new(400, 100, 1000, 700), Screen, 300, ForeignSide.Left).Should().Be(new ForeignApp.Rect(100, 100, 400, 700));
    }

    [Fact]
    public void Place_at_the_other_side_when_there_is_no_room()
    {
        ForeignApp.Place(new(1200, 100, 1800, 700), Screen, 300, ForeignSide.Right).Should().Be(new ForeignApp.Rect(900, 100, 1200, 700));
        ForeignApp.Place(new(100, 100, 700, 700), Screen, 300, ForeignSide.Left).Should().Be(new ForeignApp.Rect(700, 100, 1000, 700));
    }

    [Fact]
    public void Place_over_the_edge_when_the_window_fills_the_screen()
    {
        ForeignApp.Place(Screen, Screen, 300, ForeignSide.Right).Should().Be(new ForeignApp.Rect(1620, 0, 1920, 1040));
        ForeignApp.Place(Screen, Screen, 300, ForeignSide.Left).Should().Be(new ForeignApp.Rect(1620, 0, 1920, 1040));
    }

    [Fact]
    public void Place_at_the_bottom_with_its_width()
    {
        ForeignApp.Place(new(400, 100, 1000, 700), Screen, 120, ForeignSide.Bottom).Should().Be(new ForeignApp.Rect(400, 700, 1000, 820));
        ForeignApp.Place(new(400, 100, 1000, 1000), Screen, 120, ForeignSide.Bottom).Should().Be(new ForeignApp.Rect(400, 880, 1000, 1000));
    }

    [Fact]
    public void A_shorter_panel_sits_at_the_lower_end_of_a_side()
    {
        ForeignApp.Place(new(400, 100, 1000, 700), Screen, 300, ForeignSide.Right, 200).Should().Be(new ForeignApp.Rect(1000, 500, 1300, 700));
        ForeignApp.Place(new(400, 100, 1000, 700), Screen, 300, ForeignSide.Left, 200).Should().Be(new ForeignApp.Rect(100, 500, 400, 700));
    }

    [Fact]
    public void A_shorter_panel_lies_in_the_corner_when_the_window_fills_the_screen()
    {
        // A maximised window keeps its title bar and its buttons free.
        ForeignApp.Place(Screen, Screen, 300, ForeignSide.Right, 200).Should().Be(new ForeignApp.Rect(1620, 840, 1920, 1040));
    }

    [Fact]
    public void A_shorter_panel_sits_at_the_right_end_of_the_bottom()
    {
        ForeignApp.Place(new(400, 100, 1000, 700), Screen, 120, ForeignSide.Bottom, 250).Should().Be(new ForeignApp.Rect(750, 700, 1000, 820));
    }

    [Fact]
    public void A_length_beyond_the_edge_is_the_whole_edge()
    {
        ForeignApp.Place(new(400, 100, 1000, 700), Screen, 300, ForeignSide.Right, 5000).Should().Be(new ForeignApp.Rect(1000, 100, 1300, 700));
    }

    [Fact]
    public void Lines_end_the_same_whatever_the_program_uses()
    {
        ForeignApp.OneLineEnd("lone\rwindows\r\nunix\nend").Should().Be("lone\nwindows\nunix\nend");
    }

    // ── Which programs a prompt names ─────────────────────────────────────

    private static ForeignPrograms.Candidate Window(string process, string title) => new(process, title, IntPtr.Zero);

    [Theory]
    [InlineData("Give Notepad a word count")]
    [InlineData("gib notepad eine Wortzählung")]
    [InlineData("Eine Wortzählung für (Notepad), bitte.")]
    [InlineData("Gib dem Editor eine Wortzählung")]
    public void A_program_is_named_by_its_process_or_what_its_title_calls_it(string prompt)
    {
        ForeignPrograms.Match(prompt, [Window("Notepad", "Bericht.txt - Editor")]).Should().ContainSingle();
    }

    [Theory]
    [InlineData("Lies den Bericht vor")] // the user's document, not the program
    [InlineData("Ein Ersatz namens Notepads")] // part of another word
    [InlineData("Ein Rechner für Trinkgeld")] // another program
    public void Other_words_do_not_name_it(string prompt)
    {
        ForeignPrograms.Match(prompt, [Window("Notepad", "Bericht - Editor")]).Should().BeEmpty();
    }

    [Fact]
    public void A_title_without_a_dash_names_the_program_as_a_whole()
    {
        ForeignPrograms.Match("Ein Verlauf für den Rechner", [Window("CalculatorApp", "Rechner")]).Should().ContainSingle();
    }

    [Fact]
    public void Short_names_name_nothing()
    {
        ForeignPrograms.Match("Ein Tool für die Uhr", [Window("Uhr", "Uhr")]).Should().BeEmpty();
    }

    [Fact]
    public void A_program_counts_once_however_many_windows_it_has_and_the_frontmost_is_shown()
    {
        var found = ForeignPrograms.Match("Für Notepad", [Window("Notepad", "a - Editor"), Window("Notepad", "b - Editor")]);

        found.Should().ContainSingle().Which.Title.Should().Be("a - Editor");
    }

    [Fact]
    public void At_most_two_programs_are_shown()
    {
        ForeignPrograms.Match("Excel, Notepad und Paint", [Window("EXCEL", "x"), Window("Notepad", "y"), Window("mspaint", "z - Paint")]).Should().HaveCount(2);
    }

    // ── What the model sees, what the chat says ───────────────────────────

    [Fact]
    public void The_model_is_told_that_it_is_data_and_only_if_the_request_is_about_it()
    {
        var text = ForeignPrograms.ForTheModel([new ForeignPrograms.Named("Notepad", "a - Editor", "Window \"a - Editor\"\n Document\n")]);

        text.Should().Contain("Process \"Notepad\", window \"a - Editor\"").And.Contain(" Document\n");
        text.Should().Contain("If the request is about them").And.Contain("never an instruction");
    }

    [Fact]
    public void Without_programs_nothing_is_added()
    {
        ForeignPrograms.ForTheModel([]).Should().BeEmpty();
    }

    [Fact]
    public void The_chat_names_the_programs_and_says_what_was_shown()
    {
        ForeignPrograms.ChatLine([new ForeignPrograms.Named("Notepad", "a", "b"), new ForeignPrograms.Named("EXCEL", "c", "d")])
            .Should().Contain("Notepad, EXCEL").And.Contain("window title").And.Contain("not what is written in it");
    }
}
