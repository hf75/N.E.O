using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neo.AssemblyForge;

/// <summary>One thing two linked programs can do together, in the words of the person and in code.</summary>
public sealed record LinkBlock(string Title, string Detail, string Code);

/// <summary>What the model suggests for two programs. No blocks: they have nothing to give each other, and the note says why.</summary>
public sealed record LinkProposal(IReadOnlyList<LinkBlock> Blocks, string Note)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private sealed record Answer(List<LinkBlock>? Blocks, string? Note);

    /// <summary>What the model answered, also when it put a code fence around it.</summary>
    /// <exception cref="FormatException">That is no proposal.</exception>
    public static LinkProposal Parse(string answer)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) throw new FormatException("The answer holds no JSON object.");

        try
        {
            var parsed = JsonSerializer.Deserialize<Answer>(answer[start..(end + 1)], Options) ?? throw new FormatException("The answer is empty.");
            return new LinkProposal(
                (parsed.Blocks ?? []).Where(b => !string.IsNullOrWhiteSpace(b.Title) && !string.IsNullOrWhiteSpace(b.Code)).ToList(),
                parsed.Note ?? "");
        }
        catch (JsonException ex)
        {
            throw new FormatException("The answer is no proposal: " + ex.Message, ex);
        }
    }

    /// <summary>This proposal as the model wrote it, for the next question about it.</summary>
    public string ToJson() => JsonSerializer.Serialize(new Answer(Blocks.ToList(), Note));
}

/// <summary>A running program as the model reads it for an app link.</summary>
/// <param name="Map">Its controls with their addresses and its fields, as they are right now.</param>
public sealed record LinkProgram(string Name, string Code, string Map);

/// <summary>What the model is told to connect two running programs without changing them.</summary>
public static class LinkGuidance
{
    /// <summary>The class the blocks become methods of.</summary>
    public const string CommunicatorClass = "LinkCommunicator";

    public const string JsonSchema = """
        {
          "type": "object",
          "properties": {
            "blocks": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string" },
                  "detail": { "type": "string" },
                  "code": { "type": "string" }
                },
                "required": ["title", "detail", "code"],
                "additionalProperties": false
              }
            },
            "note": { "type": "string" }
          },
          "required": ["blocks", "note"],
          "additionalProperties": false
        }
        """;

    /// <summary>The example the model gets. A test compiles it, so it stays true.</summary>
    public const string ExampleCode = """
        a.OnChange<double>("Slider:0", "Value", v => b.Set("TextBox:1", "Text", v.ToString("0.##", CultureInfo.InvariantCulture)));
        a.OnClick("Button:0", () => b.Press("Button:2"));
        """;

    public const string SystemMessage = $$"""
        You connect two desktop programs that are running right now, without changing either of them. Neither was written for the other. A small piece of code you write sits between them while they run: it watches what one shows and holds, and does to the other what a person could do. Nothing of it is saved, and it is gone when one of the programs ends.

        A person will choose from what you suggest. Suggest blocks: each one thing these two programs can do together that this person would want, like "the total of the expense list goes into the amount of the converter". Up to five, the most useful first. Each block must make sense without the others, and all of them are ticked at first, so they must also work together: never two blocks that fight over the same thing. The same value in both directions is one block that keeps both equal, if that makes sense at all. Do not suggest what nobody would want just because it is possible: no block at all is a good answer, and better than a filler. If the two have nothing to give each other, or the good idea needs a program to change (a picture in grey cannot show colours it has no code for), say that in the note in one sentence, by what it does and not by number. Merging the two programs, which is the other thing a person can do, can change them. Otherwise leave the note empty: it is not for explaining your blocks.

        Each block has:
        - title: at most eight plain words for someone who does not program, saying what the block does.
        - detail: one sentence saying what will happen. Write title and detail in the language of the programs' labels.
        - code: C# statements that become the body of a method with two parameters, a and b. a is the program that was dragged, b the program it was dropped on. Nothing else is available to it.

        a and b offer this, and nothing else:
          a.OnChange<T>(control, property, value => ...)    // runs once right away with the current value, then at every change
          a.OnField<T>(field, value => ...)                 // the same for a field or property of the program itself
          a.OnClick(button, () => ...)                      // a button was pressed
          a.Set(control, property, value)                   // sets a property of a control; the program reacts as it does when a person changes it
          a.SetField(field, value)                          // sets a field; nothing reacts by itself, so a.Call what shows it afterwards
          a.Press(button)                                   // presses a button
          a.Call(method, args...)                           // calls a method of the program, a private one too

        Rules for the code:
        - Use only the control addresses, properties, fields and methods that exist in what you are given. A control is named by its address exactly as listed, like "Slider:2". Properties are the ones of its Avalonia control type, like Value, Text, IsChecked, SelectedIndex, Background.
        - Prefer what a person would do: Set and Press. The program then reacts by itself. Use SetField and Call only where the source shows that no control does it.
        - T is what you want to get: double, int, bool, string, or a list or array of those. A number that a control shows inside a text comes as string, written the way the program writes it. What a control shows is given only as its shape where it is not in the source, with letters as x and digits as 9: what people entered stays with them. The shape and the source tell you the format: where a program formats with the settings of this PC ("9.999,99" here), take the number out with Regex and parse it with CultureInfo.CurrentCulture, and write into a text box the way that program parses it.
        - Keep what a block must remember in local variables of the block: callbacks run one at a time, so that is safe. To combine two values, remember one in a local variable and use it when the other one changes.
        - Do not block, sleep or start threads. No files, no network. Avalonia types are not available: a colour is a string like "#FF8800".
        - What you set in one program can make it change something you watch. Avoid going round in circles.
        - The usings System, System.Linq, System.Collections.Generic, System.Globalization, System.Text and System.Text.RegularExpressions are there.

        Example of a block's code, a slider in a that fills a text box in b and a button in a that presses Save in b:
        {{ExampleCode}}
        """;

    /// <summary>The question about these two programs.</summary>
    /// <param name="wish">What the person added in their own words; null: nothing.</param>
    /// <param name="earlier">What was suggested before that, as JSON.</param>
    public static string ForTheModel(LinkProgram dragged, LinkProgram target, string? wish = null, string? earlier = null)
    {
        var text = new StringBuilder();
        AppendProgram(text, "a, the program that was dragged", dragged);
        AppendProgram(text, "b, the program it was dropped on", target);

        if (!string.IsNullOrWhiteSpace(earlier))
            text.Append("You suggested this before:\n").Append(earlier).Append("\n\n");

        text.Append(string.IsNullOrWhiteSpace(wish)
            ? "What can these two do together?"
            : $"The person wants this too, in their words: {wish}\nSuggest again: keep what still fits, and add this.");
        return text.ToString();
    }

    /// <summary>The question after a block did not compile.</summary>
    public static string AfterCompileErrors(string earlierQuestion, string answer, string errors) =>
        earlierQuestion + "\n\nYou answered this:\n" + answer + "\n\nThe code does not compile:\n" + errors + "\n\nAnswer again, with this corrected.";

    private static void AppendProgram(StringBuilder text, string role, LinkProgram program)
    {
        text.Append("Program ").Append(role).Append(": \"").Append(program.Name).Append("\"\n\n")
            .Append(program.Map).Append("\nIts source:\n```csharp\n").Append(program.Code).Append("\n```\n\n");
    }

    /// <summary>The source of the communicator: one method per block, compiled for this link and not kept.</summary>
    public static string CommunicatorSource(IReadOnlyList<LinkBlock> blocks)
    {
        var text = new StringBuilder("""
            using System;
            using System.Collections.Generic;
            using System.Globalization;
            using System.Linq;
            using System.Text;
            using System.Text.RegularExpressions;
            using Neo.App;

            """);
        text.Append("public static class ").Append(CommunicatorClass).Append("\n{\n");
        for (int i = 0; i < blocks.Count; i++)
            text.Append("    public static void Block").Append(i).Append("(ILinkSide a, ILinkSide b)\n    {\n")
                .Append(blocks[i].Code).Append("\n    }\n\n");
        text.Append("}\n");
        return text.ToString();
    }
}
