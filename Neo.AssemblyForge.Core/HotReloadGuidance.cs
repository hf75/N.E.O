namespace Neo.AssemblyForge;

/// <summary>
/// What a model has to know so that its app survives the next change: N.E.O. patches new code
/// into the running app (Neo.HotReload) instead of restarting it, and that only keeps the user's
/// state when the code follows a few conventions — above all a re-runnable UI method, which the
/// plugin window calls again after an update. One text for the desktop hosts and the MCP server;
/// the Web App reloads in the browser and does not use it.
/// </summary>
public static class HotReloadGuidance
{
    /// <summary>Method names the hot reload engine recognises as "builds the UI"; keep in sync with Neo.HotReload.</summary>
    public const string PreferredUiMethod = "BuildUi";

    public static IReadOnlyList<string> Rules { get; } =
    [
        "The app keeps running while the user asks for the next change: N.E.O. patches the new code into the running app instead of restarting it, so the user keeps what they loaded, typed and selected. Write the code so that this works.",
        "Build the complete UI in one parameterless method — BuildUi(), or InitializeComponents() where the code already has that — and let the constructor (or the existing Loaded/Initialized handler) call it. After a code change N.E.O. calls it again on the running instance, so it has to rebuild the whole visual tree from the fields every time: create all controls inside it, and never clear or reload data in it.",
        "Keep everything the user did or loaded in fields of the control (loaded data, the path of an opened file, lists, the current selection), not only in controls. Fields keep their values across updates; the controls are rebuilt.",
        "Put one-time set-up — starting timers, subscribing to objects held in fields, starting background work — in the constructor, not in the UI method, because the UI method may run many times. Keep every timer in a field.",
        "When changing existing code, keep the names and types of existing fields, properties and methods and add new members instead of renaming. A renamed or retyped field loses its value, and a changed base class or interface list forces a full restart.",
        "Optional: a parameterless method OnHotReloaded() is called after each live update.",
    ];

    /// <summary><see cref="Rules"/> as one paragraph, for the single-string system prompts.</summary>
    public static string Notes => "LIVE UPDATES: " + string.Concat(Rules.Select(r => "• " + r + " "));
}
