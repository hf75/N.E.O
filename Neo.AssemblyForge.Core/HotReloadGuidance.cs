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
        "To reach a control later, keep it in a field and assign that field in the UI method. Never look it up by name: for controls created in code, FindControl and FindName silently return null.",
        "Put one-time set-up — starting timers, subscribing to objects held in fields, starting background work — in the constructor, not in the UI method, because the UI method may run many times. Keep every timer in a field.",
        "A method that is running keeps its old code until it is called again, so a loop that runs as long as the app does never gets a change made to it. Keep such a loop small: it only waits and calls a method for each round, and all the work is in that method, not in the loop. A round that is under way during an update can fail once, so the loop catches that and goes on: while (...) { try { await RenderFrameAsync(); } catch (Exception ex) { Debug.WriteLine(ex); } await Task.Delay(...); }",
        "When changing existing code, keep the names and types of existing fields, properties and methods and add new members instead of renaming. A renamed or retyped field loses its value, and a changed base class or interface list forces a full restart.",
        "Leave the signatures and modifiers of existing methods exactly as they are: do not add or remove static, async, virtual or override, do not change public/private, and do not change parameters. Changing a method body is fine; anything else about an existing method forces a full restart. If a method has to behave differently, change its body or add a new method.",
        "Optional: a parameterless method OnHotReloaded() is called after each live update.",
    ];

    /// <summary><see cref="Rules"/> as one paragraph, for the single-string system prompts.</summary>
    public static string Notes => "LIVE UPDATES: " + string.Concat(Rules.Select(r => "• " + r + " "));
}
