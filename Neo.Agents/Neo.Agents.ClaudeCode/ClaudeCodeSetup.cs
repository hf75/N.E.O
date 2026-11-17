using System.Collections;
using Neo.ClaudeAgent;

namespace Neo.Agents
{
    /// <summary>Claude Code is switched off, missing, or cannot be started. The message says what to do.</summary>
    public sealed class ClaudeCodeUnavailableException(string message) : InvalidOperationException(message);

    /// <summary>The program that runs Claude Code, and the arguments that come before Claude Code's own.</summary>
    internal sealed record ClaudeCodeCommand(string FileName, IReadOnlyList<string> Arguments);

    /// <summary>
    /// Whether N.E.O. may use Claude Code, and how to start it. Claude Code works on this computer on its own,
    /// for minutes, so it stays off until the user sets <see cref="SwitchVariable"/>. Finding Claude Code and the
    /// environment it starts with are <see cref="ClaudeCli"/>'s, shared with the live sessions of Neo.ClaudeAgent.
    /// </summary>
    public static class ClaudeCodeSetup
    {
        /// <summary>
        /// Switches Claude Code on. Any value uses the newest Claude Code found: the claude on the PATH or a copy the
        /// Claude desktop app keeps. The path of claude.exe, claude.cmd, Claude Code's cli.js or a .ps1 script uses that file.
        /// </summary>
        public const string SwitchVariable = "NEO_CLAUDE_CODE";

        public static bool IsSwitchedOn => ReadVariable(SwitchVariable) != null;

        /// <summary>
        /// The process's own value first: a parent program or a test can set it there. Then the user's: N.E.O. keeps
        /// its keys among the user's variables, and one set after N.E.O. started counts without a restart.
        /// </summary>
        internal static string? ReadVariable(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows())
                value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        internal static ClaudeCodeCommand FindCommand()
            => FindCommand(ReadVariable(SwitchVariable), SearchFolders(), ClaudeCli.DesktopAppFolders());

        /// <summary>Only the given folders: the desktop app's copies are not looked at (tests).</summary>
        internal static ClaudeCodeCommand FindCommand(string? switchValue, IReadOnlyList<string> searchFolders)
            => FindCommand(switchValue, searchFolders, []);

        internal static ClaudeCodeCommand FindCommand(string? switchValue, IReadOnlyList<string> searchFolders, IReadOnlyList<string> desktopAppFolders)
        {
            if (string.IsNullOrWhiteSpace(switchValue))
                throw new ClaudeCodeUnavailableException(
                    $"Claude Code is switched off. Set the environment variable {SwitchVariable} to 1 to use the claude command, or to the path of claude.");

            var value = Environment.ExpandEnvironmentVariables(switchValue.Trim().Trim('"'));
            try
            {
                ClaudeCliCommand found;
                if (LooksLikeAPath(value))
                {
                    if (!File.Exists(value))
                        throw new ClaudeCodeUnavailableException($"{SwitchVariable} names {value}, and there is no such file.");
                    found = ClaudeCli.FromFile(Path.GetFullPath(value), searchFolders);
                }
                else
                {
                    found = ClaudeCli.Find(null, searchFolders, desktopAppFolders);
                }
                return new ClaudeCodeCommand(found.FileName, found.Arguments);
            }
            catch (ClaudeCliNotFoundException exception)
            {
                throw new ClaudeCodeUnavailableException($"{exception.Message} Or set {SwitchVariable} to the path of claude.");
            }
        }

        private static bool LooksLikeAPath(string value)
            => value.IndexOfAny(['\\', '/', ':']) >= 0
               || Path.GetExtension(value).ToLowerInvariant() is ".exe" or ".cmd" or ".bat" or ".js" or ".cjs" or ".mjs" or ".ps1";

        /// <summary>Where to look for claude and node: the PATH, and where the installers put them.</summary>
        internal static IReadOnlyList<string> SearchFolders() => ClaudeCli.SearchFolders();

        /// <summary>The environment Claude Code starts with: this program's, without what ties it to a Claude Code session.</summary>
        internal static Dictionary<string, string> ChildEnvironment() => ClaudeCli.ChildEnvironment();

        internal static Dictionary<string, string> ChildEnvironment(IDictionary current, Func<string, string?> usersOwn)
            => ClaudeCli.ChildEnvironment(current, usersOwn);
    }
}
