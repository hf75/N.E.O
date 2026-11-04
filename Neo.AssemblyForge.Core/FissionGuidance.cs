namespace Neo.AssemblyForge;

/// <summary>
/// What the model is told when the user tears a part out of a running program with the mouse: the part becomes a program
/// of its own, and the program it came from goes on without it and gives it its data over the app network.
/// </summary>
public static class FissionGuidance
{
    /// <summary>What goes into the chat and the history of the program the part is torn out of.</summary>
    public static string ChatPrompt(string newName) => $"Tear the part \"{newName}\" out of this app, as a program of its own.";

    /// <summary>For the program the part is torn out of, to go after the prompt.</summary>
    /// <param name="part">Which part, in words: its type, name and place.</param>
    /// <param name="map">Its controls as the window describes them, texts not in the source only as their shapes.</param>
    public static string ForTheSource(string sourceName, string newName, string part, string map) => $"""


        The user tore one part out of this program with the mouse, to make it a program of its own, "{newName}". The part: {part}.
        Its controls as they are now (with their addresses; a text in ~"…" is only the shape of what it shows):
        ```
        {map}
        ```
        Change this program:
        - Take that part out of the UI, and let the rest close the gap.
        - The new program runs on its own and gets everything the part showed or did from this program, over the app network: give this program [AppInfo("{sourceName}", "<what it is for>")] if it has no [AppInfo] (keep its name if it has one), a public [McpObservable("…")] property for every value the part showed, kept up to date by what changes it, and a public [McpCallable("…")] method for every action of the part, with Changes = true where it changes this program's data.
        - Keep everything else this program can do, with its data.
        """;

    /// <summary>The prompt for the new program, written in a session of its own.</summary>
    /// <param name="sourceBefore">The source of the program before the part was torn out, to see how it looked and what it did.</param>
    public static string ForTheNewProgram(string sourceName, string newName, string part, string map, string sourceBefore) => $"""
        Write the program "{newName}". It is a part the user tore out of the running program "{sourceName}" with the mouse: {part}.
        It shows and does exactly what that part did, and looks the same, alone in its window. Its data stays in "{sourceName}", which goes on running: get every value with Apps.Watch and do every action with Apps.CallAsync, with exactly the names "{sourceName}" offers in RUNNING APPS. While "{sourceName}" is not running, say so in the window, and go on when it is back. Put [AppInfo("{newName}", "<what it is for>")] on the class. Nothing more: the user wants that part, alone.

        The part's controls as they were (with their addresses; a text in ~"…" is only the shape of what it showed):
        ```
        {map}
        ```

        The source of "{sourceName}" before the part was torn out:
        ```csharp
        {sourceBefore}
        ```
        """;

    /// <summary>A name for the new program from what the part is called or shows: "expenseChart" becomes "Expense chart".</summary>
    public static string NameFor(string? controlName, string? firstText, string type)
    {
        if (!string.IsNullOrWhiteSpace(controlName))
        {
            var words = System.Text.RegularExpressions.Regex.Replace(controlName.Trim('_'), "(?<=[a-z0-9])(?=[A-Z])", " ").Replace('_', ' ').Trim();
            if (words.Length > 0) return char.ToUpperInvariant(words[0]) + words[1..].ToLowerInvariant();
        }
        if (!string.IsNullOrWhiteSpace(firstText) && firstText.Trim().Length <= 40) return firstText.Trim().TrimEnd(':');
        return type;
    }
}
