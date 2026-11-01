using System.Text;

namespace Neo.AssemblyForge;

/// <summary>What the model is told when the user drags one program onto another.</summary>
public static class FusionGuidance
{
    /// <summary>What goes into the chat and the history: the source of the other program goes to the model only.</summary>
    public static string ChatPrompt(string name) => $"Merge \"{name}\" into this app.";

    /// <summary>
    /// Where this program already has a field of the same name, the other one's gets this in front. Tells
    /// the running data later where to go, so it must be what the model was told.
    /// </summary>
    public static string PrefixFor(string name)
    {
        var letters = new string(name.Where(char.IsAsciiLetter).Take(12).ToArray()).ToLowerInvariant();
        return letters.Length > 0 ? letters : "other";
    }

    /// <summary>The instruction with the source of the other program, to go after the prompt.</summary>
    public static string ForTheModel(string name, string prefix, string code, IEnumerable<string> packages)
    {
        var text = new StringBuilder();
        text.Append($"""


            The user dragged another program, "{name}", onto this one, to make one program of the two. Merge it in:
            - This program keeps everything it does and shows, and its data.
            - Add everything the other program does and shows, complete and working. Connect the two only where it is obvious.
            - Lay them out side by side in a Grid with two columns of equal width, this program left and the other right, each looking the way it looks now: when this is done, the window grows by the width of the other program's window, so neither needs to be made smaller. Put each one into a ScrollViewer of its own that scrolls both ways when needed, so that neither can ever lie over the other.
            - Keep every field of the other program under its exact name, because its running data moves over by name. Only where this program already has a field of that name, put "{prefix}_" in front of the whole name.
            - Keep what the other program's constructor does, and build its UI in the same method that builds this program's UI.
            - Keep its file paths as they are, and list its NuGet packages too.

            The other program's source:

            ```csharp
            {code}
            ```
            """);

        var list = packages.ToList();
        if (list.Count > 0)
            text.Append("\n\nIts NuGet packages: ").Append(string.Join(", ", list)).Append('.');
        return text.ToString();
    }
}
