using System.Text.Json;
using Neo.App;

namespace Neo.Stage;

/// <summary>
/// What a watcher looks at and what the stage says when it happens. <see cref="Script"/> is a Windows PowerShell script
/// that only reads; instead of it, <see cref="App"/> and <see cref="Value"/> name a value of a running N.E.O. app.
/// </summary>
/// <param name="Say">The sentence the stage says; "{wert}" or "{value}" in it is replaced by what the watcher saw.</param>
/// <param name="OnChange">Fires when what it sees differs from the first time, instead of when it sees anything at all.</param>
/// <param name="Repeat">Goes on after it fired, instead of ending.</param>
internal sealed record WatcherSpec(string Name, string What, string Say, string? Script, string? App, string? Value,
    int EverySeconds, bool OnChange, bool Repeat)
{
    /// <summary>When it began, for "since"; set when it is added.</summary>
    public DateTimeOffset Since { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// "Sag mir Bescheid, wenn …": things the stage keeps an eye on for the person until they happen. A watcher asks every
/// few seconds: a script that only reads, or a value of a running N.E.O. app. Without <see cref="WatcherSpec.OnChange"/>
/// it fires when the script writes something meaningful (a file came, the battery is low); with it, when what it sees
/// differs from the first time (a web page, a number of mails). Then the stage says so in its own voice, named. A
/// watcher ends once it fired, unless it repeats; the real stage keeps them over a restart (Neo/Stage/watchers.json).
/// </summary>
internal sealed class Watchers
{
    private static readonly string[] s_nothing = ["", "false", "falsch", "$false", "0", "nein", "no", "none", "null", "nichts"];
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    private readonly List<Running> _running = [];
    private readonly string? _file;

    private sealed class Running(WatcherSpec spec)
    {
        public WatcherSpec Spec { get; } = spec;
        public CancellationTokenSource Stop { get; } = new();
        public string? Seen; // what a change watcher saw first, or what last fired
        public bool Looked;
        public int Failures;
    }

    /// <param name="keep">The real stage: watchers are written down and come back after a restart. Checks keep nothing.</param>
    public Watchers(bool keep)
    {
        _file = keep ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neo", "Stage", "watchers.json") : null;
    }

    /// <summary>A watcher fired, or gave up: who (its name) and what the stage says. Raised on a background thread.</summary>
    public event Action<string, string>? Fired;

    /// <summary>The watchers written down before the stage ended, started again.</summary>
    public void Resume()
    {
        if (_file == null || !File.Exists(_file))
            return;
        try
        {
            foreach (var spec in JsonSerializer.Deserialize<List<WatcherSpec>>(File.ReadAllText(_file)) ?? [])
                Begin(spec);
            App.Log($"watchers: {_running.Count} resumed");
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            App.Log("watchers: could not read them back, " + exception.Message);
        }
    }

    /// <summary>Starts a watcher (one of the same name is replaced). The answer is for the brain.</summary>
    public string Add(WatcherSpec spec)
    {
        if (spec.Script is { Length: > 0 } script)
        {
            if (Shell.Refusal(script) is { } refusal)
                return refusal;
            if (Shell.AtLeast(script, Changes.Nothing) != Changes.Nothing)
                return Language.Pick("Ein Wächter darf nur nachsehen, nichts verändern; dieses Skript würde etwas verändern.",
                    "A watcher may only look, never change anything; this script would change something.");
        }
        else if (spec.App is not { Length: > 0 } || spec.Value is not { Length: > 0 })
        {
            return Language.Pick("Es fehlt, was der Wächter ansehen soll: ein Skript, oder eine App und einer ihrer Werte.",
                "Missing what to watch: a script, or an app and one of its values.");
        }
        Stop(spec.Name, quietly: true);
        Begin(spec);
        Save();
        var every = Every(spec);
        return Language.Pick(
            $"Der Wächter „{spec.Name}“ läuft: alle {every.TotalSeconds:0} s sieht er nach, ob {spec.What}." + (spec.Repeat ? " Er meldet sich jedes Mal und läuft weiter." : " Er meldet sich einmal und endet dann."),
            $"The watcher “{spec.Name}” is on: every {every.TotalSeconds:0} s it looks whether {spec.What}." + (spec.Repeat ? " It speaks up every time and goes on." : " It speaks up once and ends."));
    }

    /// <summary>Ends the watcher of that name (or every watcher, for "alle"/"all"). The answer is for the brain.</summary>
    public string Stop(string name, bool quietly = false)
    {
        List<Running> ended;
        lock (_running)
        {
            var all = name.Trim().ToLowerInvariant() is "alle" or "all" or "*";
            ended = [.. _running.Where(watcher => all || watcher.Spec.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase))];
            foreach (var watcher in ended)
            {
                watcher.Stop.Cancel();
                _running.Remove(watcher);
            }
        }
        if (quietly)
            return "";
        Save();
        return ended.Count == 0
            ? Language.Pick($"Einen Wächter „{name}“ gibt es nicht.", $"There is no watcher “{name}”.")
            : Language.Pick($"Beendet: {string.Join(", ", ended.Select(watcher => watcher.Spec.Name))}.", $"Ended: {string.Join(", ", ended.Select(watcher => watcher.Spec.Name))}.");
    }

    /// <summary>The watchers as the brain reads them with every question; empty when there are none.</summary>
    public string Describe()
    {
        List<Running> running;
        lock (_running)
            running = [.. _running];
        if (running.Count == 0)
            return "";
        return Language.Pick("Wächter, die gerade laufen (stop_watching beendet sie):", "Watchers that are on (stop_watching ends them):")
            + string.Concat(running.Select(watcher =>
            {
                var minutes = (int)(DateTimeOffset.Now - watcher.Spec.Since).TotalMinutes;
                return Language.Pick(
                    $"{Environment.NewLine}- {watcher.Spec.Name}: ob {watcher.Spec.What}, alle {Every(watcher.Spec).TotalSeconds:0} s, seit {minutes} min" + (watcher.Spec.Repeat ? ", wiederholt" : ""),
                    $"{Environment.NewLine}- {watcher.Spec.Name}: whether {watcher.Spec.What}, every {Every(watcher.Spec).TotalSeconds:0} s, for {minutes} min" + (watcher.Spec.Repeat ? ", repeating" : ""));
            }));
    }

    public int Count
    {
        get
        {
            lock (_running)
                return _running.Count;
        }
    }

    private static TimeSpan Every(WatcherSpec spec) => TimeSpan.FromSeconds(Math.Clamp(spec.EverySeconds <= 0 ? 30 : spec.EverySeconds, 5, 3600));

    private void Begin(WatcherSpec spec)
    {
        var watcher = new Running(spec);
        lock (_running)
            _running.Add(watcher);
        _ = Task.Run(() => FollowAsync(watcher));
    }

    private async Task FollowAsync(Running watcher)
    {
        var spec = watcher.Spec;
        var token = watcher.Stop.Token;
        App.Log($"watcher '{spec.Name}': on, every {Every(spec).TotalSeconds:0} s");
        try
        {
            while (!token.IsCancellationRequested)
            {
                var (seen, problem) = await LookAsync(spec, token);
                if (problem != null)
                {
                    // Three failures in a row: it cannot do what it was set up for, and says so once.
                    if (++watcher.Failures >= 3)
                    {
                        App.Log($"watcher '{spec.Name}': gave up, {problem}");
                        End(watcher);
                        Fired?.Invoke(Who(spec), Language.Pick($"Der Wächter „{spec.Name}“ klappt nicht und hört auf: {problem}",
                            $"The watcher “{spec.Name}” does not work and stops: {problem}"));
                        return;
                    }
                }
                else
                {
                    watcher.Failures = 0;
                    if (Happened(watcher, seen!))
                    {
                        App.Log($"watcher '{spec.Name}': fired, saw '{Short(seen!, 120)}'");
                        if (!spec.Repeat)
                            End(watcher);
                        Fired?.Invoke(Who(spec), Sentence(spec, seen!));
                        if (!spec.Repeat)
                            return;
                    }
                }
                await Task.Delay(Every(spec), token);
            }
        }
        catch (OperationCanceledException) { }
        App.Log($"watcher '{spec.Name}': off");
    }

    // Whether what it sees now is the moment to speak up.
    private static bool Happened(Running watcher, string seen)
    {
        if (watcher.Spec.OnChange)
        {
            if (!watcher.Looked)
            {
                watcher.Looked = true;
                watcher.Seen = seen; // the first look is what it compares with
                return false;
            }
            if (seen.Length == 0 || seen == watcher.Seen)
                return false;
            watcher.Seen = seen;
            return true;
        }
        if (s_nothing.Contains(seen.Trim().ToLowerInvariant()))
        {
            watcher.Seen = null; // not (any more): the next time counts again
            return false;
        }
        if (seen == watcher.Seen)
            return false; // still the same: said already
        watcher.Seen = seen;
        return true;
    }

    private static async Task<(string? Seen, string? Problem)> LookAsync(WatcherSpec spec, CancellationToken token)
    {
        if (spec.Script is { Length: > 0 } script)
        {
            var outcome = await Shell.ExecuteAsync(script, null, TimeSpan.FromSeconds(Math.Min(25, Math.Max(5, Every(spec).TotalSeconds * 0.8))), token);
            if (outcome.Code is not { } code)
                return (null, Language.Pick("das Nachsehen dauert zu lange", "looking takes too long"));
            if (code != 0)
                return (null, Short(outcome.Errors.Trim().Length > 0 ? outcome.Errors : $"Code {code}", 160));
            return (outcome.Output.Trim(), null);
        }
        try
        {
            var json = await Apps.ReadJsonAsync(spec.App!, spec.Value!);
            using var parsed = JsonDocument.Parse(json);
            return (parsed.RootElement.ValueKind == JsonValueKind.String ? parsed.RootElement.GetString() ?? "" : parsed.RootElement.GetRawText(), null);
        }
        catch (AppNotFoundException)
        {
            return (null, Language.Pick($"die App „{spec.App}“ läuft nicht", $"the app “{spec.App}” is not running"));
        }
        catch (Exception exception) when (exception is AppCallException or JsonException or IOException or TimeoutException)
        {
            return (null, Short(exception.Message, 160));
        }
    }

    private static string Who(WatcherSpec spec) => Language.Pick($"Wächter: {spec.Name}", $"Watcher: {spec.Name}");

    private static string Sentence(WatcherSpec spec, string seen)
    {
        var value = Short(seen, 120);
        if (spec.Say.Trim().Length == 0)
            return $"{spec.What}: {value}";
        return spec.Say.Replace("{wert}", value, StringComparison.OrdinalIgnoreCase).Replace("{value}", value, StringComparison.OrdinalIgnoreCase);
    }

    private void End(Running watcher)
    {
        watcher.Stop.Cancel();
        lock (_running)
            _running.Remove(watcher);
        Save();
    }

    private void Save()
    {
        if (_file == null)
            return;
        List<WatcherSpec> specs;
        lock (_running)
            specs = [.. _running.Select(watcher => watcher.Spec)];
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(specs, s_json));
        }
        catch (IOException exception)
        {
            App.Log("watchers: could not write them down, " + exception.Message);
        }
    }

    private static string Short(string text, int most)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= most ? text : text[..(most - 1)] + "…";
    }
}
