namespace Neo.McpServer.Services;

/// <summary>
/// The Claude bridge (Bridge/ClaudeBridge.cs): an app this server runs in a window of its own, docked to the Claude
/// desktop app, that types the prompts of Ai.Trigger into this session. In the terminal, channels do that; the desktop
/// app does not take them. Its source goes in with the ForeignApp helper, so it needs nothing of the window that runs it.
/// </summary>
internal static class ClaudeBridgeApp
{
    public const string WindowId = "claude-bridge";

    public static IReadOnlyList<string> Sources { get; } = [Read("ClaudeBridge.cs"), Read("ForeignApp.cs")];

    public static bool Is(string? windowId) => string.Equals(windowId, WindowId, StringComparison.OrdinalIgnoreCase);

    private static string Read(string name)
    {
        using var stream = typeof(ClaudeBridgeApp).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is missing from Neo.McpServer's resources.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
