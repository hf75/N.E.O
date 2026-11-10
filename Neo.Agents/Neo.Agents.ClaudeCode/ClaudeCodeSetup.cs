using System.Collections;
using System.Text.RegularExpressions;

namespace Neo.Agents
{
    /// <summary>Claude Code is switched off, missing, or cannot be started. The message says what to do.</summary>
    public sealed class ClaudeCodeUnavailableException(string message) : InvalidOperationException(message);

    /// <summary>The program that runs Claude Code, and the arguments that come before Claude Code's own.</summary>
    internal sealed record ClaudeCodeCommand(string FileName, IReadOnlyList<string> Arguments);

    /// <summary>
    /// Whether N.E.O. may use Claude Code, and how to start it. Claude Code works on this computer on its own,
    /// for minutes, so it stays off until the user sets <see cref="SwitchVariable"/>.
    /// </summary>
    public static class ClaudeCodeSetup
    {
        /// <summary>
        /// Switches Claude Code on. Any value uses the claude command found on the PATH; the path of claude.exe,
        /// claude.cmd, Claude Code's cli.js or a .ps1 script uses that file.
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

        internal static ClaudeCodeCommand FindCommand() => FindCommand(ReadVariable(SwitchVariable), SearchFolders());

        internal static ClaudeCodeCommand FindCommand(string? switchValue, IReadOnlyList<string> searchFolders)
        {
            if (string.IsNullOrWhiteSpace(switchValue))
                throw new ClaudeCodeUnavailableException(
                    $"Claude Code is switched off. Set the environment variable {SwitchVariable} to 1 to use the claude command, or to the path of claude.");

            var value = Environment.ExpandEnvironmentVariables(switchValue.Trim().Trim('"'));
            if (LooksLikeAPath(value))
            {
                if (!File.Exists(value))
                    throw new ClaudeCodeUnavailableException($"{SwitchVariable} names {value}, and there is no such file.");
                return FromFile(Path.GetFullPath(value), searchFolders);
            }

            var names = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : new[] { "claude" };
            foreach (var folder in searchFolders)
            {
                foreach (var name in names)
                {
                    var candidate = Path.Combine(folder, name);
                    if (File.Exists(candidate))
                        return FromFile(candidate, searchFolders);
                }
            }

            throw new ClaudeCodeUnavailableException(
                $"Claude Code was not found. Install it (npm install -g @anthropic-ai/claude-code), or set {SwitchVariable} to the path of claude.");
        }

        private static bool LooksLikeAPath(string value)
            => value.IndexOfAny(['\\', '/', ':']) >= 0
               || Path.GetExtension(value).ToLowerInvariant() is ".exe" or ".cmd" or ".bat" or ".js" or ".cjs" or ".mjs" or ".ps1";

        private static ClaudeCodeCommand FromFile(string file, IReadOnlyList<string> searchFolders)
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            var folder = Path.GetDirectoryName(file)!;

            if (extension is ".js" or ".cjs" or ".mjs")
                return new(FindNode(folder, searchFolders), [file]);
            if (extension == ".exe")
                return new(file, []);

            // npm puts claude.cmd, claude.ps1 and a shell script beside node_modules. They all start the same
            // target, mostly node with cli.js. Started without them, there is no command shell in between to
            // misread a quote or an ampersand in what N.E.O. passes on.
            if (ShimTarget(file) is { } target)
            {
                return Path.GetExtension(target).ToLowerInvariant() is ".js" or ".cjs" or ".mjs"
                    ? new(FindNode(folder, searchFolders), [target])
                    : new(target, []);
            }

            if (extension == ".ps1")
            {
                var powerShell = OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
                    : "pwsh";
                return new(powerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file]);
            }

            if (extension == "" && OperatingSystem.IsWindows() && File.Exists(Path.ChangeExtension(file, ".cmd")))
                return FromFile(Path.ChangeExtension(file, ".cmd"), searchFolders);

            if (!OperatingSystem.IsWindows())
                return new(file, []);

            throw new ClaudeCodeUnavailableException(
                $"{file} cannot be started directly. Set {SwitchVariable} to the path of claude.exe or of Claude Code's cli.js.");
        }

        // npm's launchers name their target relative to their own folder: %dp0%\... in a .cmd, $basedir/... in the others.
        private static readonly Regex s_shimTarget = new(@"(?:%dp0%\\|\$basedir/)(?<path>[^""%$\r\n]+)", RegexOptions.CultureInvariant);

        private static string? ShimTarget(string shim)
        {
            string text;
            try
            {
                if (new FileInfo(shim).Length > 64 * 1024)
                    return null;
                text = File.ReadAllText(shim);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            var folder = Path.GetDirectoryName(shim)!;
            var target = s_shimTarget.Matches(text)
                .Select(match => match.Groups["path"].Value.Trim())
                .Where(path => Path.GetFileNameWithoutExtension(path) != "node")
                .Select(path => Path.GetFullPath(Path.Combine(folder, path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))))
                .LastOrDefault(File.Exists);
            if (target != null)
                return target;

            var cli = Path.Combine(folder, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
            return File.Exists(cli) ? cli : null;
        }

        private static string FindNode(string besides, IReadOnlyList<string> searchFolders)
        {
            var name = OperatingSystem.IsWindows() ? "node.exe" : "node";
            foreach (var folder in searchFolders.Prepend(besides))
            {
                var candidate = Path.Combine(folder, name);
                if (File.Exists(candidate))
                    return candidate;
            }

            if (OperatingSystem.IsWindows())
            {
                var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", name);
                if (File.Exists(installed))
                    return installed;
            }

            throw new ClaudeCodeUnavailableException(
                "This Claude Code needs Node.js, and node was not found. Install Node.js, or Claude Code's native build (claude install).");
        }

        /// <summary>Where to look for claude and node: the PATH, and where the installers put them.</summary>
        internal static IReadOnlyList<string> SearchFolders()
        {
            var folders = new List<string>();
            void Add(string? list)
            {
                if (string.IsNullOrWhiteSpace(list))
                    return;
                foreach (var entry in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    folders.Add(Environment.ExpandEnvironmentVariables(entry.Trim('"')));
            }

            Add(Environment.GetEnvironmentVariable("PATH"));
            if (OperatingSystem.IsWindows())
            {
                // A program started before Claude Code was installed has the PATH of back then.
                Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
                Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
                folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            }
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));

            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            return folders.Where(folder => folder.Length > 0 && Directory.Exists(folder)).Distinct(comparer).ToList();
        }

        /// <summary>The environment Claude Code starts with: this program's, without what ties it to a Claude Code session.</summary>
        internal static Dictionary<string, string> ChildEnvironment()
            => ChildEnvironment(Environment.GetEnvironmentVariables(), UsersOwnVariable);

        internal static Dictionary<string, string> ChildEnvironment(IDictionary current, Func<string, string?> usersOwn)
        {
            var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (DictionaryEntry entry in current)
                environment[(string)entry.Key] = entry.Value as string ?? "";

            // A Claude Code session hands these to every program it starts, N.E.O. included when that session
            // started it. A Claude Code of our own must not take them over: they tie it to that session, and
            // ANTHROPIC_BASE_URL would send the user's key wherever it points.
            if (environment.ContainsKey("CLAUDECODE"))
            {
                foreach (var name in environment.Keys.Where(BelongsToASession).ToList())
                {
                    // What the user set for themselves stays, such as where Git Bash is.
                    if (usersOwn(name) is { Length: > 0 } own)
                        environment[name] = own;
                    else
                        environment.Remove(name);
                }
            }

            // N.E.O. keeps the API key among the user's variables; a program started before it was set lacks it.
            if (!environment.ContainsKey("ANTHROPIC_API_KEY") && usersOwn("ANTHROPIC_API_KEY") is { Length: > 0 } key)
                environment["ANTHROPIC_API_KEY"] = key;

            return environment;
        }

        private static bool BelongsToASession(string name)
            => name.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("CLAUDE_AGENT_SDK_", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("CLAUDE_PREVIEW_", StringComparison.OrdinalIgnoreCase)
               || name.Equals("CLAUDE_PID", StringComparison.OrdinalIgnoreCase)
               || name.Equals("CLAUDE_EFFORT", StringComparison.OrdinalIgnoreCase)
               || name.Equals("ANTHROPIC_BASE_URL", StringComparison.OrdinalIgnoreCase)
               || name.Equals("ANTHROPIC_AUTH_TOKEN", StringComparison.OrdinalIgnoreCase);

        private static string? UsersOwnVariable(string name)
        {
            if (!OperatingSystem.IsWindows())
                return null;
            return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                   ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        }
    }
}
