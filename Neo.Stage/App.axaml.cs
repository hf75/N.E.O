using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Neo.ClaudeAgent;

namespace Neo.Stage;

/// <summary>
/// Neo.Stage has no window of its own: a stage over the screen and an icon in the notification area.
/// <list type="bullet">
///   <item>no arguments: the icon, and the dry run once after start (again from the icon);</item>
///   <item>--once: the dry run once, then the program ends;</item>
///   <item>--frames &lt;folder&gt;: moments of the dry run and of an answer as pictures, nothing on the screen;</item>
///   <item>--brain-check "sentence" [--at x,y;x,y] [--then "sentence"] [--windows word] [--for-real] [--yes]: the brain alone, its hands only
///   pretending unless --for-real (scripts that only read still run); --windows lets it see only windows whose title or program holds
///   the word; --yes answers its questions with yes;</item>
///   <item>--live-check seconds [--sound pcm] [--reply pcm] [--brain [--for-real]] [--windows word] [--pointing [--at x,y]]: the live
///   stage as if the key were held (and held again with --reply when the brain asks), never the microphone or a loudspeaker;</item>
///   <item>--mouth-check "text": the voice as an answer streams in, without a loudspeaker: when the first sound would come.</item>
/// </list>
/// </summary>
public partial class App : Application
{
    private StageOverlay? _stage;
    private LiveStage? _live;
    private Brain? _brain;
    private bool _playing;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var arguments = desktop.Args ?? [];
            if (Argument(arguments, "--frames") is { } folder)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    KeyFrames.Render(folder);
                    desktop.Shutdown();
                });
            }
            else if (Argument(arguments, "--probe") is { } where)
            {
                // What UI Automation finds at a screen point (x,y), into the log: the eyes without the stage.
                Dispatcher.UIThread.Post(async () =>
                {
                    var parts = where.Split(',');
                    var point = new PixelPoint(int.Parse(parts[0]), int.Parse(parts[1]));
                    var thing = await Task.Run(() => Uia.At(point));
                    Log(thing == null ? $"probe {point}: nothing" : $"probe {point}: {thing.Kind} '{thing.Name}' in {thing.Program}, {thing.Bounds}, value '{thing.Value}'");
                    desktop.Shutdown();
                });
            }
            else if (Argument(arguments, "--ears-check") is { } sound)
            {
                // The ears without a microphone: a recorded sentence (16 kHz, 16-bit, mono PCM) at speaking pace.
                Dispatcher.UIThread.Post(async () =>
                {
                    var clock = Stopwatch.StartNew();
                    if (Ears.Key() is not { } key)
                    {
                        Log($"ears: no {Ears.KeyVariable}");
                        desktop.Shutdown();
                        return;
                    }
                    var model = Argument(arguments, "--model") ?? "scribe_v3_live";
                    var language = Argument(arguments, "--language") ?? "de";
                    await using var ears = Ears.Hear(key, await File.ReadAllBytesAsync(sound), language, model);
                    ears.Partial += text => Log($"partial at {clock.Elapsed.TotalSeconds:0.00} s: {text}");
                    var heard = await ears.FinishAsync(TimeSpan.FromSeconds(15));
                    Log($"final at {clock.Elapsed.TotalSeconds:0.00} s ({model}, {language}): '{heard?.Text}' final={heard?.Final} problem={ears.Problem}");
                    foreach (var word in heard?.Words ?? [])
                        Log($"  {word.Start,5:0.00}-{word.End,5:0.00} {word.Text}");
                    desktop.Shutdown();
                });
            }
            else if (Argument(arguments, "--live-check") is { } seconds)
            {
                // The live stage as if the right Ctrl key were held for a few seconds, at wherever the pointer is.
                Dispatcher.UIThread.Post(() =>
                {
                    var hold = double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture);
                    var clock = Stopwatch.StartNew();
                    _stage ??= new StageOverlay(PrimaryScreen());
                    // Recorded sentences instead of the microphone (--sound, and --reply for the answer to a question the
                    // brain asks), or no ears at all: a check never opens the microphone.
                    var sounds = new Queue<byte[]>();
                    if (Argument(arguments, "--sound") is { } file)
                        sounds.Enqueue(File.ReadAllBytes(file));
                    var later = (Argument(arguments, "--more") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(entry => entry.Split('@'))
                        .Select(parts => (Sound: File.ReadAllBytes(parts[0]), At: double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)))
                        .OrderBy(entry => entry.At).ToList();
                    foreach (var entry in later)
                        sounds.Enqueue(entry.Sound);
                    var reply = Argument(arguments, "--reply") is { } replyFile ? File.ReadAllBytes(replyFile) : null;
                    if (reply != null)
                        sounds.Enqueue(reply);
                    // --brain: the brain answers on the stage, without a voice; its hands only pretend unless --for-real.
                    _brain = arguments.Contains("--brain") ? new Brain(pretend: !arguments.Contains("--for-real"), only: Only(Argument(arguments, "--windows"))) : null;
                    if (_brain != null)
                        _ = _brain.WarmAsync();
                    // --at x,y: the pointer rests there (the mouse is left alone).
                    var at = Argument(arguments, "--at") is { } point ? ScreenPoint(point) : (PixelPoint?)null;
                    // The key: held at the start, and again half a second after the brain asks, as long as the reply lasts.
                    double? replyFrom = null;
                    var replyFor = reply != null ? reply.Length / 32000.0 + 0.5 : 0;
                    bool KeyHeld()
                    {
                        var now = clock.Elapsed.TotalSeconds;
                        if (now < hold)
                            return true;
                        if (later.Any(entry => now >= entry.At && now < entry.At + entry.Sound.Length / 32000.0 + 0.5))
                            return true;
                        if (reply != null && replyFrom == null && _live?.Asking == true)
                            replyFrom = now + 0.5;
                        return replyFrom is { } from && now >= from && now < from + replyFor;
                    }
                    _live = new LiveStage(_stage, KeyHeld, key => sounds.Count > 0 ? Ears.Hear(key, sounds.Dequeue(), Language.Code) : null, _brain,
                        pointer: at is { } rest ? () => rest : null, pointing: arguments.Contains("--pointing"));
                    _live.Spoken += utterance =>
                    {
                        if (utterance.Unspoken)
                        {
                            Log("own turn over"); // the stage went on by itself; the check's end stays where the person's turn put it
                            return;
                        }
                        Log($"spoken:{utterance.Ended - utterance.Started:0.0} s, {utterance.Trail.Count} trail points, " +
                            $"pointed at {string.Join(" / ", utterance.Pointings.Select(p => $"{p.Thing.Kind} '{p.Thing.Label}' at {p.Time - utterance.Started:0.00} s"))}");
                        foreach (var word in utterance.Heard ?? [])
                            Log($"  {word.Start,5:0.00} {word.Text}");
                        // --linger seconds: what goes on after the answer (a guide, say) gets its time before the check ends.
                        var linger = double.Parse(Argument(arguments, "--linger") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                        DispatcherTimer.RunOnce(() => desktop.Shutdown(), TimeSpan.FromSeconds(Math.Max(0.01, linger)));
                    };
                    _live.Start();
                    StageOnNetwork.Publish(_live);
                    if (_brain != null && Argument(arguments, "--clicks") is { } clicks)
                    {
                        _brain.Recorder.HookMouse = false; // only these clicks: the person's own stay out of the check
                        foreach (var click in clicks.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var parts = click.Split('@');
                            var clickAt = ScreenPoint(parts[0]);
                            var after = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                            var brain = _brain;
                            DispatcherTimer.RunOnce(() =>
                            {
                                Log($"check: click at {clickAt}{(brain.Recorder.Recording ? "" : " (not watching)")}");
                                brain.Recorder.Feed(clickAt);
                            }, TimeSpan.FromSeconds(Math.Max(0.01, after - clock.Elapsed.TotalSeconds)));
                        }
                    }
                });
            }
            else if (Argument(arguments, "--brain-check") is { } sentence)
            {
                // The brain without microphone, voice or hands: a sentence, and what lies at the screen points after --at as
                // if it was pointed at while the sentence's pointing words were said. Windows stay as they are.
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await BrainCheckAsync(sentence, Argument(arguments, "--at"), Argument(arguments, "--then"), arguments.Contains("--for-real"),
                            Only(Argument(arguments, "--windows")), arguments.Contains("--yes"),
                            double.Parse(Argument(arguments, "--pause") ?? "0", System.Globalization.CultureInfo.InvariantCulture), Argument(arguments, "--snap"));
                    }
                    catch (Exception exception) { Log("brain check failed: " + exception); }
                    desktop.Shutdown();
                });
            }
            else if (Argument(arguments, "--open-check") is { } name)
            {
                // What "open" would do with a name, without opening anything; and the programs that came into question.
                Dispatcher.UIThread.Post(async () =>
                {
                    var clock = Stopwatch.StartNew();
                    var matches = await Task.Run(() => StartMenuApps.Matching(name));
                    Log($"open '{name}': {StartMenuApps.All().Count} programs read in {clock.Elapsed.TotalSeconds:0.00} s; best: " +
                        string.Join(" | ", matches.Take(5).Select(match => $"{match.Name} ({match.Score})")));
                    Log("  " + await Task.Run(() => Opener.Open(name, pretend: true, out _)));
                    desktop.Shutdown();
                });
            }
            else if (Argument(arguments, "--find-check") is { } words)
            {
                // The file search, with only how many were found and of which kinds in the log, not their names.
                Dispatcher.UIThread.Post(async () =>
                {
                    var clock = Stopwatch.StartNew();
                    try
                    {
                        var found = await Task.Run(() => FileSearch.Find(words));
                        Log($"find '{words}': {found.Count} files in {clock.Elapsed.TotalSeconds:0.00} s, kinds " +
                            string.Join(", ", found.Select(file => Path.GetExtension(file.Path)).GroupBy(kind => kind).Select(kind => $"{kind.Key} ×{kind.Count()}")) +
                            $", in AppData: {found.Count(file => file.Path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase))}");
                    }
                    catch (Exception exception) { Log("find failed: " + exception.Message); }
                    desktop.Shutdown();
                });
            }
            else if (Argument(arguments, "--demo") is { } demo)
            {
                // A demo nobody operates: lines spoken by a synthetic voice, the stage answering live, the screen recorded.
                Dispatcher.UIThread.Post(async () =>
                {
                    try { await Director.RunAsync(demo, PrimaryScreen()); }
                    catch (Exception exception) { Log("demo failed: " + exception); }
                    desktop.Shutdown();
                });
            }
            else if (arguments.Contains("--crew-check"))
            {
                // What the crew may do, asked of the judge directly: nothing runs, nothing is written.
                var crewFolder = Path.Combine(Path.GetTempPath(), "neo-crew-check");
                foreach (var (tool, field, value) in (ReadOnlySpan<(string, string, string)>)[
                    ("Write", "file_path", Path.Combine(crewFolder, "App", "Program.cs")),
                    ("Write", "file_path", @"C:\Users\Public\x.txt"),
                    ("Edit", "file_path", Path.Combine(crewFolder, "..", "outside.txt")),
                    ("Bash", "command", "dotnet build -nologo"),
                    ("PowerShell", "command", "Get-ChildItem; dotnet run"),
                    ("Bash", "command", "rm -rf bin"),
                    ("PowerShell", "command", @"Remove-Item -Recurse C:\Users\x"),
                    ("PowerShell", "command", "Set-Content notes.txt hello"),
                    ("PowerShell", "command", "Start-Process powershell -Verb RunAs"),
                    ("Read", "file_path", @"C:\Windows\win.ini"),
                    ("mcp__other__tool", "x", "y")])
                {
                    var input = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, string> { [field] = value });
                    var decision = Crew.Decide("check", crewFolder, new Neo.ClaudeAgent.ClaudeAgentPermissionRequest(tool, input, null, null, null, null, null));
                    Log($"crew-check: {tool} {value} → {(decision.Allowed ? "yes" : "no: " + decision.Message)}");
                }
                Dispatcher.UIThread.Post(() => desktop.Shutdown()); // once the loop runs
            }
            else if (Argument(arguments, "--uia-find") is { } titleWord)
            {
                // What the eyes find by name in a window whose title holds the word, and what can be clicked there.
                Dispatcher.UIThread.Post(async () =>
                {
                    var name = Argument(arguments, "--name") ?? "";
                    var window = OpenWindows.List(Only(titleWord)).FirstOrDefault();
                    if (window == null)
                        Log($"uia-find: no window with '{titleWord}'");
                    else
                    {
                        for (var round = 1; round <= 2; round++) // the first look of a process wakes UI Automation up
                        {
                            var clock = Stopwatch.StartNew();
                            var seen = await Task.Run(() => Uia.Look(window.Handle));
                            var found = Uia.Find(seen, name);
                            Log($"uia-find '{name}' in '{window.Title}': {(found == null ? "nothing" : $"{found.Kind} '{found.Name}' at {found.Bounds}")} " +
                                $"(look {round}: {seen?.Count ?? 0} elements in {clock.ElapsedMilliseconds} ms)");
                        }
                        Log("  clickable: " + string.Join(" | ", await Task.Run(() => Uia.Names(window.Handle, 30))));
                    }
                    desktop.Shutdown();
                });
            }
            else if (Argument(arguments, "--mouth-check") is { } text)
            {
                // The voice without a loudspeaker: the text streams in as an answer would, a few letters at a time.
                Dispatcher.UIThread.Post(async () =>
                {
                    if (Ears.Key() is not { } key)
                    {
                        Log($"mouth: no {Ears.KeyVariable}");
                        desktop.Shutdown();
                        return;
                    }
                    var mouth = new Mouth(key, player: _ => null);
                    var clock = Stopwatch.StartNew();
                    mouth.Begin();
                    for (var i = 0; i < text.Length; i += 6)
                    {
                        mouth.Add(text.Substring(i, Math.Min(6, text.Length - i)));
                        await Task.Delay(25);
                    }
                    Log($"mouth: all of it written after {clock.Elapsed.TotalSeconds:0.00} s");
                    await mouth.FinishAsync();
                    Log($"mouth: finished after {clock.Elapsed.TotalSeconds:0.00} s{(mouth.Problem != null ? ", " + mouth.Problem : "")}");
                    desktop.Shutdown();
                });
            }
            else if (arguments.Contains("--once"))
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    await Task.Delay(1200);
                    await PlayAsync();
                    desktop.Shutdown();
                });
            }
            else
            {
                // The stage at work: hold the right Ctrl key and speak. The dry run stays in the icon's menu.
                AddTrayIcon(desktop);
                Dispatcher.UIThread.Post(() =>
                {
                    // Two stages would both answer aloud at once: a newer one ends this one.
                    if (!OneStage.Take(() => Dispatcher.UIThread.Post(() =>
                        {
                            Log("a newer stage took over");
                            desktop.Shutdown();
                        })))
                    {
                        Log("the stage before did not end; this one does");
                        desktop.Shutdown();
                        return;
                    }
                    _stage ??= new StageOverlay(PrimaryScreen());
                    // The brain starts now, so the first question does not wait for Claude Code; the voice needs the ElevenLabs key.
                    _brain = new Brain(keep: true);
                    _ = _brain.WarmAsync();
                    var mouth = Ears.Key() is { } key ? new Mouth(key) : null;
                    // Pointing with the mouse while speaking is off unless NEO_STAGE_POINTING=1: the voice alone comes first.
                    _live = new LiveStage(_stage, brain: _brain, mouth: mouth, pointing: Environment.GetEnvironmentVariable("NEO_STAGE_POINTING") == "1");
                    _live.Start();
                    StageOnNetwork.Publish(_live); // apps speak and ask through it
                    _brain.Watchers.Resume(); // what it kept an eye on before a restart
                    Log($"live (process {Environment.ProcessId}): hold the right Ctrl key and speak{(mouth == null ? $" (no voice without {Ears.KeyVariable})" : "")}");
                });
            }
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            // Claude Code ends with the stage.
            lifetime.Exit += (_, _) =>
            {
                Neo.App.AppNetwork.Unpublish();
                if (_brain != null)
                    Task.Run(() => _brain.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Which windows a check may show the brain: those whose title or program holds the word, so the person's own stay out.
    private static Func<string, string, bool>? Only(string? word)
        => word == null ? null : (title, program) => title.Contains(word, StringComparison.OrdinalIgnoreCase) || program.Contains(word, StringComparison.OrdinalIgnoreCase);

    private static async Task BrainCheckAsync(string sentence, string? at, string? then, bool forReal, Func<string, string, bool>? only, bool yes,
        double pause = 0, string? snap = null)
    {
        var clock = Stopwatch.StartNew();
        await using var brain = new Brain(pretend: !forReal, only: only);
        // Nobody is there to answer a question: --yes answers it with yes, else it stays unanswered.
        brain.AskPerson = (question, _) =>
        {
            Log($"  asked: '{question}', answered {(yes ? "'Ja'" : "nothing")}");
            return Task.FromResult<string?>(yes ? "Ja" : null);
        };
        brain.Event += agentEvent =>
        {
            switch (agentEvent)
            {
                case ClaudeAgentToolStarted tool:
                    Log($"  tool {tool.Name} {tool.Input}");
                    break;
                case ClaudeAgentToolFinished done:
                    Log($"  tool result: {done.Output}");
                    break;
            }
        };
        var warming = brain.WarmAsync();
        var utterance = await PretendAsync(sentence, at);
        await warming;
        Log($"brain ready after {clock.Elapsed.TotalSeconds:0.0} s; it reads:{Environment.NewLine}{Brain.Describe(utterance, OpenWindows.List(only)).TrimEnd()}");
        await AskAsync(brain, utterance);
        Snap(0);
        // --then: one more sentence; or a file of sentences, one per line, with --pause seconds between them.
        var more = then == null ? [] : File.Exists(then) ? File.ReadAllLines(then).Where(line => line.Trim().Length > 0).ToArray() : [then];
        for (var i = 0; i < more.Length; i++)
        {
            if (pause > 0)
                await Task.Delay(TimeSpan.FromSeconds(pause));
            await AskAsync(brain, await PretendAsync(more[i], null));
            Snap(i + 1);
        }

        // --snap folder: a picture of every N.E.O. app window after each sentence.
        void Snap(int step)
        {
            if (snap == null)
                return;
            Directory.CreateDirectory(snap);
            foreach (var window in OpenWindows.List(most: 64).Where(window => window.Program.Contains("PluginWindow", StringComparison.OrdinalIgnoreCase)))
            {
                if (Hands.Picture(window.Handle) is { } png)
                    File.WriteAllBytes(Path.Combine(snap, $"step{step}-{window.ProcessId}.png"), png);
            }
        }
        // What the hands started in the background (a window brought to the front when it appears) gets its time.
        if (forReal)
            await Task.Delay(TimeSpan.FromSeconds(8));

        static async Task AskAsync(Brain brain, Utterance utterance)
        {
            var asked = Stopwatch.StartNew();
            var answer = await brain.AskAsync(utterance);
            var turn = answer.Turn;
            Log($"asked '{utterance.Text}', {(turn.Succeeded ? "answered" : "stopped: " + turn.Subtype)} after {asked.Elapsed.TotalSeconds:0.0} s " +
                $"({turn.Rounds} rounds, {turn.Usage.InputTokens + turn.Usage.CacheReadInputTokens + turn.Usage.CacheCreationInputTokens} tokens in, " +
                $"{turn.Usage.OutputTokens} out, {turn.CostUsd * 100:0.00} ct): '{answer.Text}'");
        }
    }

    // An utterance as the ears would have heard it, at a calm pace, with the things at the given points pointed at from
    // just before the pointing words; without pointing words they are spread over the sentence.
    private static async Task<Utterance> PretendAsync(string sentence, string? at)
    {
        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var heard = words.Select((word, i) => new HeardWord(word, 0.4 + 0.33 * i, 0.4 + 0.33 * i + 0.28)).ToList();
        var utterance = new Utterance { Started = 0, Ended = 0.4 + 0.33 * words.Length + 0.3, Text = sentence, Heard = heard };
        var pointingWords = Deixis.Read(words, [.. heard.Select(word => word.Start)]).Where(word => word.Points).ToList();
        var points = (at ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(ScreenPoint).ToList();
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var thing = await Task.Run(() => Uia.At(point));
            if (thing == null)
            {
                Log($"nothing at {point}");
                continue;
            }
            var picture = await Task.Run(() => Hands.Picture(thing)); // off the UI thread, as the live stage takes it
            var time = i < pointingWords.Count ? pointingWords[i].Time - 0.15 : utterance.Ended * (i + 1) / (points.Count + 1);
            utterance.Pointings.Add(new Pointing(time, new Point(point.X, point.Y), thing, picture));
            Log($"at {point}: {thing.Kind} '{thing.Name}' in {thing.Program}, {thing.Bounds}, picture {(picture == null ? "none" : $"{picture.Length / 1024} KB")}");
        }
        return utterance;
    }

    /// <summary>What the stage does, into the file NEO_STAGE_LOG names (nothing when it is not set).</summary>
    internal static void Log(string line)
    {
        if (Environment.GetEnvironmentVariable("NEO_STAGE_LOG") is not { Length: > 0 } path)
            return;
        try { File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); }
        catch (IOException) { }
    }

    private static PixelPoint ScreenPoint(string xy)
    {
        var parts = xy.Split(',');
        return new PixelPoint(int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static string? Argument(string[] arguments, string name)
    {
        var index = Array.IndexOf(arguments, name);
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }

    /// <summary>The dry run on the primary screen, over the windows that are open there.</summary>
    private async Task PlayAsync()
    {
        if (_playing || _live?.Busy == true)
            return;
        _playing = true;
        if (_live != null)
            _live.Enabled = false;
        try
        {
            var stage = _stage ??= new StageOverlay(PrimaryScreen());
            Log($"stage over {stage.ScreenBounds} at scaling {stage.Scaling}, {stage.StageSize}");
            var run = Prepare(stage);
            stage.Display(StageFrame.Empty);
            stage.Show();
            Log($"shown, handle {stage.Handle}");

            var started = Stopwatch.GetTimestamp();
            var frames = 0;
            var done = new TaskCompletionSource();
            void Tick(TimeSpan _)
            {
                var time = Stopwatch.GetElapsedTime(started).TotalSeconds;
                stage.Display(run.Frame(time));
                frames++;
                if (time < DryRun.Duration)
                    stage.RequestAnimationFrame(Tick);
                else
                    done.TrySetResult();
            }
            stage.RequestAnimationFrame(Tick);
            await done.Task;
            Log($"played: {frames} frames in {Stopwatch.GetElapsedTime(started).TotalSeconds:0.0} s");

            stage.Display(StageFrame.Empty);
            stage.Hide();
        }
        catch (Exception exception)
        {
            Log("the dry run failed: " + exception);
            Console.Error.WriteLine("[Neo.Stage] the dry run failed: " + exception);
        }
        finally
        {
            _playing = false;
            if (_live != null)
                _live.Enabled = true;
        }
    }

    private static Screen PrimaryScreen()
    {
        var probe = new Window();
        try { return probe.Screens.Primary ?? probe.Screens.All[0]; }
        finally { probe.Close(); }
    }

    private static DryRun Prepare(StageOverlay stage)
    {
        var size = stage.StageSize;
        var windows = OperatingSystem.IsWindows() ? DesktopTargets.Find(stage.ScreenBounds) : [];
        if (windows.Count == 0)
        {
            var spot = new Rect(size.Width * 0.12, size.Height * 0.25, size.Width * 0.3, size.Height * 0.35);
            return new DryRun(size, spot, "Desktop", Opposite(size, spot), "Desktop", null);
        }

        var (first, second) = DesktopTargets.PickTwo(windows);
        Log($"{windows.Count} windows; first {first.Name} {first.Bounds}, second {second?.Name ?? "none"} {second?.Bounds}");
        var firstRect = stage.FromScreen(first.Bounds);
        var secondRect = second != null ? stage.FromScreen(second.Bounds) : Opposite(size, firstRect);
        return new DryRun(size, firstRect, Short(first.Name), secondRect, Short(second?.Name ?? "Desktop"), DesktopTargets.Picture(first));
    }

    // Where the second spot goes when there is no second window: the other side of the screen.
    private static Rect Opposite(Size size, Rect first)
    {
        var width = Math.Min(first.Width, size.Width * 0.3);
        var height = Math.Min(first.Height, size.Height * 0.35);
        var left = first.Center.X < size.Width / 2 ? size.Width * 0.6 : size.Width * 0.08;
        return new Rect(left, size.Height * 0.4, width, height);
    }

    private static string Short(string name) => name.Length <= 28 ? name : name[..27] + "…";

    private void AddTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var play = new NativeMenuItem("Probelauf abspielen");
        play.Click += (_, _) => _ = PlayAsync();
        var quit = new NativeMenuItem("Beenden");
        quit.Click += (_, _) => desktop.Shutdown();
        var menu = new NativeMenu();
        menu.Add(play);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);

        var icon = new TrayIcon { Icon = Icon(), ToolTipText = "N.E.O. Stage", Menu = menu };
        icon.Clicked += (_, _) => _ = PlayAsync();
        TrayIcon.SetIcons(this, [icon]);
    }

    // The ring, as the stage shows it.
    private static WindowIcon Icon()
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var center = new Point(32, 32);
            context.DrawEllipse(new RadialGradientBrush
            {
                GradientStops = { new GradientStop(Palette.Listen.WithAlpha(0.6), 0), new GradientStop(Palette.Listen.WithAlpha(0), 1) },
            }, null, center, 31, 31);
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Palette.Listen), 5), center, 18, 18);
            context.DrawEllipse(Brushes.White, null, center, 6, 6);
        }
        var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
