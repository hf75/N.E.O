using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Neo.ClaudeAgent;

namespace Neo.Stage;

/// <summary>
/// The crew ("Bautrupp"): big jobs the brain hands on, done by a Claude Code session of their own in the background
/// while the person goes on talking to the stage. A real program, a service, a report over many files: whatever is
/// more than a small app or takes longer than a minute. Each job works in a folder of its own and may write only there;
/// commands that delete, move, overwrite, send or install are refused by the same rules as the stage's own scripts,
/// and nobody is asked anything (nobody would answer). When it is done the stage says so aloud, and the brain hears
/// what it made and where.
/// </summary>
internal sealed class Crew
{
    /// <summary>The model the crew works with, when not the default (Claude Opus 5.5).</summary>
    public const string ModelVariable = "NEO_STAGE_CREW_MODEL";
    private const int MostAtOnce = 2;
    private static readonly TimeSpan s_longest = TimeSpan.FromMinutes(30);
    private readonly List<Job> _jobs = [];

    private sealed class Job(string name, string task, string folder)
    {
        public string Name { get; } = name;
        public string Task { get; } = task;
        public string Folder { get; } = folder;
        public DateTime Started { get; } = DateTime.Now;
        public CancellationTokenSource Stop { get; } = new();
        public string Doing = "";
        public int Steps;
        public DateTime? Finished;
        public string? Result;
        public bool Succeeded;
        public double Cost;
    }

    /// <summary>A job is done, or ended without being done: who, the sentence the stage says, and the whole of it for the brain.</summary>
    public event Action<string, string, string>? Done;

    /// <summary>Starts a job in the background; the answer is for the brain.</summary>
    public string Start(string name, string task)
    {
        lock (_jobs)
        {
            if (_jobs.Count(job => job.Finished == null) >= MostAtOnce)
                return Language.Pick($"Der Bautrupp hat schon {MostAtOnce} Aufträge in Arbeit; erst wenn einer fertig ist, nimmt er den nächsten.",
                    $"The crew already has {MostAtOnce} jobs in hand; it takes the next when one is done.");
        }
        var slug = new string([.. name.Select(c => char.IsLetterOrDigit(c) ? c : '-')]).Trim('-');
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neo", "Stage", "crew",
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{(slug.Length > 0 ? slug : "auftrag")}");
        Directory.CreateDirectory(folder);
        var job = new Job(name, task, folder);
        lock (_jobs)
            _jobs.Add(job);
        _ = System.Threading.Tasks.Task.Run(() => WorkAsync(job));
        return Language.Pick(
            $"Der Bautrupp arbeitet an „{name}“, im Ordner {folder}. Er meldet sich, wenn er fertig ist; bis dahin kann der Mensch weiter mit dir reden.",
            $"The crew is working on “{name}”, in the folder {folder}. It speaks up when it is done; meanwhile the person can go on talking to you.");
    }

    /// <summary>Stops a job that is still at work; the answer is for the brain.</summary>
    public string Cancel(string name)
    {
        Job? job;
        lock (_jobs)
            job = _jobs.LastOrDefault(candidate => candidate.Finished == null && candidate.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (job == null)
            return Language.Pick($"Der Bautrupp arbeitet an keinem „{name}“.", $"The crew is not working on any “{name}”.");
        job.Stop.Cancel();
        return Language.Pick($"„{job.Name}“ wird abgebrochen.", $"“{job.Name}” is being stopped.");
    }

    /// <summary>The jobs as the brain reads them with every question: those at work and the last ones done. Empty when none.</summary>
    public string Describe()
    {
        List<Job> jobs;
        lock (_jobs)
            jobs = [.. _jobs.Where(job => job.Finished == null || DateTime.Now - job.Finished < TimeSpan.FromHours(2)).TakeLast(4)];
        if (jobs.Count == 0)
            return "";
        return Language.Pick("Der Bautrupp:", "The crew:") + string.Concat(jobs.Select(job =>
        {
            var minutes = (int)((job.Finished ?? DateTime.Now) - job.Started).TotalMinutes;
            return job.Finished == null
                ? Language.Pick($"{Environment.NewLine}- „{job.Name}“ in Arbeit seit {minutes} min, {job.Steps} Schritte, gerade: {job.Doing}",
                    $"{Environment.NewLine}- “{job.Name}” at work for {minutes} min, {job.Steps} steps, now: {job.Doing}")
                : Language.Pick($"{Environment.NewLine}- „{job.Name}“ {(job.Succeeded ? "fertig" : "nicht zu Ende gekommen")} nach {minutes} min, Ordner {job.Folder}",
                    $"{Environment.NewLine}- “{job.Name}” {(job.Succeeded ? "done" : "not finished")} after {minutes} min, folder {job.Folder}");
        }));
    }

    private async Task WorkAsync(Job job)
    {
        var clock = Stopwatch.StartNew();
        App.Log($"crew: '{job.Name}' begins in {job.Folder}");
        await File.WriteAllTextAsync(Path.Combine(job.Folder, "auftrag.md"), $"# {job.Name}{Environment.NewLine}{Environment.NewLine}{job.Task}{Environment.NewLine}", Encoding.UTF8);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(job.Stop.Token);
        limit.CancelAfter(s_longest);
        try
        {
            var options = new ClaudeAgentOptions
            {
                Model = Environment.GetEnvironmentVariable(ModelVariable) is { Length: > 0 } model ? model : "claude-opus-5-5",
                WorkingDirectory = job.Folder,
                AppendSystemPrompt = Language.Pick(
                    $"Du bist der Bautrupp der N.E.O.-Bühne und arbeitest allein im Hintergrund; niemand beantwortet Fragen, entscheide selbst sinnvoll. " +
                    $"Arbeite nur in deinem Ordner {job.Folder}: schreibe Dateien mit Write und Edit nur dort. Löschen, Verschieben, Überschreiben " +
                    "per Befehl, Senden und Installieren (außer NuGet-Paketen in dein eigenes Projekt) sind dir verboten. Prüfe, was du baust: " +
                    "bauen, kurz ausführen, testen. Starte nichts, das auf einen Menschen wartet, außer kurz zum Prüfen. Schließe mit höchstens drei " +
                    "einfachen Sätzen, gesprochen, ohne Markdown: was fertig ist, wo es liegt und wie man es startet.",
                    $"You are the crew of the N.E.O. stage and work alone in the background; nobody answers questions, decide sensibly yourself. " +
                    $"Work only in your folder {job.Folder}: write files with Write and Edit only there. Deleting, moving, overwriting by command, " +
                    "sending and installing (except NuGet packages into your own project) are forbidden to you. Check what you build: build it, " +
                    "run it briefly, test it. Start nothing that waits for a person, except briefly to check it. End with at most three plain " +
                    "sentences, to be spoken, without markdown: what is done, where it is and how to start it."),
                AllowedTools = ["Read", "Glob", "Grep", "WebSearch", "WebFetch", "TodoWrite", "Bash(dotnet *)", "PowerShell(dotnet *)"],
                DisallowedTools = ["AskUserQuestion"],
                ExtraArguments = ["--permission-mode", "acceptEdits"],
                PartialMessages = false,
                MaxBudgetUsd = 5m,
                PermissionHandler = (request, _) => Task.FromResult(Decide(job, request)),
            };
            await using var session = await ClaudeAgentSession.StartAsync(options, limit.Token);
            session.EventReceived += happened =>
            {
                if (happened is ClaudeAgentToolStarted started)
                {
                    job.Steps++;
                    job.Doing = Doing(started);
                    App.Log($"crew: '{job.Name}' step {job.Steps}: {job.Doing}");
                }
            };
            var result = await session.SendAsync(job.Task, null, limit.Token);
            job.Result = result.Text.Trim();
            job.Succeeded = result.Succeeded && job.Result.Length > 0;
            job.Cost = result.CostUsd;
            App.Log($"crew: '{job.Name}' {(job.Succeeded ? "done" : "ended: " + result.Subtype)} after {clock.Elapsed.TotalSeconds:0} s, " +
                    $"{job.Steps} steps, {result.CostUsd * 100:0.0} ct, denied: {string.Join(", ", result.PermissionDenials)}");
        }
        catch (OperationCanceledException)
        {
            job.Result = job.Stop.IsCancellationRequested ? Language.Pick("Abgebrochen.", "Stopped.") : Language.Pick("Nach 30 Minuten abgebrochen.", "Stopped after 30 minutes.");
        }
        catch (Exception exception) when (exception is ClaudeAgentException or ClaudeCliNotFoundException or InvalidOperationException or IOException)
        {
            job.Result = Language.Pick("Claude Code ließ sich nicht starten: ", "Claude Code did not start: ") + exception.Message;
            App.Log($"crew: '{job.Name}' failed: {exception.Message}");
        }
        job.Finished = DateTime.Now;
        var minutes = clock.Elapsed.TotalMinutes;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(job.Folder, "ergebnis.md"),
                $"# {job.Name}{Environment.NewLine}{Environment.NewLine}{job.Result}{Environment.NewLine}{Environment.NewLine}" +
                $"{minutes:0.0} min, {job.Steps} Schritte, {job.Cost:0.00} $" + Environment.NewLine, Encoding.UTF8);
        }
        catch (IOException) { }
        var said = job.Succeeded
            ? Language.Pick($"Der Bautrupp ist fertig mit „{job.Name}“. ", $"The crew is done with “{job.Name}”. ") + Sentences(job.Result!, 2)
            : Language.Pick($"Der Bautrupp ist mit „{job.Name}“ nicht zu Ende gekommen. ", $"The crew did not finish “{job.Name}”. ") + Sentences(job.Result ?? "", 1);
        var whole = Language.Pick(
            $"Der Bautrupp meldet zu „{job.Name}“ ({(job.Succeeded ? "fertig" : "nicht fertig")}, {minutes:0.0} min, Ordner {job.Folder}): {Short(job.Result ?? "", 1500)}",
            $"The crew reports on “{job.Name}” ({(job.Succeeded ? "done" : "not done")}, {minutes:0.0} min, folder {job.Folder}): {Short(job.Result ?? "", 1500)}");
        Done?.Invoke(Language.Pick("Bautrupp", "Crew"), said, whole);
    }

    // Writing only in its own folder; commands that do not come back or that the stage never does are refused.
    private static ClaudeAgentPermission Decide(Job job, ClaudeAgentPermissionRequest request) => Decide(job.Name, job.Folder, request);

    /// <summary>Whether a job working in <paramref name="folder"/> may use a tool so; public for the check that tries it.</summary>
    internal static ClaudeAgentPermission Decide(string name, string folder, ClaudeAgentPermissionRequest request)
    {
        var input = request.Input;
        string Field(string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        ClaudeAgentPermission decision;
        switch (request.ToolName)
        {
            case "Read" or "Glob" or "Grep" or "LS" or "WebSearch" or "WebFetch" or "TodoWrite" or "NotebookRead":
                decision = ClaudeAgentPermission.Allow();
                break;
            case "Write" or "Edit" or "MultiEdit" or "NotebookEdit":
                var path = Field("file_path") is { Length: > 0 } file ? file : Field("notebook_path");
                decision = Inside(folder, path)
                    ? ClaudeAgentPermission.Allow()
                    : ClaudeAgentPermission.Deny($"Write only inside your own folder {folder}.");
                break;
            case "Bash" or "PowerShell":
                var command = Field("command");
                decision = Shell.Refusal(command) is { } refusal ? ClaudeAgentPermission.Deny(refusal)
                    : Shell.AtLeast(command, Changes.Nothing) != Changes.Nothing
                        ? ClaudeAgentPermission.Deny("Deleting, moving, overwriting by command, sending and installing are not allowed; write files with Write inside your folder.")
                        : ClaudeAgentPermission.Allow();
                break;
            default:
                decision = ClaudeAgentPermission.Deny("This tool is not open to the crew.");
                break;
        }
        App.Log($"crew: '{name}' asks for {request.ToolName}: {(decision.Allowed ? "yes" : "no, " + decision.Message)}");
        return decision;
    }

    private static bool Inside(string folder, string path)
    {
        if (path.Length == 0)
            return false;
        try
        {
            var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(folder, path));
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // What the crew does now, in a few words: the tool and what it works on.
    private static string Doing(ClaudeAgentToolStarted started)
    {
        string Field(string name) => started.Input.ValueKind == JsonValueKind.Object && started.Input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        var what = started.Name switch
        {
            "Write" or "Edit" or "MultiEdit" => Language.Pick("schreibt ", "writes ") + Path.GetFileName(Field("file_path")),
            "Read" => Language.Pick("liest ", "reads ") + Path.GetFileName(Field("file_path")),
            "Bash" or "PowerShell" => Language.Pick("führt aus: ", "runs: ") + Field("command"),
            "WebSearch" => Language.Pick("sucht im Netz: ", "searches the web: ") + Field("query"),
            "WebFetch" => Language.Pick("liest die Seite ", "reads the page ") + Field("url"),
            _ => started.Name,
        };
        return Short(what, 90);
    }

    private static string Sentences(string text, int most)
    {
        text = text.ReplaceLineEndings(" ").Replace("**", "").Replace("`", "").Trim();
        // A sentence ends at . ! or ? before a space, not at the dot of "Program.cs".
        var end = 0;
        for (var found = 0; found < most && end < text.Length; found++)
        {
            var next = end;
            while (next < text.Length && !(text[next] is '.' or '!' or '?' && (next + 1 == text.Length || char.IsWhiteSpace(text[next + 1]))))
                next++;
            end = Math.Min(text.Length, next + 1);
        }
        return Short(text[..end], 320);
    }

    private static string Short(string text, int most)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= most ? text : text[..(most - 1)] + "…";
    }
}
