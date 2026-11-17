using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Neo.App;
using Neo.ClaudeAgent;

namespace Neo.Stage;

/// <summary>
/// The stage's brain: one Claude Code session that stays open (Neo.ClaudeAgent), so a follow-up ("und jetzt den anderen")
/// knows what came before. With every question it gets the open windows, the apps N.E.O. built for it and the skills it
/// has learned; when the person also pointed, what was pointed at, with a picture of each. It answers in one or two
/// spoken sentences. Its hands: fast ones for windows (arrange, show, look) and for opening things, the web, N.E.O. for
/// small apps, and PowerShell for everything nobody foresaw; what lasts only after the person said yes.
/// </summary>
internal sealed class Brain : IAsyncDisposable
{
    public const string ModelVariable = "NEO_STAGE_MODEL";

    private const string SystemPromptGerman = """
        Du bist N.E.O. Stage, die Stimme eines Windows-Rechners. Ein Mensch hält eine Taste, spricht mit dir und lässt los; deine Antwort wird
        vorgelesen. Mit jeder Frage bekommst du die offenen Fenster, nummeriert, das oberste zuerst. „Im Vordergrund“ ist das, mit dem er gerade
        arbeitet; meint er ein Fenster, ohne es zu nennen („mach das groß“), ist das im Vordergrund gemeint. Die Spracherkennung verhört sich
        manchmal („Wort“ statt „Word“); deute das Gesagte mit Blick auf die offenen Fenster, und frag nur, wenn es wirklich unklar ist, dann
        mit einer kurzen Ja-oder-Nein-Frage.

        Sprich, wie man spricht: Deutsch, so kurz wie möglich, meist ein Satz, höchstens zwei. Keine Aufzählungen, kein Markdown, keine Links,
        keine Fensternummern, keine Tipps, um die niemand gebeten hat. Nenne Fenster so, wie ein Mensch sie nennt („Word“, „der Browser“).
        Wenn du etwas tust, das einen Moment dauert, sag es in einem kurzen Satz, bevor du das Werkzeug benutzt; danach brauchst du nichts mehr
        zu sagen, außer etwas ging schief.

        Deine Hände:
        - arrange ordnet Fenster (auch mehrere auf einmal), show hebt sie hervor, look zeigt dir ein Bild eines Fensters, wenn die Frage es
          braucht. open öffnet Programme, Ordner, Webseiten und Dateien, find_files sucht Dateien. Diese sind schnell; nimm sie zuerst.
        - WebSearch und WebFetch für Fragen an das Internet (Wetter, Nachrichten, Fakten).
        - build_app lässt N.E.O. in Sekunden ein kleines Programm bauen (einen Timer, einen Rechner für etwas, eine Liste, ein kleines Spiel);
          change_app ändert eine solche App, während sie läuft. Laufende N.E.O.-Apps bieten Funktionen und Werte an (unter „Apps im
          App-Netzwerk“): call_app ruft eine Funktion auf, read_app liest einen Wert. Das geht sofort und genau; nimm es vor change_app und look,
          wenn die App etwas schon kann.
        - run führt ein PowerShell-Skript aus (Windows PowerShell 5.1) für alles andere: Fenster schließen (mit CloseMainWindow, nie mit
          Stop-Process), Text in ein Programm schreiben, Lautstärke, Dateien, Systeminfos. Ein Skript muss in wenigen Sekunden fertig sein
          (nach 30 Sekunden wird es abgebrochen); durchsuche nie ganze Laufwerke, dafür gibt es find_files. Gib ehrlich an, was es verändert: nothing (liest nur),
          undoable (lässt sich rückgängig machen oder das Programm fragt selbst nach, etwa beim Schließen) oder lasting (löscht, überschreibt,
          sendet, installiert). Bei lasting fragt die Bühne den Menschen und führt es erst nach seinem Ja aus; formuliere dafür die Frage.
          Hat ein neues Skript geklappt und wird man es wohl wieder brauchen, gib ihm mit learn einen Namen; schreibe es dann mit param(...)
          für das, was sich ändert. Gelernte Fähigkeiten rufst du mit run und skill auf. Ändert ein Skript etwas in einem offenen Fenster
          (Text in Word, eine Zelle in Excel), gib dessen Nummer mit window an: die Bühne holt es dann nach vorne und zeigt die Änderung.

        Behaupte nie, etwas getan zu haben, das ein Werkzeug nicht bestätigt hat; meldet es eine Probe, sag, dass es nur eine Probe war.
        """;

    private const string SystemPromptEnglish = """
        You are N.E.O. Stage, the voice of a Windows computer. A person holds a key, speaks to you and lets go; your answer is read
        aloud. With every question you get the open windows, numbered, the top one first. "In front" is the one they are working in;
        when they mean a window without naming it ("make that bigger"), they mean the one in front. Speech recognition sometimes
        mishears; read what was said in the light of the open windows, and ask only when it is really unclear, then with a short
        yes-or-no question.

        Speak the way people speak: English, as short as possible, usually one sentence, two at most. No lists, no Markdown, no links,
        no window numbers, no tips nobody asked for. Call windows what a person calls them ("Word", "the browser"). When you do something
        that takes a moment, say so in one short sentence before you use the tool; after that you need not say anything, unless
        something went wrong.

        Your hands:
        - arrange arranges windows (several at once), show highlights them, look gives you a picture of a window when the question needs
          it. open opens programs, folders, websites and files, find_files finds files. These are fast; use them first.
        - WebSearch and WebFetch for questions to the internet (weather, news, facts).
        - build_app has N.E.O. build a small program in seconds (a timer, a calculator for something, a list, a little game, a
          simulation); change_app changes such an app while it runs. Running N.E.O. apps offer functions and values (under "Apps on the
          app network"): call_app calls a function, read_app reads a value. That is instant and exact; prefer it to change_app and look
          when the app can already do it. Write what N.E.O. should build or change in English, so the app speaks English.
        - run runs a PowerShell script (Windows PowerShell 5.1) for everything else: closing windows (with CloseMainWindow, never
          Stop-Process), writing text into a program, volume, files, system information. A script must finish in a few seconds (it is
          stopped after 30 seconds); never search whole drives, find_files is there for that. State honestly what it changes: nothing
          (only reads), undoable (can be undone, or the program asks itself, as when closing) or lasting (deletes, overwrites, sends,
          installs). For lasting, the stage asks the person and runs it only after their yes; write the question for that. If a new
          script worked and will likely be needed again, give it a name with learn, written with param(...) for what changes. Learned
          skills you run with run and skill. If a script changes something in an open window, give its number with window: the stage
          then brings it to the front and shows the change.

        Never claim to have done something a tool did not confirm; if it reports a rehearsal, say it was only a rehearsal.
        """;

    private readonly SemaphoreSlim _starting = new(1, 1);
    private readonly string _work;
    private readonly Func<string, string, bool>? _only;
    private readonly List<string> _said = [];
    private readonly NeoApps _apps = new();
    private ClaudeAgentSession? _session;
    private IReadOnlyList<AppWindow> _windows = [];
    private Task? _last;

    /// <param name="pretend">A check: the hands say what they would do and do nothing (reading scripts still run).</param>
    /// <param name="only">Which windows it may see (title, program); a check passes its own. Null: all of them.</param>
    public Brain(bool pretend = false, Func<string, string, bool>? only = null)
    {
        Apps.CallerName = "N.E.O. Stage"; // the person sees it when a call wants to change something
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

    /// <summary>Something that takes a while has begun ("N.E.O. baut …"), or ended (null). Raised on the session's thread.</summary>
    public event Action<string?>? Working;

    /// <summary>
    /// Asks the person a question aloud and returns what they answered, or null when nobody did. Without it, nothing
    /// that lasts is ever done.
    /// </summary>
    public Func<string, CancellationToken, Task<string?>>? AskPerson { get; set; }

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
        // An app N.E.O. just changed may still be putting itself back on the network: at most two seconds for that.
        for (var waited = 0; waited < 10 && _apps.Names.Any(built => Apps.Find(built) == null); waited++)
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        var session = await SessionAsync().ConfigureAwait(false);
        var pictures = utterance.Pointings.Where(pointing => pointing.Picture != null).Select(pointing => new ClaudeAgentImage(pointing.Picture!)).ToList();
        lock (_said)
            _said.Clear();
        var turn = session.SendAsync(Describe(utterance, _windows, _apps.Names), pictures, cancellationToken);
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
        SystemPrompt = Language.Pick(SystemPromptGerman, SystemPromptEnglish),
        WorkingDirectory = _work,
        Restricted = true,
        ToolSearch = false,
        BuiltInTools = ["WebSearch", "WebFetch"],
        AllowedTools = ["WebSearch", "WebFetch"],
        ToolServerName = "stage",
        Tools = [ArrangeTool(), ShowTool(), LookTool(), OpenTool(), FindFilesTool(), BuildAppTool(), ChangeAppTool(), CallAppTool(), ReadAppTool(), RunTool()],
        MaxBudgetUsd = 5m,
    };

    // ── The fast hands: windows, opening, files ──

    private ClaudeAgentTool ArrangeTool() => new(
        "arrange",
        "Ordnet Fenster, auch mehrere in einem Aufruf. Aktionen: minimize (minimieren), maximize (maximieren), restore (wiederherstellen), " +
        "front (nach vorne holen), left_half oder right_half (linke oder rechte Hälfte seines Bildschirms), tile (alle Fenster mit tile " +
        "werden zusammen ordentlich nebeneinander gelegt, in einem Raster, das zu ihrer Zahl passt).",
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
                      "action": { "type": "string", "enum": ["minimize", "maximize", "restore", "front", "left_half", "right_half", "tile"] }
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
            var tiled = new List<AppWindow>();
            foreach (var step in steps.EnumerateArray())
            {
                var action = Text(step, "action");
                if (action == "tile" && Window(step, "window") is { } tile)
                {
                    tiled.Add(tile);
                    Touched?.Invoke(tile);
                    continue;
                }
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
            if (tiled.Count > 0)
                results.Add(Hands.Tile([.. tiled.Select(window => window.Handle)], Pretend));
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
                        shown.Add(window.State == OpenWindows.Minimized ? $"„{window.Label}“ ist minimiert und nur in der Taskleiste zu sehen." : $"„{window.Label}“ ist hervorgehoben.");
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
            if (window.State == OpenWindows.Minimized)
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
        }, cancellation));

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
        (input, cancellation) => Task.Run(() =>
        {
            // Windows keeps what the stage starts behind the window in use; it comes to the front when it appears.
            var before = OpenWindows.List(most: 64);
            var said = Opener.Open(Text(input, "what"), Pretend, out var hint);
            if (hint != null && !Pretend)
                Hands.FrontWhenItAppears(before, hint, TimeSpan.FromSeconds(15), window => Touched?.Invoke(window));
            return new ClaudeAgentToolResult(said);
        }, cancellation));

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
        (input, cancellation) => Task.Run(() =>
        {
            var words = Text(input, "words");
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
        }, cancellation));

    // ── N.E.O.: small apps in seconds ──

    private ClaudeAgentTool BuildAppTool() => new(
        "build_app",
        "Lässt N.E.O. ein kleines Programm bauen, das danach in einem eigenen Fenster läuft. Dauert meist 10 bis 30 Sekunden. " +
        "Beschreibe auch, was man später an der laufenden App per Sprache einstellen oder abfragen möchte: N.E.O. gibt ihr dafür Funktionen.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "description": "Kurzer Name für das Fenster, etwa „Timer“ oder „Vokabeltrainer“" },
                "description": { "type": "string", "description": "Was das Programm tun und wie es aussehen soll, so genau wie der Mensch es sagte, in seiner Sprache" }
              },
              "required": ["name", "description"]
            }
            """),
        async (input, cancellation) =>
        {
            var name = Text(input, "name");
            var description = Text(input, "description");
            if (name.Length == 0 || description.Length == 0)
                return new ClaudeAgentToolResult("name und description fehlen.", IsError: true);
            if (Pretend)
                return new ClaudeAgentToolResult($"Nur eine Probe, nichts wurde gebaut. N.E.O. hätte „{name}“ gebaut: {description}");
            Working?.Invoke(Language.Pick($"N.E.O. baut „{name}“", $"N.E.O. is building “{name}”"));
            // The app window appears a few seconds after N.E.O. starts, with N.E.O. at work in it: to the front with it.
            Hands.FrontWhenItAppears(OpenWindows.List(most: 64), name, TimeSpan.FromSeconds(60), window => Touched?.Invoke(window), place: true);
            try { return new ClaudeAgentToolResult(await _apps.BuildAsync(name, Voiceable(name, description), cancellation)); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return new ClaudeAgentToolResult("N.E.O. ließ sich nicht fragen: " + exception.Message, IsError: true);
            }
            finally { Working?.Invoke(null); }
        });

    /// <summary>
    /// What the stage asks of every app it has built: the person talks to it later, so it offers its settings and its
    /// state on the app network, under the name its window has.
    /// </summary>
    internal static string Voiceable(string name, string description) => Language.English
        ? description + Environment.NewLine + Environment.NewLine +
          $"This app will be used by voice later. Give it [AppInfo(\"{name}\", \"…\")], public [McpCallable] methods for everything one would " +
          "want to set or trigger in it (with plain parameters such as a name and a number; in an animation or simulation, for instance the speed " +
          "of each thing that moves), and public [McpObservable] properties for " +
          "everything one would want to ask about (counters, states, results); every setting that can be set can also be read. They change " +
          "only the app itself and need no Changes = true. All text in the app is in English."
        : description + Environment.NewLine + Environment.NewLine +
        $"Diese App wird später per Sprache bedient. Gib ihr [AppInfo(\"{name}\", \"…\")], öffentliche [McpCallable]-Methoden für alles, was man " +
        "an ihr einstellen oder auslösen möchte (mit einfachen Parametern wie Name und Zahl; in einer Animation oder Simulation etwa das Tempo von allem, " +
        "was sich bewegt), und öffentliche [McpObservable]-Eigenschaften für alles, " +
        "was man abfragen möchte (Zähler, Zustände, Ergebnisse); jede Einstellung, die man setzen kann, soll man auch abfragen können. " +
        "Sie ändern nur die App selbst und brauchen kein Changes = true.";

    private ClaudeAgentTool ChangeAppTool() => new(
        "change_app",
        "Lässt N.E.O. eine App ändern, die es für dich gebaut hat, während sie läuft.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "app": { "type": "string", "description": "Name der App; leer für die zuletzt gebaute" },
                "change": { "type": "string", "description": "Was sich ändern soll, in der Sprache des Menschen" }
              },
              "required": ["change"]
            }
            """),
        async (input, cancellation) =>
        {
            var name = Text(input, "app");
            var change = Text(input, "change");
            if (Pretend)
                return new ClaudeAgentToolResult($"Nur eine Probe, nichts wurde geändert: {change}");
            Working?.Invoke(Language.Pick($"N.E.O. ändert {(name.Length > 0 ? $"„{name}“" : "die App")}", $"N.E.O. is changing {(name.Length > 0 ? $"“{name}”" : "the app")}"));
            try
            {
                var said = await _apps.ChangeAsync(name, change, cancellation);
                // The changed app comes to the front, so that the change can be seen.
                if (_apps.Window(name) is { } changed)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => Hands.Front(changed));
                    if (OpenWindows.List(most: 64).FirstOrDefault(window => window.Handle == changed) is { } shown)
                        Touched?.Invoke(shown);
                }
                return new ClaudeAgentToolResult(said);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                return new ClaudeAgentToolResult("N.E.O. ließ sich nicht fragen: " + exception.Message, IsError: true);
            }
            finally { Working?.Invoke(null); }
        });

    // ── The app network: talking to running apps through what they offer ──

    private ClaudeAgentTool CallAppTool() => new(
        "call_app",
        "Ruft eine Funktion einer laufenden N.E.O.-App auf, so wie sie unter „Apps im App-Netzwerk“ steht, und gibt ihr Ergebnis zurück.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "app": { "type": "string", "description": "Name der App, wie im App-Netzwerk" },
                "method": { "type": "string", "description": "Name der Funktion" },
                "arguments": { "type": "array", "description": "Die Werte für ihre Parameter, in ihrer Reihenfolge", "items": {} }
              },
              "required": ["app", "method"]
            }
            """),
        async (input, cancellation) =>
        {
            var app = Text(input, "app");
            var method = Text(input, "method");
            object?[] arguments = input.TryGetProperty("arguments", out var given) && given.ValueKind == JsonValueKind.Array
                ? [.. given.EnumerateArray().Select(value => (object?)value.Clone())]
                : [];
            if (Pretend)
                return new ClaudeAgentToolResult($"Nur eine Probe, nichts wurde aufgerufen: {app}.{method}({string.Join(", ", arguments)})");
            await AppOnNetworkAsync(app, cancellation);
            ShowApp(app);
            App.Log($"call: {app}.{method}({string.Join(", ", arguments)})");
            try
            {
                var result = await Apps.CallAsync<JsonElement>(app, method, arguments).WaitAsync(TimeSpan.FromSeconds(40), cancellation);
                var text = result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "Erledigt." : "Ergebnis: " + result.GetRawText();
                App.Log($"call: {text[..Math.Min(text.Length, 160)]}");
                return new ClaudeAgentToolResult(text);
            }
            catch (AppNotFoundException)
            {
                return new ClaudeAgentToolResult($"Eine App „{app}“ läuft nicht.", IsError: true);
            }
            catch (Exception exception) when (exception is AppCallException or TimeoutException or IOException)
            {
                return new ClaudeAgentToolResult($"{app}.{method} ging nicht: {exception.Message}{Offer(app)}", IsError: true);
            }
        });

    private ClaudeAgentTool ReadAppTool() => new(
        "read_app",
        "Liest einen Wert einer laufenden N.E.O.-App, so wie er unter „Apps im App-Netzwerk“ steht.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "app": { "type": "string", "description": "Name der App, wie im App-Netzwerk" },
                "value": { "type": "string", "description": "Name des Werts" }
              },
              "required": ["app", "value"]
            }
            """),
        async (input, cancellation) =>
        {
            var app = Text(input, "app");
            var value = Text(input, "value");
            await AppOnNetworkAsync(app, cancellation);
            ShowApp(app);
            try
            {
                var result = await Apps.ReadAsync<JsonElement>(app, value).WaitAsync(TimeSpan.FromSeconds(20), cancellation);
                App.Log($"read: {app}.{value} = {(result.ValueKind == JsonValueKind.Undefined ? "nothing" : result.GetRawText())}");
                return new ClaudeAgentToolResult(result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "Kein Wert." : result.GetRawText());
            }
            catch (AppNotFoundException)
            {
                return new ClaudeAgentToolResult($"Eine App „{app}“ läuft nicht.", IsError: true);
            }
            catch (Exception exception) when (exception is AppCallException or TimeoutException or IOException)
            {
                return new ClaudeAgentToolResult($"{app}.{value} ging nicht: {exception.Message}{Offer(app)}", IsError: true);
            }
        });

    // An app N.E.O. has just changed may be in a new process that has not put itself on the network yet: a few seconds' grace.
    private static async Task AppOnNetworkAsync(string app, CancellationToken cancellationToken)
    {
        for (var waited = 0; waited < 40 && Apps.Find(app) == null; waited++)
            await Task.Delay(200, cancellationToken);
    }

    // What an app really offers, for a call that named something it does not have.
    private static string Offer(string app)
        => Apps.Find(app) is { } found ? Environment.NewLine + Language.Pick("Die App bietet: ", "The app offers: ") + found.Describe() : "";

    // The window of an app Claude talks to lights up on the stage.
    private void ShowApp(string name)
    {
        if (Apps.Find(name) is not { } app)
            return;
        if (OpenWindows.List(most: 64).FirstOrDefault(window => window.ProcessId == (uint)app.ProcessId) is { } window)
            Touched?.Invoke(window);
    }

    /// <summary>The apps on the app network with what they offer, as the brain reads them with every question.</summary>
    private static string NetworkApps()
    {
        try
        {
            var running = Apps.Running(includeSelf: true);
            return running.Count == 0 ? "" : Language.Pick("Apps im App-Netzwerk (mit call_app und read_app nutzbar):", "Apps on the app network (usable with call_app and read_app):") + string.Concat(running.Select(app => Environment.NewLine + "- " + app.Describe().Replace("\n", "\n  ")));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return "";
        }
    }

    // ── PowerShell: everything nobody foresaw ──

    private ClaudeAgentTool RunTool() => new(
        "run",
        "Führt ein Windows-PowerShell-Skript aus, oder eine gelernte Fähigkeit (skill), und gibt seine Ausgabe zurück.",
        Schema("""
            {
              "type": "object",
              "properties": {
                "script": { "type": "string", "description": "Das Skript (Windows PowerShell 5.1); weglassen, wenn skill benutzt wird" },
                "skill": { "type": "string", "description": "Name einer gelernten Fähigkeit statt eines Skripts" },
                "with": { "type": "object", "description": "Werte für die param(...) der Fähigkeit, Name: Wert", "additionalProperties": { "type": "string" } },
                "says": { "type": "string", "description": "In wenigen Worten, was es tut, für den Menschen, etwa „Word schließen“" },
                "window": { "type": "integer", "description": "Nummer des offenen Fensters, in dem das Skript etwas ändert, falls eins" },
                "changes": { "type": "string", "enum": ["nothing", "undoable", "lasting"] },
                "question": { "type": "string", "description": "Nur bei lasting: die Frage an den Menschen, etwa „Soll ich die drei Dateien in Downloads löschen?“" },
                "learn": {
                  "type": "object",
                  "description": "Nur wenn das Skript als Fähigkeit behalten werden soll",
                  "properties": { "name": { "type": "string" }, "description": { "type": "string" } },
                  "required": ["name", "description"]
                }
              },
              "required": ["says", "changes"]
            }
            """),
        async (input, cancellation) =>
        {
            var says = Text(input, "says");
            var script = Text(input, "script");
            var changes = Parse(Text(input, "changes"));
            var parameters = new Dictionary<string, string>();
            if (Text(input, "skill") is { Length: > 0 } skillName)
            {
                if (Skills.Find(skillName) is not { } skill)
                    return new ClaudeAgentToolResult($"Eine Fähigkeit „{skillName}“ gibt es nicht.", IsError: true);
                script = skill.Script;
                changes = (Changes)Math.Max((int)changes, (int)skill.Changes);
                if (input.TryGetProperty("with", out var with) && with.ValueKind == JsonValueKind.Object)
                {
                    foreach (var value in with.EnumerateObject())
                        parameters[value.Name] = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() ?? "" : value.Value.GetRawText();
                }
            }
            if (script.Trim().Length == 0)
                return new ClaudeAgentToolResult("Es fehlt das Skript.", IsError: true);
            if (Shell.Refusal(script) is { } refusal)
            {
                App.Log($"run: refused '{says}'");
                return new ClaudeAgentToolResult(refusal, IsError: true);
            }
            changes = Shell.AtLeast(script, changes);
            App.Log($"run: '{says}' ({changes})");

            if (changes != Changes.Nothing && Pretend)
                return new ClaudeAgentToolResult($"Nur eine Probe, nichts wurde ausgeführt: {says}" + (changes == Changes.Lasting ? " (hätte vorher gefragt)." : "."));
            if (changes == Changes.Lasting)
            {
                var question = Text(input, "question") is { Length: > 0 } asked ? asked : $"Soll ich das tun: {says}?";
                var reply = AskPerson == null ? null : await AskPerson(question, cancellation);
                App.Log($"run: asked '{question}', heard '{reply}'");
                if (reply == null)
                    return new ClaudeAgentToolResult("Niemand hat geantwortet; nicht ausgeführt.");
                if (!Yes(reply))
                    return new ClaudeAgentToolResult($"Nicht ausgeführt. Der Mensch sagte: „{reply}“.");
            }

            App.Log("run script: " + (script.Length <= 1500 ? script : script[..1500] + " …").Replace("\r", "").Replace("\n", " ⏎ "));
            Working?.Invoke(says);
            string result;
            try { result = await Shell.RunAsync(script, parameters, cancellation); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                return new ClaudeAgentToolResult("PowerShell ließ sich nicht starten: " + exception.Message, IsError: true);
            }
            finally { Working?.Invoke(null); }
            App.Log($"run: {result[..Math.Min(result.Length, 160)]}");

            // A program changed from outside often shows the change only when something happens in its window (a mouse
            // move): the window comes to the front and is drawn anew, so the person sees what was done.
            if (changes != Changes.Nothing && Window(input, "window") is { } changed && OpenWindows.Seen(changed.Handle) != null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => Hands.Show(changed.Handle));
                Touched?.Invoke(changed);
            }

            if (input.TryGetProperty("learn", out var learn) && learn.ValueKind == JsonValueKind.Object && result.StartsWith("Fertig", StringComparison.Ordinal)
                && Text(learn, "name") is { Length: > 0 } learnName)
            {
                Skills.Keep(new Skill(learnName, Text(learn, "description"), changes, script));
                result += $" Als Fähigkeit „{learnName}“ behalten.";
            }
            return new ClaudeAgentToolResult(result);
        });

    private static Changes Parse(string changes) => changes switch
    {
        "nothing" => Changes.Nothing,
        "undoable" => Changes.Undoable,
        _ => Changes.Lasting,
    };

    /// <summary>Whether a spoken answer is a yes: "Ja", "Ja, mach", "Okay", "Klar", "Genau"… and no "nein" or "nicht" in it.</summary>
    internal static bool Yes(string reply)
    {
        var words = reply.ToLowerInvariant().Split([' ', ',', '.', '!', '?', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Any(word => word is "nein" or "nicht" or "nö" or "nee" or "stopp" or "warte" or "abbrechen"
                or "no" or "not" or "don't" or "dont" or "stop" or "wait" or "cancel" or "nope"))
            return false;
        return words[0] is "ja" or "jawohl" or "jap" or "jo" or "yes" or "okay" or "ok" or "klar" or "genau" or "gerne" or "gern" or "bitte" or "mach" or "los"
            or "tu" or "natürlich" or "sicher" or "yeah" or "yep" or "sure" or "go" or "do" or "please" or "of" or "absolutely" or "alright";
    }

    private AppWindow? Window(JsonElement input, string property)
        => Number(input, property) is var number && number >= 1 && number <= _windows.Count ? _windows[number - 1] : null;

    private static int Number(JsonElement input, string property)
        => input.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static string Text(JsonElement input, string property)
        => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement;

    // ── What the brain reads ──

    /// <summary>
    /// The utterance as the brain reads it: what was said, the open windows, the apps N.E.O. built and the skills it
    /// learned, and what was pointed at, if anything.
    /// </summary>
    internal static string Describe(Utterance utterance, IReadOnlyList<AppWindow> windows, IReadOnlyList<string>? apps = null)
    {
        var text = new StringBuilder();
        text.AppendLine(Language.Pick($"Gesagt: „{utterance.Text}“", $"Said: “{utterance.Text}”"));
        text.AppendLine(OpenWindows.Describe(windows));
        if (apps is { Count: > 0 })
            text.AppendLine(Language.Pick("Von N.E.O. gebaute Apps, die noch offen sind: ", "Apps N.E.O. built that are still open: ") + string.Join(", ", apps.Select(app => $"“{app}”")));
        if (NetworkApps() is { Length: > 0 } network)
            text.AppendLine(network);
        if (Skills.Describe() is { Length: > 0 } skills)
            text.AppendLine(skills);
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
