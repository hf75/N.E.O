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

                // An interface: what a person does in the program, as methods that throw instead of returning false.
                public async Task<string> BookOut(string item, int quantity)
                {
                    try
                    {
                        await _program.SetAsync("Item no.:", item);
                        await _program.SetAsync("Quantity:>Edit", quantity.ToString());
                        await _program.CheckAsync("Print slip", true);
                        await _program.SelectAsync("Store", "Main");
                        await _program.PressAsync("Book out");
                        await _program.WaitForAsync("Edit:2", seconds: 5);
                        ForeignDialog? dialog = await _program.DialogAsync(seconds: 2);
                        if (dialog != null) { await _program.PressAsync(dialog.Buttons[0]); return dialog.Title + dialog.Text; }
                        IReadOnlyList<string[]> rows = await _program.RowsAsync("Bookings");
                        return await _program.GetAsync("status") + rows.Count + rows[0][0];
                    }
                    catch (ForeignProgramClosedException closed) { return closed.Program; }
                    catch (ForeignControlMissingException missing) { throw new InvalidOperationException(missing.Program + missing.Control, missing); }
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
    [InlineData("public Task SetAsync(string control, string value)")]
    [InlineData("public Task PressAsync(string control)")]
    [InlineData("public Task<string> GetAsync(string control)")]
    [InlineData("public Task CheckAsync(string control, bool on)")]
    [InlineData("public Task SelectAsync(string control, string item)")]
    [InlineData("public Task<IReadOnlyList<string[]>> RowsAsync(string control)")]
    [InlineData("public Task WaitForAsync(string control, int seconds = 10)")]
    [InlineData("public Task<ForeignDialog?> DialogAsync(int seconds = 0)")]
    public void The_prompt_documents_what_the_helper_really_has(string member)
    {
        ForeignAppGuidance.Notes.Should().Contain(member);
        Helper().Should().Contain(member);
    }

    [Theory]
    [InlineData("record ForeignDialog(string Title, string Text, IReadOnlyList<string> Buttons)")]
    [InlineData("ForeignControlMissingException")]
    [InlineData("ForeignProgramClosedException")]
    public void The_prompt_names_the_records_and_exceptions_of_the_helper(string name)
    {
        ForeignAppGuidance.Notes.Should().Contain(name);
        Helper().Should().Contain(name.StartsWith("record") ? "public sealed " + name : "public sealed class " + name);
    }

    [Fact]
    public void An_interface_is_told_how_it_is_found_and_what_is_asked_first()
    {
        ForeignAppGuidance.Notes.Should().Contain(ForeignAppGuidance.Interfaces);
        ForeignAppGuidance.Interfaces.Should().Contain("Program = \"<its process name>\"").And.Contain("Changes = true")
            .And.Contain("never catch ForeignControlMissingException");
    }

    // ── Addresses of controls, the same in the helper and in N.E.O.'s description of a window ──

    [Theory]
    [InlineData("Menge:>Edit", "Menge:", "Edit", 0)]
    [InlineData("Bestand: > Text", "Bestand:", "Text", 0)]
    [InlineData("StatusBar:1>Text", "StatusBar:1", "Text", 0)]
    [InlineData("Booking>Pane", "Booking", "Pane", 0)]
    [InlineData("Edit:2", null, "Edit", 2)]
    [InlineData("button:1", null, "button", 1)]
    public void An_address_is_what_comes_before_and_a_type_or_a_type_and_a_number(string control, string? label, string type, int index)
    {
        ForeignApp.ParseAddress(control).Should().Be((label, type, index));
    }

    [Theory]
    [InlineData("Book out")]
    [InlineData("txtArtikel")]
    [InlineData("Seite:2")]   // no type of control
    [InlineData("Edit:0")]    // they count from 1
    [InlineData("a > b")]
    public void A_name_or_an_id_is_no_address(string control)
    {
        ForeignApp.ParseAddress(control).Should().BeNull();
    }

    [Theory]
    [InlineData("Text", "Menge:", "Menge:")]
    [InlineData("Text", "Kunde: Müller", "")]        // what the program shows
    [InlineData("Text", "Total: 250,00", "")]
    [InlineData("Text", "Stock 2:", "")]
    [InlineData("Edit", "Item no.:", "Item no.:")]   // the label beside the field
    [InlineData("Edit", "4711", "")]                 // what was typed, in some programs
    [InlineData("Button", "Book out", "Book out")]
    [InlineData("ListItem", "Copper pipe", "")]
    [InlineData("DataItem", "Anna", "")]
    [InlineData("Document", "Brief an Anna", "")]
    public void Of_a_control_the_model_sees_its_name_only_where_it_names_a_control(string type, string name, string shown)
    {
        Neo.Shared.ForeignWindows.Shown(type, name).Should().Be(shown);
    }

    [Fact]
    public void Of_what_a_program_shows_the_model_sees_the_shape()
    {
        Neo.Shared.ForeignWindows.Shape("Kunde: Müller GmbH, 250,00 €").Should().Be("Xxxxx: Xxxxxx XxxX, 999,99 €");
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
    public void A_title_with_an_en_dash_names_the_program_after_it_and_not_the_folder_before_it()
    {
        ForeignPrograms.Match("Ein Tool für den Datei-Explorer", [Window("explorer", "Vorgarten – Datei-Explorer")]).Should().ContainSingle();
        ForeignPrograms.Match("Was liegt im Vorgarten?", [Window("explorer", "Vorgarten – Datei-Explorer")]).Should().BeEmpty();
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
