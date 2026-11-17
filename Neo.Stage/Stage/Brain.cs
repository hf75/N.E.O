using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Neo.ClaudeAgent;

namespace Neo.Stage;

/// <summary>
/// The stage's brain: one Claude Code session that stays open (Neo.ClaudeAgent), so a follow-up ("und jetzt den anderen")
/// knows what came before. With every question it gets the open windows, numbered, the top one first; when the person
/// also pointed, what was pointed at, with a picture of each. It answers in one or two spoken sentences and has hands:
/// it arranges windows, shows them, looks at one, opens programs, folders, websites and files, and finds files.
/// </summary>
internal sealed class Brain : IAsyncDisposable
{
    public const string ModelVariable = "NEO_STAGE_MODEL";

    private const string SystemPrompt = """
        Du bist N.E.O. Stage, die Stimme eines Windows-Rechners. Ein Mensch hält eine Taste, spricht mit dir und lässt los; deine Antwort wird
        vorgelesen. Mit jeder Frage bekommst du die offenen Fenster, nummeriert, das oberste zuerst. „Im Vordergrund“ ist das, mit dem er gerade
        arbeitet; meint er ein Fenster, ohne es zu nennen („mach das groß“), ist das im Vordergrund gemeint. Zeigt er dabei mit der Maus auf etwas,
        steht das unter „Gezeigt“, mit einem Bild.

        Sprich, wie man spricht: Deutsch, ein oder zwei kurze Sätze, keine Aufzählungen, kein Markdown, keine Links, keine Fensternummern.
        Nenne Fenster so, wie ein Mensch sie nennt („Word“, „der Browser“, „die Einkaufsliste“). Wenn du etwas tust, sag es in einem kurzen Satz,
        bevor du das Werkzeug benutzt; danach brauchst du nichts mehr zu sagen, außer etwas ging schief.

        Deine Werkzeuge: arrange ordnet Fenster, auch mehrere auf einmal. show hebt Fenster auf dem Bildschirm hervor, wenn du über sie sprichst.
        look gibt dir ein Bild eines Fensters; benutze es nur, wenn die Frage es braucht („Was steht da?“). open öffnet Programme, Ordner, Webseiten
        und Dateien. find_files sucht Dateien im Benutzerordner nach Namen und Inhalt.

        Mehr kannst du noch nicht, zum Beispiel keine Fenster schließen, nichts tippen, keine Mails lesen. Sag das ehrlich und kurz. Behaupte nie,
        etwas getan zu haben, das ein Werkzeug nicht bestätigt hat; meldet es eine Probe, sag, dass es nur eine Probe war. Ist unklar, was gemeint
        ist, frag kurz nach.
        """;

    private readonly SemaphoreSlim _starting = new(1, 1);
    private readonly string _work;
    private readonly Func<string, string, bool>? _only;
    private readonly List<string> _said = [];
    private ClaudeAgentSession? _session;
    private IReadOnlyList<AppWindow> _windows = [];
    private Task? _last;

    /// <param name="pretend">A check: the hands say what they would do and do nothing.</param>
    /// <param name="only">Which windows it may see (title, program); a check passes its own. Null: all of them.</param>
    public Brain(bool pretend = false, Func<string, string, bool>? only = null)
    {
        Pretend = pretend;
        _only = only;
        _work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neo", "Stage", "work");
        Directory.CreateDirectory(_work);
    }

    public bool Pretend { get; }

    /// <summary>Everything the session reports. Raised on its reading thread.</summary>
    public event Action<ClaudeAgentEvent>? Event;

    /// <summary>A window was arranged, shown or looked at: the stage outlines it. Raised on the session's thread.</summary>
    public event Action<AppWindow>? Touched;

    /// <summary>Starts the session ahead of the first question, so that it does not wait for Claude Code to start.</summary>
    public async Task WarmAsync()
    {
        try { await SessionAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is ClaudeAgentException or ClaudeCliNotFoundException)
        {
            App.Log("brain: " + exception.Message);
        }
    }

    /// <summary>
    /// Asks about an utterance and returns when the answer is complete. A question still being answered is interrupted
    /// first: the person has moved on.
    /// </summary>
    public async Task<BrainAnswer> AskAsync(Utterance utterance, CancellationToken cancellationToken = default)
    {
        if (_last is { IsCompleted: false } last)
        {
            await InterruptAsync().ConfigureAwait(false);
            await Task.WhenAny(last, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken)).ConfigureAwait(false);
        }
        _windows = OpenWindows.List(_only);
        var session = await SessionAsync().ConfigureAwait(false);
        var pictures = utterance.Pointings.Where(pointing => pointing.Picture != null).Select(pointing => new ClaudeAgentImage(pointing.Picture!)).ToList();
        lock (_said)
            _said.Clear();
        var turn = session.SendAsync(Describe(utterance, _windows), pictures, cancellationToken);
        _last = turn;
        var result = await turn.ConfigureAwait(false);
        string said;
        lock (_said)
            said = string.Join(" ", _said.Select(text => text.Trim()).Where(text => text.Length > 0));
        return new BrainAnswer(said.Length > 0 ? said : result.Text.Trim(), result);
    }

    public async Task InterruptAsync()
    {
        if (_session is { HasExited: false, IsBusy: true } session)
        {
            try { await session.InterruptAsync().ConfigureAwait(false); }
            catch (ClaudeAgentException) { }
        }
    }

    private async Task<ClaudeAgentSession> SessionAsync()
    {
        await _starting.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is { HasExited: false } running)
                return running;
            if (_session != null)
                await _session.DisposeAsync().ConfigureAwait(false);
            var started = DateTime.Now;
            _session = await ClaudeAgentSession.StartAsync(Options()).ConfigureAwait(false);
            _session.EventReceived += agentEvent =>
            {
                // Everything it writes in a turn is spoken, not only its last words (Claude Code's result).
                if (agentEvent is ClaudeAgentText text)
                {
                    lock (_said)
                        _said.Add(text.Text);
                }
                Event?.Invoke(agentEvent);
            };
            App.Log($"brain: Claude Code {_session.Cli.Version} ready after {(DateTime.Now - started).TotalSeconds:0.0} s");
            return _session;
        }
        finally
        {
            _starting.Release();
        }
    }

    private ClaudeAgentOptions Options() => new()
    {
        Model = Environment.GetEnvironmentVariable(ModelVariable) ?? Environment.GetEnvironmentVariable(ModelVariable, EnvironmentVariableTarget.User) ?? "claude-haiku-5-5",
        Effort = "low",
        SystemPrompt = SystemPrompt,
        WorkingDirectory = _work,
        Restricted = true,
        ToolSearch = false,
        BuiltInTools = [],
        ToolServerName = "stage",
        Tools = [ArrangeTool(), ShowTool(), LookTool(), OpenTool(), FindFilesTool()],
        MaxBudgetUsd = 5m,
    };

    // ── The hands ──

    private ClaudeAgentTool ArrangeTool() => new(
        "arrange",
        "Ordnet Fenster, auch mehrere in einem Aufruf. Aktionen: minimize (minimieren), maximize (maximieren), restore (wiederherstellen), " +
        "front (nach vorne holen), left_half oder right_half (linke oder rechte Hälfte seines Bildschirms).",
        Schema("""
            {
              "type": "object",
              "properties": {
                "steps": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "window": { "type": "integer", "description": "Nummer des Fensters aus der Liste der offenen Fenster" },
                      "action": { "type": "string", "enum": ["minimize", "maximize", "restore", "front", "left_half", "right_half"] }
                    },
                    "required": ["window", "action"]
                  }
                }
              },
              "required": ["steps"]
            }
            """),
        async (input, _) =>
        {
            if (!input.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
                return new ClaudeAgentToolResult("steps fehlt.", IsError: true);
            var results = new List<string>();
            foreach (var step in steps.EnumerateArray())
            {
                var action = step.TryGetProperty("action", out var what) ? what.GetString() ?? "" : "";
                if (Window(step, "window") is not { } window)
                {
                    results.Add($"Fenster {Number(step, "window")} gibt es nicht.");
                    continue;
                }
                if (!Hands.Actions.Contains(action))
                {
                    results.Add($"„{action}“ gibt es nicht.");
                    continue;
                }
                Touched?.Invoke(window);
                // Bringing a window to the front needs a thread with a message queue: the stage's own.
                results.Add(action == "front"
                    ? await Dispatcher.UIThread.InvokeAsync(() => Hands.Arrange(window.Handle, action, Pretend))
                    : Hands.Arrange(window.Handle, action, Pretend));
            }
            return new ClaudeAgentToolResult(string.Join(" ", results));
        });

    private ClaudeAgentTool ShowTool() => new(
        "show",
        "Hebt Fenster auf dem Bildschirm hervor, solange du über sie sprichst, etwa wenn gefragt wird, wo etwas ist.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "windows": { "type": "array", "items": { "type": "integer" }, "description": "Nummern der Fenster" }
              },
              "required": ["windows"]
            }
            """),
        (input, _) =>
        {
            var shown = new List<string>();
            if (input.TryGetProperty("windows", out var numbers) && numbers.ValueKind == JsonValueKind.Array)
            {
                foreach (var number in numbers.EnumerateArray())
                {
                    if (number.TryGetInt32(out var value) && value >= 1 && value <= _windows.Count)
                    {
                        var window = _windows[value - 1];
                        Touched?.Invoke(window);
                        shown.Add(window.State == "minimiert" ? $"„{window.Label}“ ist minimiert und nur in der Taskleiste zu sehen." : $"„{window.Label}“ ist hervorgehoben.");
                    }
                }
            }
            return Task.FromResult(shown.Count > 0 ? new ClaudeAgentToolResult(string.Join(" ", shown)) : new ClaudeAgentToolResult("Keines dieser Fenster gibt es.", IsError: true));
        });

    private ClaudeAgentTool LookTool() => new(
        "look",
        "Gibt dir ein Bild eines Fensters, so wie es gerade aussieht. Ohne Nummer: das Fenster im Vordergrund.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "window": { "type": "integer", "description": "Nummer des Fensters" }
              }
            }
            """),
        (input, cancellation) => Task.Run(() =>
        {
            var window = input.TryGetProperty("window", out JsonElement _) ? Window(input, "window") : _windows.FirstOrDefault(candidate => candidate.Front) ?? _windows.FirstOrDefault();
            if (window == null)
                return new ClaudeAgentToolResult("Dieses Fenster gibt es nicht.", IsError: true);
            if (window.State == "minimiert")
                return new ClaudeAgentToolResult($"„{window.Label}“ ist minimiert; davon gibt es kein Bild.", IsError: true);
            Touched?.Invoke(window);
            byte[]? picture;
            try { picture = Hands.Picture(window.Handle); }
            catch (Exception exception) when (exception is InvalidOperationException or ExternalException)
            {
                return new ClaudeAgentToolResult("Das Bild ließ sich nicht machen: " + exception.Message, IsError: true);
            }
            return picture == null
                ? new ClaudeAgentToolResult($"Von „{window.Label}“ ließ sich kein Bild machen.", IsError: true)
                : new ClaudeAgentToolResult($"So sieht „{window.Label}“ gerade aus.", Image: new ClaudeAgentImage(picture));
        }));

    private ClaudeAgentTool OpenTool() => new(
        "open",
        "Öffnet ein Programm (so wie es im Startmenü heißt, etwa „Word“ oder „Rechner“), einen Ordner (Downloads, Dokumente, Bilder, Desktop, " +
        "Musik, Videos oder ein Pfad), eine Webseite (Adresse wie spiegel.de) oder eine Datei (vollständiger Pfad, etwa von find_files).",
        Schema("""
            {
              "type": "object",
              "properties": {
                "what": { "type": "string", "description": "Programmname, Ordnername, Pfad oder Webadresse" }
              },
              "required": ["what"]
            }
            """),
        (input, _) => Task.Run(() =>
        {
            var what = input.TryGetProperty("what", out var value) ? value.GetString() ?? "" : "";
            return new ClaudeAgentToolResult(Opener.Open(what, Pretend));
        }));

    private ClaudeAgentTool FindFilesTool() => new(
        "find_files",
        "Sucht Dateien im Benutzerordner, deren Name oder Inhalt alle Wörter enthält, die passendsten zuerst. Gibt Pfade zurück, die open öffnen kann.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "words": { "type": "string", "description": "Ein bis drei Suchwörter, etwa „Netzwerkplan“ oder „Rechnung Strom“" }
              },
              "required": ["words"]
            }
            """),
        (input, _) => Task.Run(() =>
        {
            var words = input.TryGetProperty("words", out var value) ? value.GetString() ?? "" : "";
            try
            {
                var found = FileSearch.Find(words);
                if (found.Count == 0)
                    return new ClaudeAgentToolResult($"Nichts gefunden zu „{words}“.");
                var german = CultureInfo.GetCultureInfo("de-DE");
                return new ClaudeAgentToolResult(string.Join(Environment.NewLine,
                    found.Select(file => $"{file.Path} (geändert {file.Modified.ToString("d. MMMM yyyy", german)})")));
            }
            catch (Exception exception) when (exception is System.Data.Common.DbException or InvalidOperationException)
            {
                return new ClaudeAgentToolResult("Die Windows-Suche antwortet nicht: " + exception.Message, IsError: true);
            }
        }));

    private AppWindow? Window(JsonElement input, string property)
        => Number(input, property) is var number && number >= 1 && number <= _windows.Count ? _windows[number - 1] : null;

    private static int Number(JsonElement input, string property)
        => input.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement;

    // ── What the brain reads ──

    /// <summary>The utterance as the brain reads it: what was said, the open windows, and what was pointed at, if anything.</summary>
    internal static string Describe(Utterance utterance, IReadOnlyList<AppWindow> windows)
    {
        var text = new StringBuilder();
        text.AppendLine($"Gesagt: „{utterance.Text}“");
        text.AppendLine(OpenWindows.Describe(windows));
        if (utterance.Pointings.Count == 0)
            return text.ToString();

        var german = CultureInfo.GetCultureInfo("de-DE");
        if (utterance.Heard is { Count: > 0 } heard)
        {
            var words = Deixis.Read([.. heard.Select(word => word.Text)], [.. heard.Select(word => utterance.Started + word.Start)]);
            text.Append("Wörter (Sekunden ab Tastendruck): ");
            text.AppendLine(string.Join(" · ", words.Select(word =>
            {
                var at = (word.Time - utterance.Started).ToString("0.0", german);
                return word.Points && Deixis.Meant(word.Time, utterance.Pointings) is { } meant ? $"{word.Text} {at} → Ding {meant + 1}" : $"{word.Text} {at}";
            })));
        }
        text.AppendLine("Gezeigt:");
        var pictures = 0;
        for (var i = 0; i < utterance.Pointings.Count; i++)
        {
            var pointing = utterance.Pointings[i];
            var thing = pointing.Thing;
            text.Append($"Ding {i + 1}: {(thing.Kind.Length > 0 ? thing.Kind : "Element")}");
            if (thing.Name.Length > 0)
                text.Append($" „{thing.Name}“");
            if (thing.Value is { } value)
                text.Append($", Inhalt „{value}“");
            text.Append($" in {(thing.Program.Length > 0 ? thing.Program : "einem Programm")}");
            var top = Hands.TopWindow(thing);
            if (windows.FirstOrDefault(window => window.Handle == top) is { } window)
                text.Append($", in Fenster {window.Number}");
            text.Append($"; gezeigt ab {(pointing.Time - utterance.Started).ToString("0.0", german)} s");
            text.AppendLine(pointing.Picture != null ? $"; Bild {++pictures}." : ".");
        }
        return text.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_session != null)
            await _session.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>What the brain said in a turn, all of it as it is spoken, and how the turn went.</summary>
internal sealed record BrainAnswer(string Text, ClaudeAgentTurnResult Turn);
