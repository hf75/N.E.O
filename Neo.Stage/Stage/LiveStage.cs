using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Neo.ClaudeAgent;

namespace Neo.Stage;

/// <summary>Something pointed at during an utterance: when, where the pointer rested, what lay there, and a picture of it (PNG).</summary>
internal sealed record Pointing(double Time, Point At, PointedThing Thing, byte[]? Picture = null);

/// <summary>One go, from pressing the key to letting it go: what was said, the pointer's way and what it rested on. The brain gets this.</summary>
internal sealed class Utterance
{
    public required double Started { get; init; }
    public double Ended { get; set; }
    public List<(double Time, Point Position)> Trail { get; } = [];
    public List<Pointing> Pointings { get; } = [];

    /// <summary>What was heard so far, or in the end.</summary>
    public string Text { get; set; } = "";

    /// <summary>The final words with their times (seconds from <see cref="Started"/>), when the recognition gave them.</summary>
    public IReadOnlyList<HeardWord>? Heard { get; set; }

    /// <summary>Not said by the person: the stage tells the brain something (a guide's step was done). Shown without words.</summary>
    public bool Unspoken { get; init; }

    /// <summary>An app's words, not the person's: the app that speaks or asks through the stage (<see cref="StageOnNetwork"/>).</summary>
    public string? App { get; init; }

    /// <summary>For an app's words: done once they are said, with the person's answer when the app asked.</summary>
    public TaskCompletionSource<string?>? Done { get; init; }
}

/// <summary>
/// The stage at work. Hold the right Ctrl key and speak: a ring appears low in the middle of the screen and breathes with
/// the voice, and the words come above it as they are heard. Let go: the final words come, the brain thinks (the ring
/// spins, three dots), its answer appears above the words as it is written and is spoken sentence by sentence while the
/// rest is still being written; the ring breathes with the stage's own voice, and every window the brain arranges, shows
/// or looks at is outlined and followed. Then everything fades. Pressing the key again silences the answer and listens
/// anew. A shortcut with the right Ctrl (Ctrl+C) never starts it; the microphone is open only while the key is held.
/// When the brain wants to do something that lasts, the stage asks aloud and waits: the person holds the key and says yes
/// or no, and the brain goes on with that. Something that takes a while (N.E.O. building an app) shows as a pill with its
/// seconds beside the ring.
/// <para>
/// With pointing on, the ring sits at the mouse pointer instead, its trail follows it, what it rests on is outlined and
/// pointing words ("das hier", "da") become chips in the colour of what was pointed at when they were said; without a
/// brain, letting go shows that as a plan.
/// </para>
/// </summary>
internal sealed class LiveStage
{
    private const double ArmDelay = 0.2, Dwell = 0.33, DwellRadius = 7, PlanFor = 1.4, FadeOut = 0.5, PartialDelay = 0.7, HoldAfter = 1.2;
    private const int CaptionCharacters = 64;
    private static readonly Color[] s_colors = [Palette.That, Palette.There, Color.FromRgb(0xF4, 0x7A, 0xC8), Palette.Yes];

    private readonly StageOverlay _stage;
    private readonly DispatcherTimer _watch;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Func<bool>? _keyHeld;
    private readonly Func<PixelPoint>? _pointer;
    private readonly Func<string, Ears?> _hearing;
    private readonly Brain? _brain;
    private readonly Mouth? _mouth;
    private readonly bool _pointing;
    private readonly List<double> _wordShown = [];
    private readonly List<Touch> _touched = [];
    private readonly List<string> _written = []; // the answer's finished pieces; a tool call can stand between two
    private string _writing = "";
    private Phase _phase = Phase.Idle;
    private double _keyDown, _listening, _released, _planFrom = double.NaN, _voice, _heard;
    private double _thinkingFrom, _answeringFrom, _fadeFrom = double.NaN, _speaking, _spoken, _looked;
    private bool _speakingNow;
    private Utterance? _utterance;
    private Utterance? _turn; // the utterance the brain is answering; a reply to its question does not replace it
    private TaskCompletionSource<string?>? _question;
    private bool _replying, _wroteAny;
    private double _askedAt;
    private string? _working;
    private Guide? _guide;
    private double _guideShownAt, _guideEndedAt = double.NaN, _onTopAt;
    private readonly List<(int Number, string Name, PixelRect Bounds, double At)> _noted = []; // steps the recorder just noted
    private int _followUps; // how often a guide went on by itself since the person last spoke
    private int _stepsDone; // steps the guides showed since the person last spoke: a guide that goes on counts on from there
    private (Guide Guide, string Step, IReadOnlyList<string> Fresh, IReadOnlyList<string> Others)? _goOn; // waits until the stage is quiet
    private readonly Queue<(string App, string Text, bool Asking, TaskCompletionSource<string?> Done, string? Note)> _fromApps = new(); // wait until the stage is free
    private double _workingFrom;
    private Ears? _ears;
    private bool _probing, _looping;
    private Point _probedAt = new(double.NaN, double.NaN);
    private Point _captionAt;

    private enum Phase { Idle, Armed, Blocked, Listening, Releasing, Thinking, Answering, Asking }

    /// <summary>A window the brain arranged, showed or looked at, and where it is seen now (stage coordinates).</summary>
    private sealed class Touch(AppWindow window, double at, Color color)
    {
        public AppWindow Window { get; } = window;
        public Color Color { get; } = color;
        public double At { get; } = at;
        public double Glow { get; set; } = at;
        public Rect? Target { get; set; }
        public Rect? Shown { get; set; }
    }

    /// <param name="keyHeld">Stands in for the right Ctrl key (a check without input); null watches the key.</param>
    /// <param name="hearing">Opens the ears with the API key; null listens to the microphone.</param>
    /// <param name="brain">Understands and answers; without one, letting go shows a plan.</param>
    /// <param name="mouth">Speaks the answer; without one, it is only shown.</param>
    /// <param name="pointer">Stands in for the mouse pointer, in screen pixels (a check that leaves the mouse alone); null follows the pointer.</param>
    /// <param name="pointing">Pointing with the mouse while speaking (Put That There); off, the stage listens to the voice alone.</param>
    public LiveStage(StageOverlay stage, Func<bool>? keyHeld = null, Func<string, Ears?>? hearing = null, Brain? brain = null, Mouth? mouth = null,
        Func<PixelPoint>? pointer = null, bool pointing = false)
    {
        _stage = stage;
        _keyHeld = keyHeld;
        _pointer = pointer;
        _hearing = hearing ?? (key => Ears.Listen(key, Language.Code));
        _brain = brain;
        _mouth = mouth;
        _pointing = pointing;
        _watch = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => Watch());
        if (brain != null)
        {
            brain.Event += agentEvent =>
            {
                if (agentEvent is ClaudeAgentTextDelta delta)
                    Dispatcher.UIThread.Post(() => Written(delta.Text, finished: false));
                else if (agentEvent is ClaudeAgentText text)
                    Dispatcher.UIThread.Post(() => Written(text.Text, finished: true));
            };
            brain.Touched += window => Dispatcher.UIThread.Post(() => Touched(window));
            brain.Working += what => Dispatcher.UIThread.Post(() =>
            {
                _working = what;
                _workingFrom = Now;
                // Something takes a while and nothing has been said yet: the stage says what is happening, so that the
                // person is not left waiting in silence.
                if (what != null && !_wroteAny && _phase is Phase.Thinking or Phase.Answering)
                    Written(what + ".", finished: true);
            });
            brain.AskPerson = AskPersonAsync;
            brain.Guiding += guide => Dispatcher.UIThread.Post(() => StartGuide(guide));
            brain.Recorder.Changed += recording =>
            {
                if (recording)
                    Loop();
            };
            brain.Recorder.Stepped += (number, step, bounds) => Dispatcher.UIThread.Post(() =>
            {
                _noted.Add((number, step.Find, bounds, Now));
                Loop();
            });
            brain.Watchers.Fired += (who, said) => _ = FromAppAsync(who, said, asking: false,
                note: Language.Pick($"{who} meldete: „{said}“", $"{who} said: “{said}”"));
            brain.Crew.Done += (who, said, whole) => _ = FromAppAsync(who, said, asking: false, note: whole);
        }
    }

    /// <summary>An utterance is over, answered or not.</summary>
    public event Action<Utterance>? Spoken;

    /// <summary>Off while something else uses the stage (the dry run).</summary>
    public bool Enabled { get; set; } = true;

    public bool Busy => _phase is Phase.Listening or Phase.Releasing or Phase.Thinking or Phase.Answering or Phase.Asking;

    /// <summary>The brain asked the person something and waits for the answer.</summary>
    public bool Asking => _phase == Phase.Asking;

    public void Start() => _watch.Start();

    private double Now => _clock.Elapsed.TotalSeconds;

    private bool KeyHeld() => _keyHeld?.Invoke() ?? IsDown(VK_RCONTROL);

    private bool Chord() => _keyHeld == null && AnotherKeyDown();

    // Between utterances only the key is watched, about 33 times a second. While the stage thinks or answers, the key
    // interrupts it.
    private void Watch()
    {
        var down = KeyHeld();
        switch (_phase)
        {
            case Phase.Idle or Phase.Thinking or Phase.Answering or Phase.Asking when down && Enabled:
                if (_phase == Phase.Asking)
                {
                    _replying = true; // the person answers the question; the brain waits for it
                    _mouth?.Stop();
                }
                else if (_phase != Phase.Idle)
                {
                    Silence();
                }
                _phase = Chord() ? Phase.Blocked : Phase.Armed;
                _keyDown = Now;
                break;
            case Phase.Armed or Phase.Blocked when !down && _replying:
                _replying = false;
                _phase = Phase.Asking; // only a tap: the question still stands
                break;
            case Phase.Armed or Phase.Blocked when !down:
                // A tap: what was still on the stage fades out to its end, or the stage goes away at once.
                if (_looping)
                {
                    _phase = Phase.Answering;
                    if (double.IsNaN(_fadeFrom))
                        _fadeFrom = Now;
                }
                else
                {
                    _phase = Phase.Idle;
                    _stage.Hide();
                }
                break;
            case Phase.Armed when Chord():
                _phase = Phase.Blocked; // a shortcut, not speech
                break;
            case Phase.Armed when Now - _keyDown >= ArmDelay:
                BeginListening();
                break;
        }
    }

    // The person speaks again: the answer falls silent and fades, and the brain stops.
    private void Silence()
    {
        _mouth?.Stop();
        if (_brain != null)
            _ = _brain.InterruptAsync();
        if (double.IsNaN(_fadeFrom) || _fadeFrom > Now)
            _fadeFrom = Now;
        App.Log("interrupted by the key");
    }

    private string Answer => string.Join(" ", _written.Append(_writing).Select(piece => piece.Trim()).Where(piece => piece.Length > 0));

    private void BeginListening()
    {
        _phase = Phase.Listening;
        _listening = Now;
        _planFrom = double.NaN;
        _fadeFrom = double.NaN;
        _voice = 0;
        _speaking = 0;
        _spoken = 0;
        _written.Clear();
        _writing = "";
        _wordShown.Clear();
        if (!_replying)
        {
            _touched.Clear();
            _turn?.Done?.TrySetResult(null); // an app's words the person spoke into are over
            _guide?.Stop(); // the person speaks again: the guide is over
            _guide = null;
            _goOn = null;
        }
        var utterance = _utterance = new Utterance { Started = _listening };
        _probedAt = new Point(double.NaN, double.NaN);

        if (Ears.Key() is { } key)
        {
            try
            {
                var ears = _ears = _hearing(key);
                if (ears != null)
                    ears.Partial += text => Dispatcher.UIThread.Post(() =>
                    {
                        if (_utterance == utterance)
                            Hearing(utterance, text);
                    });
            }
            catch (Exception exception) when (exception is NAudio.MmException or InvalidOperationException)
            {
                _ears = null;
                App.Log("no microphone: " + exception.Message);
            }
        }

        _stage.Display(StageFrame.Empty);
        _stage.Show();
        if (!_looping)
        {
            _looping = true;
            _stage.RequestAnimationFrame(Tick);
        }
        App.Log("listening" + (_ears == null ? " (without ears)" : ""));
    }

    // A word is shown when it is first heard; a partial result comes about PartialDelay after it was said.
    private void Hearing(Utterance utterance, string text)
    {
        utterance.Text = text;
        var count = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        while (_wordShown.Count < count)
            _wordShown.Add(Now);
    }

    private void Tick(TimeSpan _)
    {
        var time = Now;
        var utterance = _utterance!;
        var pointer = _pointing ? PointerOnStage() : null;

        if (_phase == Phase.Listening)
        {
            if (!KeyHeld() || Chord())
                Release(utterance, time);
            else if (pointer is { } at)
            {
                utterance.Trail.Add((time, at));
                if (Resting(utterance, time, at) && !_probing && (double.IsNaN(_probedAt.X) || Point.Distance(at, _probedAt) > DwellRadius * 2))
                    Probe(utterance, at);
            }
        }
        if (_phase == Phase.Releasing && double.IsNaN(_planFrom) && time - _released > 6)
            _planFrom = time; // the recognition did not answer: go on without its last word

        _voice = _voice * 0.75 + (_ears?.Level ?? 0) * 0.25;
        _speaking = _speaking * 0.85 + (_speakingNow ? 1 : 0) * 0.15;
        _spoken = _spoken * 0.55 + (_mouth?.Level ?? 0) * 0.45;
        FollowTouched(time);
        var fadeFrom = FadeFrom();
        if (_guide != null && !double.IsNaN(_guideEndedAt) && time - _guideEndedAt > 0.7)
            _guide = null; // faded out
        if ((_guide?.Shown != null || _noted.Count > 0 || Watching) && time - _onTopAt > 0.5)
        {
            _onTopAt = time;
            _stage.StayOnTop(); // above a menu the program opened meanwhile, where the lit step may be
        }
        _stage.Display(_phase == Phase.Idle ? IdleFrame(time)
            : _pointing ? PointingFrame(utterance, time, pointer, fadeFrom) : VoiceFrame(utterance, time, fadeFrom));

        if (time - fadeFrom > FadeOut + 0.1 && _phase is Phase.Releasing or Phase.Answering)
        {
            _phase = Phase.Idle;
            _turn?.Done?.TrySetResult(null); // an app's words are said (the answer it asked for came before)
            Spoken?.Invoke(utterance);
        }
        if (_phase == Phase.Idle && _goOn is { } waiting)
            GoOn(waiting.Guide, waiting.Step, waiting.Fresh, waiting.Others);
        if (_phase == Phase.Idle)
            NextFromApp();
        _noted.RemoveAll(noted => time - noted.At > 1.8);
        if (_phase == Phase.Idle && _guide == null && !Watching && _noted.Count == 0)
        {
            _looping = false;
            _stage.Display(StageFrame.Empty);
            _stage.Hide();
            return;
        }
        if (_phase is Phase.Armed or Phase.Blocked && fadeFrom < double.PositiveInfinity && time - fadeFrom > FadeOut + 0.1)
        {
            _looping = false; // the key is held for a new utterance; BeginListening starts the loop again
            _stage.Display(StageFrame.Empty);
            return;
        }
        _stage.RequestAnimationFrame(Tick);
    }

    private double FadeFrom() => _phase switch
    {
        Phase.Releasing when !double.IsNaN(_planFrom) => _planFrom + PlanFor,
        Phase.Answering or Phase.Armed or Phase.Blocked when !double.IsNaN(_fadeFrom) => _fadeFrom,
        _ => double.PositiveInfinity,
    };

    private void Release(Utterance utterance, double time)
    {
        _phase = Phase.Releasing;
        _released = time;
        utterance.Ended = time;
        App.Log($"released after {time - utterance.Started:0.0} s{(_pointing ? $", {utterance.Pointings.Count} things pointed at" : "")}");
        if (_ears is not { } ears)
        {
            Heard(utterance);
            return;
        }
        _ears = null;
        _ = FinishHearingAsync(ears, utterance);
    }

    private async Task FinishHearingAsync(Ears ears, Utterance utterance)
    {
        var heard = await ears.FinishAsync(TimeSpan.FromSeconds(4));
        var problem = ears.Problem;
        App.Log("ears: " + ears.Trace);
        await ears.DisposeAsync();
        Dispatcher.UIThread.Post(() =>
        {
            if (_utterance != utterance)
                return;
            if (heard != null)
            {
                utterance.Text = heard.Text;
                if (heard.Final)
                    utterance.Heard = heard.Words;
            }
            if (problem != null)
                App.Log("ears: " + problem);
            App.Log($"heard: '{utterance.Text}'{(utterance.Heard != null ? $", {utterance.Heard.Count} words with times" : "")}");
            Heard(utterance);
        });
    }

    // The words are in: the answer to the brain's question, or a new question for it, or the plan.
    private void Heard(Utterance utterance)
    {
        _heard = Now;
        if (_replying && _question is { } question)
        {
            _replying = false;
            _question = null;
            var reply = utterance.Text.Trim();
            App.Log($"replied: '{reply}'");
            if (_turn?.App is { } asker)
            {
                // An app asked: it gets the answer, the stage goes quiet; what the app makes of it, it says itself.
                _brain?.Note(Language.Pick($"Der Mensch antwortete der App „{asker}“: „{reply}“", $"The person answered the app “{asker}”: “{reply}”"));
                question.TrySetResult(reply);
                _phase = Phase.Answering;
                _fadeFrom = Now + 0.6;
                return;
            }
            _phase = Phase.Thinking;
            _thinkingFrom = Now;
            _written.Clear();
            _writing = "";
            _mouth?.Begin(); // what it says after the answer is a new speech
            question.TrySetResult(reply.Length > 0 ? reply : null);
            return;
        }
        if (_brain == null || utterance.Text.Trim().Length == 0)
        {
            _planFrom = Now;
            return;
        }
        _turn = utterance;
        _wroteAny = false;
        _followUps = 0;
        _stepsDone = 0;
        _phase = Phase.Thinking;
        _thinkingFrom = Now;
        _written.Clear();
        _writing = "";
        _mouth?.Begin();
        _ = AnswerAsync(utterance);
    }

    private async Task AnswerAsync(Utterance utterance)
    {
        string text;
        try
        {
            var answer = await _brain!.AskAsync(utterance);
            var result = answer.Turn;
            text = answer.Text;
            App.Log($"brain: {(result.Succeeded ? "answered" : "stopped: " + result.Subtype)} in {result.Duration.TotalSeconds:0.0} s " +
                    $"({result.Rounds} rounds, {result.CostUsd * 100:0.00} ct): '{text}'");
            if (!result.Succeeded && text.Length == 0)
                text = "Das ging gerade nicht.";
        }
        catch (Exception exception) when (exception is ClaudeAgentException or ClaudeCliNotFoundException or InvalidOperationException or OperationCanceledException)
        {
            App.Log("brain: " + exception.Message);
            text = "Ich komme gerade nicht an mein Gehirn heran.";
        }

        Dispatcher.UIThread.Post(() =>
        {
            _working = null;
            if (_turn != utterance || _phase is not (Phase.Thinking or Phase.Answering))
                return;
            // Nothing came while it was written (it failed, say): the voice gets all of it now.
            if (!_wroteAny)
                _mouth?.Add(text);
            _written.Clear();
            _written.Add(text);
            _writing = "";
            Answering();
            _ = SpeakAsync(utterance, text);
        });
    }

    // The answer as it is written: the three dots give way to its words, and each finished sentence goes to the voice at
    // once. A finished piece replaces what was streamed of it, and what of it did not come streamed is spoken too.
    private void Written(string text, bool finished)
    {
        if (_phase is not (Phase.Thinking or Phase.Answering))
            return;
        _wroteAny = true;
        if (finished)
        {
            if (text.StartsWith(_writing, StringComparison.Ordinal))
                _mouth?.Add(text[_writing.Length..]);
            _mouth?.Flush(); // a tool may come next: say what stands before it
            _written.Add(text);
            _writing = "";
        }
        else
        {
            _writing += text;
            _mouth?.Add(text);
        }
        Answering();
    }

    private void Answering()
    {
        if (_phase == Phase.Answering)
            return;
        _phase = Phase.Answering;
        _answeringFrom = Now;
        _speakingNow = _mouth != null;
    }

    private async Task SpeakAsync(Utterance utterance, string text)
    {
        if (_mouth != null)
        {
            _speakingNow = true;
            await _mouth.FinishAsync();
            _speakingNow = false;
            if (_mouth.Problem is { } problem)
                App.Log("mouth: " + problem);
        }
        else
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5 + 0.045 * text.Length));
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (_turn == utterance && _phase == Phase.Answering)
                _fadeFrom = Now + HoldAfter;
        });
    }

    // The brain wants to do something that lasts: the stage asks aloud and waits for the person's answer, at most half a
    // minute. The answer is what they say next with the key held.
    private Task<string?> AskPersonAsync(string question, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            if (_phase is not (Phase.Thinking or Phase.Answering))
            {
                answer.TrySetResult(null);
                return;
            }
            _question = answer;
            _phase = Phase.Asking;
            _askedAt = Now;
            if (_answeringFrom < _thinkingFrom)
                _answeringFrom = Now;
            _written.Add(question);
            _writing = "";
            _mouth?.Add(" " + question);
            _mouth?.Flush();
            App.Log($"asking: '{question}'");
        });
        _ = Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (_question != answer || _replying)
                return;
            _question = null;
            _phase = Phase.Thinking;
            answer.TrySetResult(null);
        }), TaskScheduler.Default);
        cancellationToken.Register(() => answer.TrySetResult(null));
        return answer.Task;
    }

    // ── Windows the brain touched ──

    private void Touched(AppWindow window)
    {
        if (_phase is not (Phase.Thinking or Phase.Answering or Phase.Asking))
            return;
        if (_touched.FirstOrDefault(touch => touch.Window.Handle == window.Handle) is { } known)
        {
            known.Glow = Now; // touched again: it lights up again
            return;
        }
        _touched.Add(new Touch(window, Now, s_colors[_touched.Count % s_colors.Length]));
        _looked = 0; // look where it is at once
    }

    // Where the touched windows are seen now, asked every tenth of a second; the outlines glide after them.
    private void FollowTouched(double time)
    {
        if (_touched.Count == 0)
            return;
        if (time - _looked >= 0.1)
        {
            _looked = time;
            foreach (var touch in _touched)
                touch.Target = OpenWindows.Seen(touch.Window.Handle) is { } seen && _stage.FromScreen(seen) is var rect && rect.Intersects(new Rect(_stage.StageSize)) ? rect : null;
        }
        foreach (var touch in _touched)
        {
            if (touch.Target is not { } target)
                touch.Shown = null;
            else if (touch.Shown is not { } shown)
                touch.Shown = target;
            else
                touch.Shown = new Rect(Motion.Lerp(shown.X, target.X, 0.3), Motion.Lerp(shown.Y, target.Y, 0.3),
                    Motion.Lerp(shown.Width, target.Width, 0.3), Motion.Lerp(shown.Height, target.Height, 0.3));
        }
    }

    private IEnumerable<TargetView> TouchedTargets(double time, double leaving)
        => _touched.Where(touch => touch.Shown != null).Select(touch =>
        {
            var grow = Motion.EaseOutCubic(Motion.Progress(time, touch.At, 0.34));
            var opacity = Motion.EaseOutCubic(Motion.Progress(time, touch.At, 0.3)) * (1 - leaving);
            var label = Motion.EaseOutCubic(Motion.Progress(time, touch.At + 0.1, 0.25)) * (1 - leaving);
            var glow = Math.Max(Motion.Hump(time, touch.Glow, 0.9), 0.15);
            return new TargetView(touch.Shown!.Value, touch.Window.Label, touch.Color, opacity, Motion.Lerp(22, 6, grow), glow, label);
        });

    // ── Apps that speak up: N.E.O. apps say things and ask through the stage ──

    /// <summary>
    /// An app on the app network speaks up (<see cref="StageOnNetwork"/>). As soon as the stage is free (never into the
    /// person's words or another answer) it says <paramref name="text"/> aloud, with the app's name and its window outlined.
    /// Asking, it waits up to a minute for the person's spoken answer and returns it; otherwise it returns null once the
    /// words are said. Any thread.
    /// </summary>
    public Task<string?> FromAppAsync(string? app, string text, bool asking, string? note = null)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        text = text.Trim();
        if (text.Length == 0)
        {
            done.TrySetResult(null);
            return done.Task;
        }
        Dispatcher.UIThread.Post(() =>
        {
            _fromApps.Enqueue((string.IsNullOrWhiteSpace(app) ? Language.Pick("Eine App", "An app") : app.Trim(), text, asking, done, note));
            if (app != null)
                _brain?.AppSpeaks(app); // a call to it the brain is waiting for goes on by itself
            NextFromApp();
        });
        return done.Task;
    }

    // The next app's words, when the stage is free.
    private void NextFromApp()
    {
        if (_phase != Phase.Idle || !Enabled || !_fromApps.TryDequeue(out var next))
            return;
        var (app, text, asking, done, note) = next;
        var utterance = new Utterance { Started = Now, Ended = Now, Text = text, Unspoken = true, App = app, Done = done };
        _utterance = utterance;
        _turn = utterance;
        _listening = _released = _thinkingFrom = _answeringFrom = Now;
        _fadeFrom = double.NaN;
        _planFrom = double.NaN;
        _written.Clear();
        _written.Add(text);
        _writing = "";
        _wordShown.Clear();
        _wroteAny = true;
        _touched.Clear();
        _working = null;
        _mouth?.Begin();
        _mouth?.Add(text);
        App.Log($"app '{app}' {(asking ? "asks" : "says")}: '{text}'");
        _brain?.Note(note ?? (asking
            ? Language.Pick($"Die App „{app}“ fragte den Menschen: „{text}“", $"The app “{app}” asked the person: “{text}”")
            : Language.Pick($"Die App „{app}“ sagte: „{text}“", $"The app “{app}” said: “{text}”")));
        if (asking)
        {
            _mouth?.Flush();
            _question = done;
            _phase = Phase.Asking;
            _askedAt = Now;
            // A minute without an answer: the question goes, the app gets an empty answer.
            DispatcherTimer.RunOnce(() =>
            {
                if (_question != done || _replying)
                    return;
                _question = null;
                done.TrySetResult(null);
                App.Log($"app '{app}': no answer");
                if (_turn == utterance)
                {
                    _phase = Phase.Answering;
                    _fadeFrom = Now;
                }
            }, TimeSpan.FromSeconds(60));
        }
        else
        {
            _phase = Phase.Answering;
            _speakingNow = _mouth != null;
            _ = SpeakAsync(utterance, text);
        }
        // The app's window, outlined with its name: who speaks.
        if (Neo.App.Apps.Find(app) is { } descriptor
            && OpenWindows.List(most: 64).FirstOrDefault(window => window.ProcessId == (uint)descriptor.ProcessId) is { } window)
            Touched(window);
        if (!_looping)
        {
            _looping = true;
            _stage.Show();
            _stage.RequestAnimationFrame(Tick);
        }
    }

    // ── Show me: a guide lights up what to click, step by step ──

    private void StartGuide(Guide guide)
    {
        _guide?.Stop();
        _guide = guide;
        guide.Before = _stepsDone;
        _guideShownAt = Now;
        _guideEndedAt = double.NaN;
        guide.StepShown += (index, element) => Dispatcher.UIThread.Post(() =>
        {
            if (_guide != guide)
                return;
            _guideShownAt = Now;
            // The first step is in the brain's answer; each next one the stage says itself, when it is not speaking anyway.
            if (index > 0 && _phase == Phase.Idle && guide.Steps[index].Say is { Length: > 0 } say && _mouth != null)
                _ = _mouth.SpeakAsync(say);
        });
        guide.Ended += () => Dispatcher.UIThread.Post(() =>
        {
            if (_guide == guide)
                _guideEndedAt = Now;
        });
        guide.Progressed += (step, fresh, others) => Dispatcher.UIThread.Post(() => GoOn(guide, step, fresh, others));
        if (!_looping)
        {
            _looping = true;
            _stage.Show();
            _stage.RequestAnimationFrame(Tick);
        }
    }

    // The person did the last step shown: the brain looks at what is there now and shows the next one, or says it is done.
    // At most six times in a row without the person speaking.
    private void GoOn(Guide guide, string step, IReadOnlyList<string> fresh, IReadOnlyList<string> others)
    {
        // The person spoke meanwhile, or a newer guide is on: nothing to go on with. (This one may have faded out already.)
        if (guide.Stopped || (_guide != null && _guide != guide) || _brain == null)
            return;
        if (_phase != Phase.Idle)
        {
            _goOn = (guide, step, fresh, others); // the stage is still answering: right after that
            return;
        }
        _goOn = null;
        if (++_followUps > 6)
            return;
        _stepsDone = guide.Before + guide.Current + 1;
        // What came new is what the step opened: it goes first, the rest only as far as it helps.
        var rest = string.Join(", ", others.Take(Math.Max(20, 60 - fresh.Count)));
        var besides = rest.Length > 0 ? Language.Pick($"Außerdem: {rest}. ", $"Besides: {rest}. ") : "";
        // What the guide saw is only that the window changed: whether the person did the step, the names have to tell.
        var said = fresh.Count > 0
            ? Language.Pick(
                $"Nach dem Schritt „{step}“ hat sich „{guide.Title}“ verändert. Neu anklickbar ist jetzt: {string.Join(", ", fresh)}. {besides}",
                $"After the step “{step}”, “{guide.Title}” changed. New to click there now: {string.Join(", ", fresh)}. {besides}")
            : Language.Pick(
                $"„{step}“ ist in „{guide.Title}“ nicht mehr zu sehen, und nichts Neues kam dazu: Der Mensch hat es angeklickt, " +
                $"oder er hat nur das Menü geschlossen. Schau im Zweifel mit look nach. Anklickbar ist jetzt: {rest}. ",
                $"“{step}” can no longer be seen in “{guide.Title}”, and nothing new came: the person clicked it, or only closed " +
                $"the menu. If in doubt, look. What can be clicked now: {rest}. ");
        said += Language.Pick(
            $"Sein Ziel war: „{guide.Goal}“. Passt das Neue zu dem, was dieser Schritt öffnet, zeig mit guide den nächsten Schritt, oder sag in einem " +
            "kurzen Satz, dass er am Ziel ist. Passt es nicht (er ist woanders hingegangen), zeig den Schritt noch einmal. Behaupte nichts, was die Namen nicht zeigen.",
            $"Their goal was: “{guide.Goal}”. If what is new fits what this step opens, show the next step with guide, or say in one short " +
            "sentence that they are there. If it does not fit (they went elsewhere), show the step again. Claim nothing the names do not show.");
        var utterance = new Utterance { Started = Now, Ended = Now, Text = said, Unspoken = true };
        _utterance = utterance;
        _turn = utterance;
        _listening = _released = _thinkingFrom = Now;
        _fadeFrom = double.NaN;
        _planFrom = double.NaN;
        _written.Clear();
        _writing = "";
        _wordShown.Clear();
        _wroteAny = false;
        _phase = Phase.Thinking;
        _mouth?.Begin();
        App.Log($"guide: going on after '{step}'");
        _ = AnswerAsync(utterance);
    }

    // The lit step: a green outline that breathes, with its number and name.
    private IEnumerable<TargetView> GuideTargets(double time)
    {
        if (_guide is not { Shown: { } shown } guide)
            yield break;
        var leaving = double.IsNaN(_guideEndedAt) ? 0 : Motion.EaseInOutCubic(Motion.Progress(time, _guideEndedAt, 0.6));
        var appear = Motion.EaseOutCubic(Motion.Progress(time, _guideShownAt, 0.4));
        var pulse = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (time - _guideShownAt) / 1.2);
        yield return new TargetView(_stage.FromScreen(shown.Bounds), $"{guide.Before + guide.Current + 1} · {shown.Name}", Palette.Yes,
            appear * (1 - leaving), Motion.Lerp(30, 5, appear), 0.35 + 0.65 * pulse, appear * (1 - leaving));
    }

    // Between utterances: the lit step of a guide, the steps just noted, and while the stage watches, its light.
    private StageFrame IdleFrame(double time)
    {
        var work = _stage.WorkArea;
        return new StageFrame(null, [], [.. GuideTargets(time), .. NotedTargets(time)], [], null, null,
            [.. RecordPill(time, new Point(work.Center.X, work.Bottom - 70), PillAlign.Center)]);
    }

    private bool Watching => _brain?.Recorder.Recording == true;

    // The stage starts drawing (again) when something shows between utterances.
    private void Loop()
    {
        if (_looping)
            return;
        _looping = true;
        _stage.Show();
        _stage.RequestAnimationFrame(Tick);
    }

    // A step just noted: a red outline that flares and fades, with its number and name.
    private IEnumerable<TargetView> NotedTargets(double time)
        => _noted.Select(noted =>
        {
            var age = time - noted.At;
            var appear = Motion.EaseOutCubic(Motion.Progress(time, noted.At, 0.25));
            var fade = 1 - Motion.EaseInOutCubic(Motion.Progress(time, noted.At + 1.0, 0.8));
            return new TargetView(_stage.FromScreen(noted.Bounds), $"{noted.Number} · {noted.Name}", Palette.Record, appear * fade,
                Motion.Lerp(26, 5, appear), Math.Max(0.2, 1 - age), appear * fade);
        });

    // While the stage watches: its light and how many steps it has, beside the ring or where the ring would be.
    private IEnumerable<PillView> RecordPill(double time, Point at, PillAlign align)
    {
        if (!Watching)
            yield break;
        var count = _brain!.Recorder.Count;
        yield return new PillView(at, [
                new PillPart(Language.Pick("Ich schaue zu", "Watching"), Palette.Record, Dot: true, Strong: true),
                new PillPart(Language.Pick($"{count} {(count == 1 ? "Schritt" : "Schritte")}", $"{count} {(count == 1 ? "step" : "steps")}"), Palette.TextDim)],
            0.8 + 0.2 * Math.Sin(2 * Math.PI * time / 1.6), 0, align, 13);
    }

    // ── The voice alone ──

    // Low in the middle: the ring, the words above it, the answer above them. Windows the brain touches are outlined.
    private StageFrame VoiceFrame(Utterance utterance, double time, double fadeFrom)
    {
        var leaving = Motion.EaseInOutCubic(Motion.Progress(time, fadeFrom, FadeOut));
        var afterwards = _phase is Phase.Thinking or Phase.Answering or Phase.Asking or Phase.Armed or Phase.Blocked;
        var work = _stage.WorkArea;
        var presence = new Point(work.Center.X, work.Bottom - 70);

        // The ring: it grows with the voice while listening, waits small and spins while thinking, and breathes with the
        // stage's own voice while it answers.
        var appear = Motion.EaseOutBack(Motion.Progress(time, _listening, 0.35));
        var scale = _phase switch
        {
            Phase.Listening => appear * (1 + 0.28 * _voice),
            Phase.Releasing or Phase.Thinking => Motion.Lerp(1, 0.62, Motion.EaseOutCubic(Motion.Progress(time, _released, 0.3))),
            _ => Motion.Lerp(0.62, 0.8, Motion.EaseOutCubic(Motion.Progress(time, _answeringFrom, 0.3))) * (1 + 0.45 * _spoken),
        };
        var spin = time * (_phase is Phase.Releasing or Phase.Thinking ? 300 : 110);
        var ring = new PointerView(presence, Motion.EaseOutCubic(Motion.Progress(time, _listening, 0.2)) * (1 - leaving), scale,
            Math.Sin(2 * Math.PI * time / 1.6), spin, ShowArrow: false);

        var captions = new List<CaptionView>();
        var words = utterance.Unspoken ? [] : Words(utterance);
        var captionBottom = presence.Y - 50;
        if (words.Count > 0)
        {
            // The last words that fit; the earlier ones have been read.
            var from = words.Count;
            var characters = 0;
            while (from > 0 && characters + words[from - 1].Text.Length + 1 <= CaptionCharacters)
                characters += words[--from].Text.Length + 1;
            from = Math.Min(from, words.Count - 1);
            var first = _wordShown.Count > 0 ? _wordShown[0] : time;
            var views = words.Skip(from).Select(word =>
            {
                var shown = word.First < _wordShown.Count ? _wordShown[word.First] : Math.Min(time, _heard > 0 ? _heard : time);
                return new WordView(word.Text, Motion.Progress(time, shown, 0.22));
            }).ToList();
            captions.Add(new CaptionView(new Point(presence.X, captionBottom), views,
                Motion.EaseOutCubic(Motion.Progress(time, first, 0.2)) * (1 - leaving) * (afterwards ? 0.75 : 1), Centered: true));
        }

        AnswerView? answer = null;
        var said = Answer;
        if (_phase is Phase.Thinking or Phase.Answering or Phase.Asking || (_phase is Phase.Armed or Phase.Blocked && leaving < 1 && said.Length > 0))
        {
            answer = new AnswerView(new Point(presence.X, captionBottom - 44 - 14), said,
                Motion.EaseOutCubic(Motion.Progress(time, _thinkingFrom, 0.25)) * (1 - leaving), said.Length == 0, _speaking, time, Above: true);
        }

        // Beside the ring: what is waiting for the person, or what takes a while.
        var pills = new List<PillView>();
        if (_phase == Phase.Asking)
        {
            var shown = Motion.EaseOutCubic(Motion.Progress(time, _askedAt, 0.3));
            var hint = _turn?.App is { } asker
                ? Language.Pick($"{asker} fragt · Taste halten und antworten", $"{asker} asks · hold the key and answer")
                : Language.Pick("Taste halten: Ja oder Nein", "Hold the key: yes or no");
            pills.Add(new PillView(new Point(presence.X + 64, presence.Y), [new PillPart(hint, Palette.Yes, Dot: true, Strong: true)],
                shown * (0.75 + 0.25 * Math.Sin(2 * Math.PI * time / 1.4)), 6 * (1 - shown), PillAlign.Left, 13));
        }
        else if (_turn?.App is { } speaker && _phase is Phase.Answering)
        {
            var begun = Motion.EaseOutCubic(Motion.Progress(time, _answeringFrom, 0.3));
            pills.Add(new PillView(new Point(presence.X + 64, presence.Y), [new PillPart(speaker, Palette.There, Dot: true, Strong: true)],
                begun * (1 - leaving), 6 * (1 - begun), PillAlign.Left, 13));
        }
        else if (_working is { } doing && _phase is Phase.Thinking or Phase.Answering)
        {
            var begun = Motion.EaseOutCubic(Motion.Progress(time, _workingFrom, 0.3));
            var seconds = (int)(time - _workingFrom);
            pills.Add(new PillView(new Point(presence.X + 64, presence.Y),
                [new PillPart(doing, Palette.There, Dot: true, Strong: true), new PillPart($"{seconds} s", Palette.TextDim)], begun * (1 - leaving), 6 * (1 - begun), PillAlign.Left, 13));
        }

        if (pills.Count == 0)
            pills.AddRange(RecordPill(time, new Point(presence.X + 64, presence.Y), PillAlign.Left));
        return new StageFrame(ring, [], [.. TouchedTargets(time, leaving), .. GuideTargets(time), .. NotedTargets(time)], captions, null, null, pills, answer);
    }

    // ── Pointing (Put That There) ──

    // The pointer rests when it has stayed within a few pixels for a moment.
    private static bool Resting(Utterance utterance, double time, Point at)
    {
        var trail = utterance.Trail;
        if (trail.Count < 2 || time - trail[0].Time < Dwell)
            return false;
        for (var i = trail.Count - 1; i >= 0 && time - trail[i].Time <= Dwell; i--)
        {
            if (Point.Distance(trail[i].Position, at) > DwellRadius)
                return false;
        }
        return true;
    }

    // UI Automation and the picture off the UI thread; the answer comes back to it.
    private void Probe(Utterance utterance, Point at)
    {
        _probing = true;
        _probedAt = at;
        var rested = Now - Dwell; // it was already there when the rest began
        var screen = new PixelPoint((int)Math.Round(at.X * _stage.Scaling) + _stage.ScreenBounds.X, (int)Math.Round(at.Y * _stage.Scaling) + _stage.ScreenBounds.Y);
        Task.Run(() =>
        {
            var thing = Uia.At(screen);
            byte[]? picture = null;
            if (thing != null && (utterance.Pointings.Count == 0 || !utterance.Pointings[^1].Thing.SameAs(thing)))
            {
                try { picture = Hands.Picture(thing); }
                catch (Exception exception) { App.Log("picture: " + exception.Message); }
            }
            return (thing, picture);
        }).ContinueWith(found => Dispatcher.UIThread.Post(() =>
        {
            _probing = false;
            if (found.IsFaulted || found.Result.thing is not { } thing || utterance != _utterance || _phase != Phase.Listening)
                return;
            if (utterance.Pointings.Count > 0 && utterance.Pointings[^1].Thing.SameAs(thing))
                return;
            utterance.Pointings.Add(new Pointing(rested, at, thing, found.Result.picture));
            App.Log($"pointed at: {thing.Kind} '{thing.Name}' in {thing.Program}, {thing.Bounds}{(found.Result.picture != null ? $", picture {found.Result.picture.Length / 1024} KB" : "")}");
        }), TaskScheduler.Default);
    }

    /// <summary>The words as they stand: the final ones with their times when there are, else the partial ones.</summary>
    private List<SpokenWord> Words(Utterance utterance)
    {
        if (utterance.Heard is { Count: > 0 } heard)
            return Deixis.Read([.. heard.Select(word => word.Text)], [.. heard.Select(word => utterance.Started + word.Start)]);
        var raw = utterance.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Deixis.Read(raw, [.. raw.Select((_, i) => (i < _wordShown.Count ? _wordShown[i] : Now) - PartialDelay)]);
    }

    private StageFrame PointingFrame(Utterance utterance, double time, Point? pointer, double fadeFrom)
    {
        var leaving = Motion.EaseInOutCubic(Motion.Progress(time, fadeFrom, FadeOut));
        var afterwards = _phase is Phase.Thinking or Phase.Answering or Phase.Armed or Phase.Blocked;

        PointerView? ring = null;
        if (pointer is { } at && _phase is Phase.Listening or Phase.Releasing)
        {
            var appear = Motion.EaseOutBack(Motion.Progress(time, _listening, 0.35));
            var releasing = _phase == Phase.Releasing;
            var shrink = releasing ? Motion.EaseOutCubic(Motion.Progress(time, _released, 0.3)) : 0;
            var opacity = Motion.EaseOutCubic(Motion.Progress(time, _listening, 0.2)) * (releasing ? 1 - Motion.EaseInOutCubic(Motion.Progress(time, _released, 0.45)) : 1);
            ring = new PointerView(at, opacity, Motion.Lerp(appear, 0.42, shrink) * (1 + 0.28 * _voice), Math.Sin(2 * Math.PI * time / 1.6), time * 110, ShowArrow: false);
        }

        var trail = _phase == Phase.Listening
            ? utterance.Trail.Where(point => time - point.Time <= 0.66).Select(point => new TrailPoint(point.Position, time - point.Time)).ToList()
            : (IReadOnlyList<TrailPoint>)[];

        var targets = utterance.Pointings.Select((pointing, index) =>
        {
            var appear = Math.Max(pointing.Time, _listening);
            var grow = Motion.EaseOutCubic(Motion.Progress(time, appear, 0.34));
            var opacity = Motion.EaseOutCubic(Motion.Progress(time, appear, 0.3)) * (1 - leaving) * (afterwards ? 0.85 : 1);
            var label = Motion.EaseOutCubic(Motion.Progress(time, appear + 0.1, 0.25)) * (1 - leaving);
            var thing = pointing.Thing;
            var name = thing.Name.Length > 0 && thing.Kind.Length > 0 ? $"{thing.Name} · {thing.Kind}" : thing.Label;
            var glow = Math.Max(Motion.Hump(time, appear, 0.7), 0.15);
            return new TargetView(_stage.FromScreen(thing.Bounds), name, s_colors[index % s_colors.Length], opacity, Motion.Lerp(22, 6, grow), glow, label);
        }).ToList();

        // The words, and later the answer, ride beside the pointer while it is listening, and stay where it let go.
        if (_phase == Phase.Listening && utterance.Trail.Count > 0)
            _captionAt = CaptionAnchor(utterance, time);
        var captions = new List<CaptionView>();
        var words = Words(utterance);
        if (words.Count > 0)
        {
            var first = _wordShown.Count > 0 ? _wordShown[0] : time;
            var views = words.Select(word =>
            {
                var shown = word.First < _wordShown.Count ? _wordShown[word.First] : time;
                var appear = Motion.Progress(time, shown, 0.22);
                if (!word.Points)
                    return new WordView(word.Text, appear);
                var meant = Deixis.Meant(word.Time, utterance.Pointings);
                var color = meant is { } index ? s_colors[index % s_colors.Length] : Palette.Listen;
                return new WordView(word.Text, appear, color, Motion.Progress(time, shown + 0.1, 0.25), Motion.Progress(time, shown + 0.1, 0.3),
                    meant, Motion.EaseInOutCubic(Motion.Progress(time, shown + 0.25, 0.4)));
            }).ToList();
            captions.Add(new CaptionView(_captionAt, views, Motion.EaseOutCubic(Motion.Progress(time, first, 0.2)) * (1 - leaving) * (afterwards ? 0.75 : 1)));
        }

        var pills = new List<PillView>();
        if (_brain == null && _phase == Phase.Releasing && !double.IsNaN(_planFrom) && utterance.Pointings.Count > 0)
        {
            var plan = Motion.EaseOutCubic(Motion.Progress(time, _planFrom, 0.25)) * (1 - leaving);
            var parts = new List<PillPart>();
            for (var i = 0; i < utterance.Pointings.Count; i++)
            {
                if (i > 0)
                    parts.Add(new PillPart("→", Palette.TextDim));
                parts.Add(new PillPart(utterance.Pointings[i].Thing.Label, s_colors[i % s_colors.Length], Dot: true, Strong: true));
            }
            var last = targets[^1].Bounds;
            var anchor = new Point(Math.Clamp(last.Center.X, 200, _stage.StageSize.Width - 200), Math.Max(last.Top - 54, 40));
            pills.Add(new PillView(anchor, parts, plan, 8 * (1 - plan), PillAlign.Center, 14));
        }

        AnswerView? answer = null;
        var said = Answer;
        if (_phase is Phase.Thinking or Phase.Answering or Phase.Asking || (_phase is Phase.Armed or Phase.Blocked && leaving < 1 && said.Length > 0))
        {
            var size = _stage.StageSize;
            var anchor = new Point(Math.Clamp(_captionAt.X, 24, Math.Max(24, size.Width - 470)), Math.Clamp(_captionAt.Y + 58, 24, Math.Max(24, size.Height - 200)));
            answer = new AnswerView(anchor, said, Motion.EaseOutCubic(Motion.Progress(time, _thinkingFrom, 0.25)) * (1 - leaving),
                said.Length == 0, _speaking, time);
        }

        return new StageFrame(ring, trail, [.. targets, .. TouchedTargets(time, leaving), .. GuideTargets(time)], captions, null, null, pills, answer);
    }

    // The words ride beside the pointer, a little behind it, as if on a spring.
    private Point CaptionAnchor(Utterance utterance, double time)
    {
        var recent = utterance.Trail.Where(point => time - point.Time <= 0.2).Select(point => point.Position).ToList();
        if (recent.Count == 0)
            recent.Add(utterance.Trail[^1].Position);
        var follow = new Point(recent.Average(point => point.X), recent.Average(point => point.Y));
        var size = _stage.StageSize;
        return new Point(Math.Clamp(follow.X + 30, 24, Math.Max(24, size.Width - 520)), Math.Clamp(follow.Y + 30, 24, Math.Max(24, size.Height - 72)));
    }

    private Point? PointerOnStage()
    {
        PixelPoint cursor;
        if (_pointer != null)
            cursor = _pointer();
        else if (GetCursorPos(out var native))
            cursor = new PixelPoint(native.X, native.Y);
        else
            return null;
        var bounds = _stage.ScreenBounds;
        if (cursor.X < bounds.X || cursor.Y < bounds.Y || cursor.X >= bounds.Right || cursor.Y >= bounds.Bottom)
            return null;
        return new Point((cursor.X - bounds.X) / _stage.Scaling, (cursor.Y - bounds.Y) / _stage.Scaling);
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    // Keys that make a held Ctrl a shortcut: letters, digits, space, Enter, Tab, Escape, F-keys, arrows, Delete and the like.
    private static readonly int[] s_otherKeys = [.. Enumerable.Range(0x08, 2), 0x0D, 0x1B, .. Enumerable.Range(0x20, 0x10), .. Enumerable.Range(0x30, 10),
        .. Enumerable.Range(0x41, 26), .. Enumerable.Range(0x70, 12), .. Enumerable.Range(0xBA, 7), .. Enumerable.Range(0xDB, 4)];

    private static bool AnotherKeyDown() => s_otherKeys.Any(IsDown);

    private const int VK_RCONTROL = 0xA3;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
}
