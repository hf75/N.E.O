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
    /// <param name="ownWidth">How wide this program's window is; with <paramref name="otherWidth"/> that gives each part its share.</param>
    public static string ForTheModel(string name, string prefix, string code, IEnumerable<string> packages, double? ownWidth = null, double otherWidth = 0)
    {
        var columns = ownWidth is > 0 && otherWidth > 0
            ? $"two star columns in the proportion {Math.Round(ownWidth.Value)}:{Math.Round(otherWidth)}"
            : "two columns of equal width";

        var text = new StringBuilder();
        text.Append($"""


            The user dragged another program, "{name}", onto this one, to make one program of the two. Merge it in:
            - This program keeps everything it can do, and its data.
            - Add everything the other program can do, complete and working.
            - Then connect them: the user put exactly these two together for a reason. Where one holds or produces something the other can use, let the other use it, live, so that a change in one shows in the other at once, even if that changes how it looks. For example, an expense list and a currency converter: the amounts appear converted. Do not add new functions for this, and say in the explanation what you connected.
            - Lay them out side by side in a Grid with {columns}, this program left and the other right, each laid out the way it is now: when this is done, the window grows by the width of the other program's window, so each keeps the room it has now and neither needs to be made smaller. Set ClipToBounds on what fills each column, so that neither can ever lie over the other. A part that is a form or a list may go into a ScrollViewer of its own; one that fills the space it gets (a picture, a canvas, a chart) must not, because it has no size there.
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
