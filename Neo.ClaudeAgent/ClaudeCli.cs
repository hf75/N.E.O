using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Neo.ClaudeAgent;

/// <summary>Claude Code is missing or cannot be started. The message says what to do.</summary>
public sealed class ClaudeCliNotFoundException(string message) : InvalidOperationException(message);

/// <summary>The program that runs Claude Code, the arguments that come before Claude Code's own, and its version when it can be told.</summary>
public sealed record ClaudeCliCommand(string FileName, IReadOnlyList<string> Arguments, Version? Version = null);

/// <summary>
/// Where Claude Code is on this computer, and the environment it starts with.
/// Old builds fail with the current models (2.1.111 sends a thinking setting they refuse), so the newest
/// one wins: the claude on the PATH (npm or the native installer) or a copy the Claude desktop app keeps.
/// </summary>
public static class ClaudeCli
{
    /// <summary>The given file, or else the newest Claude Code on the PATH and among the Claude desktop app's copies.</summary>
    public static ClaudeCliCommand Find(string? path = null) => Find(path, SearchFolders(), DesktopAppFolders());

    internal static ClaudeCliCommand Find(string? path, IReadOnlyList<string> searchFolders, IReadOnlyList<string> desktopAppFolders)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var file = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!File.Exists(file))
                throw new ClaudeCliNotFoundException($"{file}: there is no such file.");
            return FromFile(Path.GetFullPath(file), searchFolders);
        }

        var candidates = new List<ClaudeCliCommand>();
        if (OnPath(searchFolders) is { } onPath)
            candidates.Add(onPath);
        candidates.AddRange(DesktopAppCopies(desktopAppFolders));
        if (candidates.Count == 0)
        {
            throw new ClaudeCliNotFoundException(
                "Claude Code was not found. Install it (npm install -g @anthropic-ai/claude-code), or the Claude desktop app.");
        }
        return Newest(candidates);
    }

    /// <summary>The claude on the PATH, without looking anywhere else; null when there is none.</summary>
    internal static ClaudeCliCommand? OnPath(IReadOnlyList<string> searchFolders)
    {
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
        return null;
    }

    // The newest wins. The one on the PATH, which comes first, wins a tie and wins when its version cannot be told:
    // the user installed it on purpose.
    private static ClaudeCliCommand Newest(IReadOnlyList<ClaudeCliCommand> candidates)
    {
        var best = candidates[0];
        foreach (var candidate in candidates.Skip(1))
        {
            if (best.Version != null && candidate.Version != null && candidate.Version > best.Version)
                best = candidate;
        }
        return best;
    }

    /// <summary>How to start the given file: an exe as it is, cli.js with node, an npm launcher through its target, a .ps1 with PowerShell.</summary>
    internal static ClaudeCliCommand FromFile(string file, IReadOnlyList<string> searchFolders)
    {
        var extension = Path.GetExtension(file).ToLowerInvariant();
        var folder = Path.GetDirectoryName(file)!;

        if (extension is ".js" or ".cjs" or ".mjs")
            return new(FindNode(folder, searchFolders), [file], NpmVersion(file));
        if (extension == ".exe")
            return new(file, [], NpmVersion(file) ?? DesktopAppVersion(file));

        // npm puts claude.cmd, claude.ps1 and a shell script beside node_modules. They start the same target:
        // node with cli.js in older releases, bin/claude.exe since Claude Code is built natively. Started without
        // them, there is no command shell in between to misread a quote or an ampersand in what is passed on.
        if (ShimTarget(file) is { } target)
        {
            return Path.GetExtension(target).ToLowerInvariant() is ".js" or ".cjs" or ".mjs"
                ? new(FindNode(folder, searchFolders), [target], NpmVersion(target))
                : new(target, [], NpmVersion(target));
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

        throw new ClaudeCliNotFoundException($"{file} cannot be started directly. Name claude.exe or Claude Code's cli.js instead.");
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

        var package = Path.Combine(folder, "node_modules", "@anthropic-ai", "claude-code");
        foreach (var candidate in new[] { Path.Combine(package, "bin", OperatingSystem.IsWindows() ? "claude.exe" : "claude"), Path.Combine(package, "cli.js") })
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
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

        throw new ClaudeCliNotFoundException(
            "This Claude Code needs Node.js, and node was not found. Install Node.js, or a current Claude Code (npm install -g @anthropic-ai/claude-code).");
    }

    // npm: the version is in the package.json of @anthropic-ai/claude-code, above cli.js or bin/claude.exe.
    private static Version? NpmVersion(string file)
    {
        var folder = Path.GetDirectoryName(file);
        for (var level = 0; level < 3 && folder != null; level++, folder = Path.GetDirectoryName(folder))
        {
            var manifest = Path.Combine(folder, "package.json");
            if (!File.Exists(manifest))
                continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                if (document.RootElement.TryGetProperty("name", out var name) && name.GetString() == "@anthropic-ai/claude-code"
                    && document.RootElement.TryGetProperty("version", out var version))
                    return ParseVersion(version.GetString());
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { }
            return null;
        }
        return null;
    }

    // The desktop app: claude-code\<version>\<build>\claude.exe.
    private static Version? DesktopAppVersion(string file)
    {
        var build = Path.GetDirectoryName(file);
        var version = build == null ? null : Path.GetDirectoryName(build);
        return version != null && Path.GetFileName(Path.GetDirectoryName(version)) == "claude-code"
            ? ParseVersion(Path.GetFileName(version))
            : null;
    }

    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var plain = text.Split('-', '+')[0];
        return Version.TryParse(plain, out var version) ? version : null;
    }

    /// <summary>The claude-code folders of the Claude desktop app: the Store app keeps them under Claude-Data.</summary>
    internal static IReadOnlyList<string> DesktopAppFolders()
    {
        if (!OperatingSystem.IsWindows())
            return [];
        var folders = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var data = Path.Combine(local, "Claude-Data");
        if (Directory.Exists(data))
        {
            try { folders.AddRange(Directory.EnumerateDirectories(data).Select(app => Path.Combine(app, "claude-code"))); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        folders.Add(Path.Combine(local, "Claude", "claude-code"));
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code"));
        return folders.Where(Directory.Exists).ToList();
    }

    /// <summary>Every claude.exe the desktop app keeps (claude-code\&lt;version&gt;\&lt;build&gt;\claude.exe), newest first.</summary>
    internal static IReadOnlyList<ClaudeCliCommand> DesktopAppCopies(IReadOnlyList<string> desktopAppFolders)
    {
        var copies = new List<ClaudeCliCommand>();
        foreach (var root in desktopAppFolders)
        {
            IEnumerable<string> versions;
            try { versions = Directory.EnumerateDirectories(root).ToList(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }

            foreach (var versionFolder in versions)
            {
                if (ParseVersion(Path.GetFileName(versionFolder)) is not { } version)
                    continue;
                string? exe = null;
                try
                {
                    exe = Directory.EnumerateDirectories(versionFolder)
                        .Select(build => Path.Combine(build, "claude.exe"))
                        .Where(File.Exists)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                if (exe != null)
                    copies.Add(new(exe, [], version));
            }
        }
        return copies.OrderByDescending(copy => copy.Version).ToList();
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
    public static Dictionary<string, string> ChildEnvironment()
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
