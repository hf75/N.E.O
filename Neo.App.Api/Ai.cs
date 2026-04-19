namespace Neo.App;

/// <summary>
/// Bridge between a running generated Neo app and Claude.
///
/// When generated code calls <see cref="Trigger(string)"/>, a structured prompt is pushed
/// to Claude via the MCP channel mechanism — Claude automatically starts a new turn and
/// executes whatever the prompt asks for.
///
/// The host process (Neo.PluginWindow) wires up the emitter at startup via
/// <see cref="SetEmitter"/>. Generated code should NOT call SetEmitter.
///
/// <para>
/// Named <c>Ai</c> (not <c>Neo</c>) to avoid a namespace-vs-type collision: the root
/// <c>Neo</c> namespace exists in the compilation context (from Neo.Shared, Neo.IPC, etc.),
/// so a class called <c>Neo</c> would create ambiguity at call sites like <c>Neo.Trigger(...)</c>.
/// </para>
/// </summary>
public static class Ai
{
    private static Action<string>? _emitter;

    /// <summary>
    /// Wired by the host process at startup. Generated code must not call this.
    /// </summary>
    public static void SetEmitter(Action<string> emitter) => _emitter = emitter;

    /// <summary>
    /// Send a structured prompt to Claude, triggering a new turn.
    ///
    /// The prompt is a complete instruction — Claude executes it as if the user had typed it.
    /// Include all relevant context from the current app state (selected items, text input values,
    /// counters, etc.) by interpolating them directly into the prompt string.
    ///
    /// Example:
    /// <code>
    ///   private void OnResearchClick(object sender, RoutedEventArgs e)
    ///   {
    ///       var country = CountryCombo.SelectedItem?.ToString() ?? "unknown";
    ///       Ai.Trigger(
    ///           $"Research vacation destination {country}. " +
    ///           $"Write the best travel season with set_property into the TextBlock named 'resultText'.");
    ///   }
    /// </code>
    /// </summary>
    /// <param name="prompt">
    /// A complete instruction for Claude. Interpolate app state directly so Claude has
    /// all the context needed to act.
    /// </param>
    public static void Trigger(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        _emitter?.Invoke(prompt);
    }

    /// <summary>
    /// Schedule a trigger after a delay. Non-blocking.
    ///
    /// Example:
    /// <code>
    ///   Ai.ScheduleTrigger(TimeSpan.FromSeconds(10),
    ///       "Ten seconds are up. Update the status label to 'Timeout'.");
    /// </code>
    /// </summary>
    public static void ScheduleTrigger(TimeSpan delay, string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        _ = Task.Delay(delay).ContinueWith(_ => _emitter?.Invoke(prompt),
            TaskScheduler.Default);
    }
}
