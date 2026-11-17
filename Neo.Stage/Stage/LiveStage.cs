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
}

/// <summary>
/// The stage at work. Hold the right Ctrl key and speak: the ring appears at the real pointer and breathes with the
/// voice, its trail follows the mouse, the words run along beside it, and what the pointer rests on is asked of UI
/// Automation and outlined with its name. A pointing word ("das hier", "da") becomes a chip in the colour of what was
/// pointed at when it was said. Let go: the final words come, the brain thinks (three dots), its answer appears as it is
/// written and is spoken, a window it arranges lights up, then everything fades. Pressing the key again silences the
/// answer and listens anew. A shortcut with the right Ctrl (Ctrl+C) never starts it; the microphone is open only while
/// the key is held. Without a brain, letting go shows what was pointed at as a plan.
/// </summary>
internal sealed class LiveStage
{
    private const double ArmDelay = 0.2, Dwell = 0.33, DwellRadius = 7, PlanFor = 1.4, FadeOut = 0.5, PartialDelay = 0.7;
    private static readonly Color[] s_colors = [Palette.That, Palette.There, Color.FromRgb(0xF4, 0x7A, 0xC8), Palette.Yes];

    private readonly StageOverlay _stage;
    private readonly DispatcherTimer _watch;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Func<bool>? _keyHeld;
    private readonly Func<PixelPoint>? _pointer;
    private readonly Func<string, Ears?> _hearing;
    private readonly Brain? _brain;
    private readonly Mouth? _mouth;
    private readonly List<double> _wordShown = [];
    private readonly Dictionary<int, double> _arranged = [];
    private Phase _phase = Phase.Idle;
    private double _keyDown, _listening, _released, _planFrom = double.NaN, _voice;
    private double _thinkingFrom, _fadeFrom = double.NaN, _speaking;
    private readonly List<string> _written = []; // the answer's finished pieces; a tool call can stand between two
    private string _writing = "";
    private bool _speakingNow;
    private Utterance? _utterance;
    private Ears? _ears;
    private bool _probing, _looping;
    private Point _probedAt = new(double.NaN, double.NaN);
    private Point _captionAt;

    private enum Phase { Idle, Armed, Blocked, Listening, Releasing, Thinking, Answering }

    /// <param name="keyHeld">Stands in for the right Ctrl key (a check without input); null watches the key.</param>
    /// <param name="hearing">Opens the ears with the API key; null listens to the microphone.</param>
    /// <param name="brain">Understands and answers; without one, letting go shows a plan.</param>
    /// <param name="mouth">Speaks the answer; without one, it is only shown.</param>
    /// <param name="pointer">Stands in for the mouse pointer, in screen pixels (a check that leaves the mouse alone); null follows the pointer.</param>
    public LiveStage(StageOverlay stage, Func<bool>? keyHeld = null, Func<string, Ears?>? hearing = null, Brain? brain = null, Mouth? mouth = null,
        Func<PixelPoint>? pointer = null)
    {
        _stage = stage;
        _keyHeld = keyHeld;
        _pointer = pointer;
        _hearing = hearing ?? (key => Ears.Listen(key));
        _brain = brain;
        _mouth = mouth;
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
            brain.Arranged += (index, action) => Dispatcher.UIThread.Post(() => _arranged[index] = Now);
        }
    }

    /// <summary>An utterance is over, answered or not.</summary>
    public event Action<Utterance>? Spoken;

    /// <summary>Off while something else uses the stage (the dry run).</summary>
    public bool Enabled { get; set; } = true;

    public bool Busy => _phase is Phase.Listening or Phase.Releasing or Phase.Thinking or Phase.Answering;

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
            case Phase.Idle or Phase.Thinking or Phase.Answering when down && Enabled:
                if (_phase != Phase.Idle)
                    Silence();
                _phase = Chord() ? Phase.Blocked : Phase.Armed;
                _keyDown = Now;
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
        _written.Clear();
        _writing = "";
        _wordShown.Clear();
        _arranged.Clear();
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
        var pointer = PointerOnStage();

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
        var fadeFrom = FadeFrom();
        _stage.Display(Frame(utterance, time, pointer, fadeFrom));

        if (time - fadeFrom > FadeOut + 0.1 && _phase is Phase.Releasing or Phase.Answering)
        {
            _phase = Phase.Idle;
            _looping = false;
            _stage.Display(StageFrame.Empty);
            _stage.Hide();
            Spoken?.Invoke(utterance);
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
        App.Log($"released after {time - utterance.Started:0.0} s, {utterance.Pointings.Count} things pointed at");
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

    // The words are in: ask the brain, or show the plan.
    private void Heard(Utterance utterance)
    {
        if (_brain == null || utterance.Text.Trim().Length == 0)
        {
            _planFrom = Now;
            return;
        }
        _phase = Phase.Thinking;
        _thinkingFrom = Now;
        _written.Clear();
        _writing = "";
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
            if (_utterance != utterance || _phase is not (Phase.Thinking or Phase.Answering))
                return;
            _written.Clear();
            _written.Add(text);
            _writing = "";
            _phase = Phase.Answering;
            _ = SpeakAsync(utterance, text);
        });
    }

    // The answer as it is written: the three dots give way to its words. A finished piece replaces what was streamed of it.
    private void Written(string text, bool finished)
    {
        if (_phase is not (Phase.Thinking or Phase.Answering))
            return;
        if (finished)
        {
            _written.Add(text);
            _writing = "";
        }
        else
        {
            _writing += text;
        }
        _phase = Phase.Answering;
    }

    private async Task SpeakAsync(Utterance utterance, string text)
    {
        if (_mouth != null)
        {
            _speakingNow = true;
            await _mouth.SpeakAsync(text);
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
            if (_utterance == utterance && _phase == Phase.Answering)
                _fadeFrom = Now + 1.2;
        });
    }

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

    private StageFrame Frame(Utterance utterance, double time, Point? pointer, double fadeFrom)
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
            if (_arranged.TryGetValue(index, out var arrangedAt))
                glow = Math.Max(glow, Motion.Hump(time, arrangedAt, 0.9)); // it was just arranged
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
        if (_phase is Phase.Thinking or Phase.Answering || (_phase is Phase.Armed or Phase.Blocked && leaving < 1 && said.Length > 0))
        {
            var size = _stage.StageSize;
            var anchor = new Point(Math.Clamp(_captionAt.X, 24, Math.Max(24, size.Width - 470)), Math.Clamp(_captionAt.Y + 58, 24, Math.Max(24, size.Height - 200)));
            answer = new AnswerView(anchor, said, Motion.EaseOutCubic(Motion.Progress(time, _thinkingFrom, 0.25)) * (1 - leaving),
                said.Length == 0, _speaking, time);
        }

        return new StageFrame(ring, trail, targets, captions, null, null, pills, answer);
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
