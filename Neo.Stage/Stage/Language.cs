namespace Neo.Stage;

/// <summary>
/// The language the stage listens and speaks in: German, or English when NEO_STAGE_LANGUAGE is "en" (a demo can set it
/// too). The ears, the brain's instructions and what the stage shows follow it.
/// </summary>
internal static class Language
{
    public const string Variable = "NEO_STAGE_LANGUAGE";

    public static string Code { get; set; } = Read();

    public static bool English => Code == "en";

    /// <summary>The German or the English wording, as the stage speaks.</summary>
    public static string Pick(string german, string english) => English ? english : german;

    private static string Read()
    {
        var code = Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } own ? own
            : OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(Variable, EnvironmentVariableTarget.User) : null;
        return code != null && code.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "de";
    }
}
