using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace Neo.Stage;

/// <summary>Something pointed at during an utterance: when, where the pointer rested, and what lay there.</summary>
internal sealed record Pointing(double Time, Point At, PointedThing Thing);

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
/// pointed at when it was said. Let go: the final words come, what was pointed at shows as a plan, everything fades.
/// A shortcut with the right Ctrl (Ctrl+C) never starts it. Only looks and listens: the mouse and the keyboard stay the
/// person's, and the microphone is open only while the key is held.
/// </summary>
internal sealed class LiveStage
{
    private const double ArmDelay = 0.2, Dwell = 0.33, DwellRadius = 7, PlanFor = 1.4, FadeOut = 0.5, PartialDelay = 0.7;
    private static readonly Color[] s_colors = [Palette.That, Palette.There, Color.FromRgb(0xF4, 0x7A, 0xC8), Palette.Yes];

    private readonly StageOverlay _stage;
    private readonly DispatcherTimer _watch;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Func<bool>? _keyHeld;
    private readonly List<double> _wordShown = [];
    private Phase _phase = Phase.Idle;
    private double _keyDown, _listening, _released, _planFrom = double.NaN, _voice;
    private Utterance? _utterance;
    private Ears? _ears;
    private bool _probing, _looping;
    private Point _probedAt = new(double.NaN, double.NaN);
    private Point _captionAt;

    private enum Phase { Idle, Armed, Blocked, Listening, Releasing }

    private readonly Func<string, Ears?> _hearing;

    /// <param name="keyHeld">Stands in for the right Ctrl key (a check without input); null watches the key.</param>
    /// <param name="hearing">Opens the ears with the API key; null listens to the microphone.</param>
    public LiveStage(StageOverlay stage, Func<bool>? keyHeld = null, Func<string, Ears?>? hearing = null)
    {
        _stage = stage;
        _keyHeld = keyHeld;
        _hearing = hearing ?? (key => Ears.Listen(key));
        _watch = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => Watch());
    }

    /// <summary>An utterance is over. Phase 2 hands it to the brain.</summary>
    public event Action<Utterance>? Spoken;

    /// <summary>Off while something else uses the stage (the dry run).</summary>
    public bool Enabled { get; set; } = true;

    public bool Busy => _phase is Phase.Listening or Phase.Releasing;

    public void Start() => _watch.Start();

    private double Now => _clock.Elapsed.TotalSeconds;

    private bool KeyHeld() => _keyHeld?.Invoke() ?? IsDown(VK_RCONTROL);

    private bool Chord() => _keyHeld == null && AnotherKeyDown();

    // Between utterances only the key is watched, about 33 times a second.
    private void Watch()
    {
        var down = KeyHeld();
        switch (_phase)
        {
            case Phase.Idle when down && Enabled:
                _phase = Chord() ? Phase.Blocked : Phase.Armed;
                _keyDown = Now;
                break;
            case Phase.Armed when !down:
                _phase = Phase.Idle;
                break;
            case Phase.Armed when Chord():
                _phase = Phase.Blocked; // a shortcut, not speech
                break;
            case Phase.Armed when Now - _keyDown >= ArmDelay:
                BeginListening();
                break;
            case Phase.Blocked when !down:
                _phase = Phase.Idle;
                break;
        }
    }

    private void BeginListening()
    {
        _phase = Phase.Listening;
        _listening = Now;
        _planFrom = double.NaN;
        _voice = 0;
        _wordShown.Clear();
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
        _stage.Display(Frame(utterance, time, pointer));

        if (_phase == Phase.Releasing && !double.IsNaN(_planFrom) && time - _planFrom > PlanFor + FadeOut + 0.1)
        {
            _phase = Phase.Idle;
            _looping = false;
            _stage.Display(StageFrame.Empty);
            _stage.Hide();
            Spoken?.Invoke(utterance);
            return;
        }
        _stage.RequestAnimationFrame(Tick);
    }

    private void Release(Utterance utterance, double time)
    {
        _phase = Phase.Releasing;
        _released = time;
        utterance.Ended = time;
        App.Log($"released after {time - utterance.Started:0.0} s, {utterance.Pointings.Count} things pointed at");
        if (_ears is not { } ears)
        {
            _planFrom = time;
            return;
        }
        _ears = null;
        _ = FinishHearingAsync(ears, utterance);
    }

    private async Task FinishHearingAsync(Ears ears, Utterance utterance)
    {
        var heard = await ears.FinishAsync(TimeSpan.FromSeconds(4));
        var problem = ears.Problem;
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
            _planFrom = Now;
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

    // UI Automation off the UI thread; the answer comes back to it.
    private void Probe(Utterance utterance, Point at)
    {
        _probing = true;
        _probedAt = at;
        var rested = Now - Dwell; // it was already there when the rest began
        var screen = new PixelPoint((int)Math.Round(at.X * _stage.Scaling) + _stage.ScreenBounds.X, (int)Math.Round(at.Y * _stage.Scaling) + _stage.ScreenBounds.Y);
        Task.Run(() => Uia.At(screen)).ContinueWith(found => Dispatcher.UIThread.Post(() =>
        {
            _probing = false;
            if (found.Result is not { } thing || utterance != _utterance || _phase != Phase.Listening)
                return;
            if (utterance.Pointings.Count > 0 && utterance.Pointings[^1].Thing.SameAs(thing))
                return;
            utterance.Pointings.Add(new Pointing(rested, at, thing));
            App.Log($"pointed at: {thing.Kind} '{thing.Name}' in {thing.Program}, {thing.Bounds}");
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

    private StageFrame Frame(Utterance utterance, double time, Point? pointer)
    {
        var leaving = _phase == Phase.Releasing && !double.IsNaN(_planFrom) ? Motion.EaseInOutCubic(Motion.Progress(time, _planFrom + PlanFor, FadeOut)) : 0;

        PointerView? ring = null;
        if (pointer is { } at)
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
            var opacity = Motion.EaseOutCubic(Motion.Progress(time, appear, 0.3)) * (1 - leaving);
            var label = Motion.EaseOutCubic(Motion.Progress(time, appear + 0.1, 0.25)) * (1 - leaving);
            var thing = pointing.Thing;
            var name = thing.Name.Length > 0 && thing.Kind.Length > 0 ? $"{thing.Name} · {thing.Kind}" : thing.Label;
            return new TargetView(_stage.FromScreen(thing.Bounds), name, s_colors[index % s_colors.Length], opacity, Motion.Lerp(22, 6, grow),
                Math.Max(Motion.Hump(time, appear, 0.7), 0.15), label);
        }).ToList();

        var captions = new List<CaptionView>();
        var words = Words(utterance);
        if (words.Count > 0)
        {
            if (_phase == Phase.Listening && utterance.Trail.Count > 0)
                _captionAt = CaptionAnchor(utterance, time);
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
            captions.Add(new CaptionView(_captionAt, views, Motion.EaseOutCubic(Motion.Progress(time, first, 0.2)) * (1 - leaving)));
        }

        var pills = new List<PillView>();
        if (_phase == Phase.Releasing && !double.IsNaN(_planFrom) && utterance.Pointings.Count > 0)
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

        return new StageFrame(ring, trail, targets, captions, null, null, pills);
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
        if (!GetCursorPos(out var cursor))
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
