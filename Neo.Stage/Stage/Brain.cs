using System.Globalization;
using System.Text;
using System.Text.Json;
using Neo.ClaudeAgent;

namespace Neo.Stage;

/// <summary>
/// The stage's brain: one Claude Code session that stays open (Neo.ClaudeAgent), so a follow-up ("und das da?") knows
/// what came before. It gets what was said with the time of each word, what was pointed at with a picture of each, and
/// which pointing word meant which thing; it answers in one or two spoken sentences and can arrange windows.
/// </summary>
internal sealed class Brain : IAsyncDisposable
{
    public const string ModelVariable = "NEO_STAGE_MODEL";

    private const string SystemPrompt = """
        Du bist das Gehirn von N.E.O. Stage. Ein Mensch hält eine Taste, spricht und zeigt dabei mit der Maus auf Dinge auf seinem Bildschirm.
        Du bekommst, was er gesagt hat, wann er welches Wort sagte, und worauf er gezeigt hat: nummeriert als Ding 1, Ding 2 und so weiter,
        jeweils mit einem Bildausschnitt. Zeigewörter wie „das hier“, „da“ oder „dahin“ sind schon dem Ding zugeordnet, auf das der Zeiger
        in dem Moment zeigte; wo keins zugeordnet ist, nimm das naheliegende.

        Antworte so, wie man spricht: auf Deutsch, in einem oder zwei kurzen Sätzen, ohne Aufzählungen, ohne Markdown, ohne Links.
        Deine Antwort wird vorgelesen.

        Fenster ordnen kannst du mit dem Werkzeug window: minimieren, maximieren, wiederherstellen, auf die linke oder rechte Bildschirmhälfte.
        Andere Dinge auf dem Rechner kannst du noch nicht tun. Sag das dann ehrlich und kurz. Behaupte nie, etwas getan zu haben, das du nicht
        getan hast. Ist unklar, was gemeint ist, frag kurz nach.
        """;

    private readonly SemaphoreSlim _starting = new(1, 1);
    private readonly string _work;
    private readonly List<string> _said = [];
    private ClaudeAgentSession? _session;
    private Utterance? _current;
    private Task? _last;

    /// <param name="pretend">A check: the hands say what they would do and do nothing.</param>
    public Brain(bool pretend = false)
    {
        Pretend = pretend;
        _work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neo", "Stage", "work");
        Directory.CreateDirectory(_work);
    }

    public bool Pretend { get; }

    /// <summary>Everything the session reports. Raised on its reading thread.</summary>
    public event Action<ClaudeAgentEvent>? Event;

    /// <summary>A window was arranged: which thing (0-based) and what was done.</summary>
    public event Action<int, string>? Arranged;

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
        _current = utterance;
        var session = await SessionAsync().ConfigureAwait(false);
        var pictures = utterance.Pointings.Where(pointing => pointing.Picture != null).Select(pointing => new ClaudeAgentImage(pointing.Picture!)).ToList();
        lock (_said)
            _said.Clear();
        var turn = session.SendAsync(Describe(utterance), pictures, cancellationToken);
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
        Tools = [WindowTool()],
        MaxBudgetUsd = 5m,
    };

    private ClaudeAgentTool WindowTool() => new(
        "window",
        "Ordnet das Fenster eines gezeigten Dings: minimize (minimieren), maximize (maximieren), restore (wiederherstellen), " +
        "left_half oder right_half (auf die linke oder rechte Hälfte seines Bildschirms).",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "thing": { "type": "integer", "description": "Nummer des Dings (1, 2, …), dessen Fenster gemeint ist" },
                "action": { "type": "string", "enum": ["minimize", "maximize", "restore", "left_half", "right_half"] }
              },
              "required": ["thing", "action"]
            }
            """).RootElement,
        (input, _) =>
        {
            var pointings = _current?.Pointings ?? [];
            var number = input.TryGetProperty("thing", out var thing) && thing.TryGetInt32(out var value) ? value : 0;
            var action = input.TryGetProperty("action", out var what) ? what.GetString() ?? "" : "";
            if (number < 1 || number > pointings.Count)
                return Task.FromResult(new ClaudeAgentToolResult($"Ding {number} gibt es nicht; gezeigt wurde auf {pointings.Count}.", IsError: true));
            if (!Hands.Actions.Contains(action))
                return Task.FromResult(new ClaudeAgentToolResult($"„{action}“ gibt es nicht.", IsError: true));
            var result = Hands.Arrange(pointings[number - 1].Thing, action, Pretend);
            Arranged?.Invoke(number - 1, action);
            return Task.FromResult(new ClaudeAgentToolResult(result));
        });

    /// <summary>The utterance as the brain reads it.</summary>
    internal static string Describe(Utterance utterance)
    {
        var text = new StringBuilder();
        text.AppendLine($"Gesagt: „{utterance.Text}“");

        var words = utterance.Heard is { Count: > 0 } heard
            ? Deixis.Read([.. heard.Select(word => word.Text)], [.. heard.Select(word => utterance.Started + word.Start)])
            : Deixis.Read(utterance.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries), [.. utterance.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(_ => utterance.Ended)]);
        if (utterance.Heard != null)
        {
            text.Append("Wörter (Sekunden ab Tastendruck): ");
            text.AppendLine(string.Join(" · ", words.Select(word =>
            {
                var at = (word.Time - utterance.Started).ToString("0.0", CultureInfo.GetCultureInfo("de-DE"));
                return word.Points && Deixis.Meant(word.Time, utterance.Pointings) is { } meant ? $"{word.Text} {at} → Ding {meant + 1}" : $"{word.Text} {at}";
            })));
        }

        if (utterance.Pointings.Count == 0)
        {
            text.AppendLine("Gezeigt wurde auf nichts Bestimmtes.");
            return text.ToString();
        }
        text.AppendLine("Gezeigt:");
        var pictures = 0;
        for (var i = 0; i < utterance.Pointings.Count; i++)
        {
            var pointing = utterance.Pointings[i];
            var thing = pointing.Thing;
            var window = Hands.TopWindow(thing);
            var title = window != IntPtr.Zero ? Hands.Title(window) : "";
            text.Append($"Ding {i + 1}: {(thing.Kind.Length > 0 ? thing.Kind : "Element")}");
            if (thing.Name.Length > 0)
                text.Append($" „{thing.Name}“");
            if (thing.Value is { } value)
                text.Append($", Inhalt „{value}“");
            text.Append($" in {(thing.Program.Length > 0 ? thing.Program : "einem Programm")}");
            if (title.Length > 0 && title != thing.Name)
                text.Append($", Fenster „{title}“");
            text.Append($"; Lage x={thing.Bounds.X}, y={thing.Bounds.Y}, {thing.Bounds.Width}×{thing.Bounds.Height} Pixel");
            text.AppendLine($"; gezeigt ab {(pointing.Time - utterance.Started).ToString("0.0", CultureInfo.GetCultureInfo("de-DE"))} s{(pointing.Picture != null ? $"; Bild {++pictures}" : "")}.");
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
