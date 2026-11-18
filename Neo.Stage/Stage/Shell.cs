using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Neo.Stage;

/// <summary>What a script may do, as the brain says and the stage checks.</summary>
internal enum Changes { Nothing, Undoable, Lasting }

/// <summary>
/// The brain's general hands: Windows PowerShell scripts it writes itself. What only reads runs at once; what can be
/// undone and was asked for runs at once too; what lasts (deleting, sending, installing…) runs only after the person has
/// said yes. Some things it never does, whatever it says: run as administrator, or change security and system settings.
/// Scripts that worked can be kept as skills (<see cref="Skills"/>), so the next time needs no new invention.
/// </summary>
internal static partial class Shell
{
    // A person is waiting for the answer: a script that takes longer has gone wrong (a search over a whole disk, say).
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    /// <summary>A reason not to run the script at all, or null.</summary>
    public static string? Refusal(string script)
        => Forbidden().IsMatch(script)
            ? "Das würde als Administrator laufen oder Sicherheits- oder Systemeinstellungen ändern; das tue ich nicht. Das muss der Mensch selbst machen."
            : null;

    /// <summary>What the script really does, at least: a script that deletes, sends or installs counts as lasting, whatever the brain said.</summary>
    public static Changes AtLeast(string script, Changes said)
        => said != Changes.Lasting && Lasting().IsMatch(script) ? Changes.Lasting : said;

    /// <summary>What a script wrote and how it ended: its exit code (null when it ran too long and was stopped), its output and errors.</summary>
    internal sealed record Outcome(int? Code, string Output, string Errors, TimeSpan Took);

    /// <summary>Runs the script with Windows PowerShell and returns what it wrote, shortened, and how it ended.</summary>
    public static async Task<string> RunAsync(string script, IReadOnlyDictionary<string, string>? parameters = null, CancellationToken cancellationToken = default)
    {
        var outcome = await ExecuteAsync(script, parameters, s_timeout, cancellationToken);
        if (outcome.Code is not { } code)
            return $"Abgebrochen nach {outcome.Took.TotalSeconds:0} s, ohne Ende. Bisher kam: {Short(outcome.Output)}";
        var text = new StringBuilder();
        text.Append(code == 0 ? "Fertig" : $"Beendet mit Code {code}");
        text.Append($" nach {outcome.Took.TotalSeconds:0.0} s.");
        if (Short(outcome.Output) is { Length: > 0 } written)
            text.Append(" Ausgabe: ").Append(written);
        if (Short(outcome.Errors) is { Length: > 0 } failed)
            text.Append(" Fehler: ").Append(failed);
        return text.ToString();
    }

    /// <summary>Runs the script with Windows PowerShell, stopped after <paramref name="timeout"/>, and returns what came of it untouched.</summary>
    public static async Task<Outcome> ExecuteAsync(string script, IReadOnlyDictionary<string, string>? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neo", "Stage", "scripts");
        Directory.CreateDirectory(folder);
        // Watchers run their scripts at the same time as the brain runs its own: every file a name of its own.
        var file = Path.Combine(folder, $"run-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}.ps1");
        // With a byte order mark, so that Windows PowerShell reads umlauts right; and UTF-8 out, so that they come back right.
        await File.WriteAllTextAsync(file, "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\r\n$ProgressPreference = 'SilentlyContinue'\r\n" + script,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);

        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (var argument in (string[])["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file])
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in parameters ?? new Dictionary<string, string>())
        {
            start.ArgumentList.Add("-" + name);
            start.ArgumentList.Add(value);
        }

        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            return new Outcome(null, await Settle(output), "", clock.Elapsed);
        }
        finally
        {
            try { File.Delete(file); }
            catch (IOException) { }
        }
        return new Outcome(process.ExitCode, await output, await errors, clock.Elapsed);
    }

    private static async Task<string> Settle(Task<string> output)
    {
        try { return await output.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException) { return ""; }
    }

    private static string Short(string text)
    {
        text = text.Trim();
        return text.Length <= 6000 ? text : text[..3000] + " … " + text[^3000..];
    }

    // Elevation, security and system settings: never, not even with a yes.
    [GeneratedRegex(@"-Verb\s+['""]?RunAs|\bSet-MpPreference\b|\bAdd-MpPreference\b|\bnetsh\s+advfirewall|\b(Set|New|Remove|Disable)-NetFirewall|\bbcdedit\b|\bFormat-Volume\b|\bdiskpart\b|\bcipher\s+/w|\b(Clear|Initialize)-Disk\b|\bvssadmin\s+delete|\bwbadmin\s+delete|\bSet-ExecutionPolicy\b|\breg(\.exe)?\s+(add|delete|import)\s+HKLM|HKLM:\\|HKEY_LOCAL_MACHINE|\bEnableLUA\b|\b(Disable|Enable)-WindowsOptionalFeature\b|\bSet-LocalUser\b|\bNew-LocalUser\b|\bAdd-LocalGroupMember\b|\btakeown\b|\bicacls\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();

    // Things that do not come back: deleting, moving, overwriting, sending, installing, killing, shutting down.
    [GeneratedRegex(@"\b(Remove-Item|Clear-RecycleBin|Clear-Content|Move-Item|Rename-Item|Stop-Computer|Restart-Computer|Uninstall-Package|Install-Package|Remove-AppxPackage|Send-MailMessage|Stop-Service|Set-Service|Register-ScheduledTask|Unregister-ScheduledTask|Disable-ScheduledTask|Set-ItemProperty|New-ItemProperty|Remove-ItemProperty|Stop-Process|Set-Content|Out-File)\b|(^|[\s;|(])(del|erase|rd|rmdir|rm|ri|mv|move|ren|kill|taskkill|shutdown|logoff|schtasks|winget|choco|reg)(\s|$)|-Method\s+['""]?(Post|Put|Delete|Patch)\b|\.Delete\(|\.SendMail|>\s*['""]?[A-Za-z]:",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Lasting();
}

/// <summary>A script that worked, kept under a name so that the brain can use it again (Neo/Stage/skills).</summary>
internal sealed record Skill(string Name, string Description, Changes Changes, string Script);

internal static class Skills
{
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neo", "Stage", "skills");

    public static IReadOnlyList<Skill> All()
    {
        if (!Directory.Exists(Folder))
            return [];
        var skills = new List<Skill>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json").OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (JsonSerializer.Deserialize<Skill>(File.ReadAllText(file)) is { Name.Length: > 0, Script.Length: > 0 } skill)
                    skills.Add(skill);
            }
            catch (Exception exception) when (exception is JsonException or IOException) { }
        }
        return skills;
    }

    public static Skill? Find(string name) => All().FirstOrDefault(skill => string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase));

    public static void Keep(Skill skill)
    {
        Directory.CreateDirectory(Folder);
        var file = new string([.. skill.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
        File.WriteAllText(Path.Combine(Folder, file + ".json"), JsonSerializer.Serialize(skill, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The skills as the brain reads them with every question: name and what each does.</summary>
    public static string Describe()
    {
        var all = All();
        return all.Count == 0 ? "" : "Gelernte Fähigkeiten (mit run und skill nutzbar):" + string.Concat(all.Select(skill => $"{Environment.NewLine}- {skill.Name}: {skill.Description}"));
    }
}
