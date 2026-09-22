namespace Neo.AssemblyForge;

/// <summary>
/// What the model is told about the app network: every running N.E.O. app on the machine can offer
/// methods and values, and any app can use another one's. One source for the desktop hosts and the
/// MCP server, next to <see cref="HotReloadGuidance"/>.
/// </summary>
public static class AppNetworkGuidance
{
    public static IReadOnlyList<string> Rules { get; } =
    [
        "Every N.E.O. app on this machine is on the app network: it can offer methods and values to the other apps, and it can use theirs. The API is in the Neo.App namespace (add `using Neo.App;`).",
        "Give every app a name: put `[AppInfo(\"Name\", \"what it is for\")]` on the control class — a short name the user would use for it (\"Sales\", \"Inventory\", \"Notes\"). Other apps and the user refer to it by that name.",
        "Offer what other apps could want: mark public instance methods with `[McpCallable(\"what it does\")]` and public read-only properties with `[McpObservable(\"what it is\")]`. Offered methods run on this app's UI thread; parameters and results must be plain data (strings, numbers, bools, lists, records) — never controls.",
        "Use another app when the user asks for it or names one: `await Apps.CallAsync<T>(\"App\", \"Method\", arg1, arg2)` calls a method and returns its result, `await Apps.CallAsync(\"App\", \"Method\", …)` when there is no result, `await Apps.ReadAsync<T>(\"App\", \"Value\")` reads a value, and `Apps.Watch<T>(\"App\", \"Value\", v => …)` runs the callback on this UI thread now and whenever the value changes (keep the returned IDisposable in a field). `Apps.IsRunning(\"App\")` tells whether an app is there.",
        "Always await these calls (they cross processes) — never .Result or .Wait() on the UI thread. Wrap them in try/catch: AppNotFoundException means the other app is not running, AppCallException carries the reason it gave. Show the user a short message in the UI in either case; do not let the app crash over another app.",
        "Only the apps listed under RUNNING APPS exist right now; use exactly their names, method names and parameter lists. If the user asks for an app that is not listed, tell them it is not running and write the code so it works once the app is started (Watch resumes by itself, calls fail with a message until then).",
    ];

    /// <summary><see cref="Rules"/> as one paragraph, for the single-string system prompts.</summary>
    public static string Notes => "APP NETWORK: " + string.Concat(Rules.Select(r => "• " + r + " "));

    /// <summary>
    /// The section that lists the apps running right now, from their descriptions
    /// (<c>AppDescriptor.Describe()</c>): name, methods with parameters and results, values.
    /// </summary>
    public static string RunningApps(IReadOnlyList<string> descriptions)
    {
        if (descriptions.Count == 0)
            return " RUNNING APPS: none besides this one right now.";
        return " RUNNING APPS (other N.E.O. apps on this machine this app can use, exactly as listed):\n"
             + string.Join("\n", descriptions.Select(d => "- " + d.Replace("\n", "\n  "))) + "\n";
    }
}
