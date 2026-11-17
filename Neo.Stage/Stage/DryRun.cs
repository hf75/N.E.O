using Avalonia;
using Avalonia.Media;

namespace Neo.Stage;

/// <summary>
/// A scripted moment, to judge look and motion before anything listens or thinks: "Nimm das hier und leg es da hin."
/// The pointer goes to the first window and rests (das hier), moves on to the second (da), and lets go. A bow shows
/// where it goes, a picture of the first window floats over and waits for a yes, the yes makes it solid, a short
/// answer follows, everything fades.
/// The frame is a function of time alone, so a moment can be drawn again exactly.
/// </summary>
internal sealed class DryRun
{
    public const double Duration = 10.4;

    // The script, in seconds.
    private const double RingIn = 0.15, MoveToFirst = 0.35, AtFirst = 1.4, MoveToSecond = 2.05, AtSecond = 3.2;
    private const double LetGo = 4.05, ArcFrom = 4.25, GhostLift = 4.6, GhostFly = 4.8, GhostLands = 5.65, HintAt = 5.75;
    private const double YesSaid = 6.55, Solid = 6.85, AnswerAt = 7.45, Away = 9.35;

    private readonly Rect _first, _second;
    private readonly string _firstName, _secondName;
    private readonly IImage? _picture;
    private readonly Size _stage;
    private readonly Point _start, _grab, _drop;
    private readonly Rect _ghostAtFirst, _ghostAtSecond;

    private static readonly (string Text, double At, bool First, bool Second)[] s_words =
    [
        ("Nimm", 1.0, false, false),
        ("das hier", 1.3, true, false),
        ("und", 2.25, false, false),
        ("leg", 2.47, false, false),
        ("es", 2.67, false, false),
        ("da", 3.25, false, true),
        ("hin.", 3.52, false, false),
    ];

    public DryRun(Size stage, Rect first, string firstName, Rect second, string secondName, IImage? picture)
    {
        _stage = stage;
        _first = first;
        _second = second;
        _firstName = firstName;
        _secondName = secondName;
        _picture = picture;

        _grab = new Point(first.X + first.Width * 0.42, first.Y + first.Height * 0.45);
        _drop = new Point(second.X + second.Width * 0.5, second.Y + second.Height * 0.55);
        _start = Inside(new Point(_grab.X - first.Width * 0.35 - 60, _grab.Y + first.Height * 0.45 + 70), 40);

        var aspect = picture is { Size: { Width: > 0, Height: > 0 } size } ? size.Width / size.Height : first.Width / Math.Max(1, first.Height);
        var ghost = Fit(aspect, 300, 190);
        _ghostAtFirst = Centered(first.Center, ghost);
        _ghostAtSecond = Centered(second.Center, ghost);
    }

    public StageFrame Frame(double time)
    {
        var targets = new List<TargetView>
        {
            Target(_first, _firstName, Palette.That, time, AtFirst + 0.05),
            Target(_second, _secondName, Palette.There, time, AtSecond + 0.02),
        };
        var captions = new List<CaptionView>();
        if (Caption(time) is { } said)
            captions.Add(said);
        if (YesCaption(time) is { } yes)
            captions.Add(yes);

        return new StageFrame(Pointer(time), Trail(time), targets, captions, Arc(time), Ghost(time), Pills(time));
    }

    // ── The pointer ──

    private Point PointerAt(double time)
    {
        if (time < MoveToFirst)
            return _start + Jitter(time, 0.5);
        if (time < AtFirst)
            return Path(_start, _grab, Motion.EaseInOutCubic(Motion.Progress(time, MoveToFirst, AtFirst - MoveToFirst)), 0.16) + Jitter(time, 0.5);
        if (time < MoveToSecond)
            return _grab + Jitter(time, 1.3);
        if (time < AtSecond)
            return Path(_grab, _drop, Motion.EaseInOutCubic(Motion.Progress(time, MoveToSecond, AtSecond - MoveToSecond)), -0.2) + Jitter(time, 0.5);
        return _drop + Jitter(time, 1.1);
    }

    // A hand does not move in straight lines.
    private static Point Path(Point from, Point to, double progress, double bend)
    {
        var along = to - from;
        var across = new Vector(-along.Y, along.X) * bend;
        return Motion.Bezier(from, from + along * 0.3 + across, from + along * 0.7 + across, to, progress);
    }

    // A hand at rest is never quite still.
    private static Vector Jitter(double time, double amount)
        => new(amount * (0.7 * Math.Sin(time * 7.3) + 0.3 * Math.Sin(time * 13.1 + 1.7)),
               amount * (0.6 * Math.Cos(time * 6.1) + 0.4 * Math.Sin(time * 11.7 + 0.4)));

    private PointerView Pointer(double time)
    {
        var opacity = Motion.Visible(time, 0, RingIn, Away, 0.5);
        // Listening while the sentence is spoken and while the yes is; a small ring while it waits.
        var listening = Math.Max(
            Motion.Visible(time, RingIn, 0.35, LetGo, 0.3),
            Motion.Visible(time, YesSaid - 0.25, 0.2, YesSaid + 0.6, 0.3));
        var ringScale = Motion.Lerp(0.42, 1, time < RingIn + 0.4 ? Motion.EaseOutBack(Motion.Progress(time, RingIn, 0.4)) : listening);
        return new PointerView(PointerAt(time), opacity, ringScale, Math.Sin(2 * Math.PI * time / 1.6), time * 110, ShowArrow: true);
    }

    private IReadOnlyList<TrailPoint> Trail(double time)
    {
        if (time < RingIn || time > LetGo + 0.6)
            return [];
        var points = new List<TrailPoint>();
        for (var age = 0.66; age >= 0; age -= 0.02)
        {
            var at = time - age;
            if (at >= RingIn && at <= LetGo)
                points.Add(new TrailPoint(PointerAt(at), age));
        }
        return points;
    }

    // The words ride beside the pointer, a little behind it, as if on a spring.
    private Point CaptionAnchor(double time)
    {
        var sum = new Vector();
        const int samples = 10;
        for (var i = 0; i < samples; i++)
            sum += (Vector)PointerAt(time - 0.2 * i / samples);
        var follow = (Point)(sum / samples);
        return Inside(new Point(follow.X + 30, follow.Y + 30), 24, width: 360, height: 48);
    }

    // ── What is said ──

    private CaptionView? Caption(double time)
    {
        var opacity = Motion.Visible(time, 0.92, 0.2, LetGo, 0.28);
        if (opacity <= 0)
            return null;
        var words = s_words.Select(word =>
        {
            var appear = Motion.Progress(time, word.At, 0.22);
            if (word.First)
                // Once the pointer has moved on, the line to the first window steps back.
                return new WordView(word.Text, appear, Palette.That, Motion.Progress(time, 1.62, 0.25), Motion.Progress(time, 1.62, 0.3), 0,
                    Motion.EaseInOutCubic(Motion.Progress(time, 1.72, 0.4)), 1 - 0.65 * Motion.EaseInOutCubic(Motion.Progress(time, MoveToSecond + 0.15, 0.5)));
            if (word.Second)
                return new WordView(word.Text, appear, Palette.There, Motion.Progress(time, 3.3, 0.22), Motion.Progress(time, 3.3, 0.3), 1, Motion.EaseInOutCubic(Motion.Progress(time, 3.4, 0.4)));
            return new WordView(word.Text, appear);
        }).ToList();
        return new CaptionView(CaptionAnchor(time), words, opacity);
    }

    private CaptionView? YesCaption(double time)
    {
        var opacity = Motion.Visible(time, YesSaid - 0.1, 0.18, YesSaid + 1.2, 0.35);
        if (opacity <= 0)
            return null;
        var yes = new WordView("Ja.", Motion.Progress(time, YesSaid, 0.2), Palette.Yes, Motion.Progress(time, YesSaid + 0.05, 0.2), Motion.Progress(time, YesSaid + 0.05, 0.3));
        // Beside the picture it answers, not over it.
        var anchor = Inside(new Point(_ghostAtSecond.Right + 18, _ghostAtSecond.Center.Y - 22), 24, width: 110, height: 48);
        return new CaptionView(anchor, [yes], opacity);
    }

    // ── What is pointed at ──

    private static TargetView Target(Rect bounds, string name, Color color, double time, double appear)
    {
        var opacity = Motion.Visible(time, appear, 0.3, Away - 0.75, 0.45);
        var grow = Motion.EaseOutCubic(Motion.Progress(time, appear, 0.34));
        var glow = Math.Max(Motion.Hump(time, appear, 0.7), 0.15);
        var label = Motion.Visible(time, appear + 0.1, 0.25, Away - 0.75, 0.45);
        return new TargetView(bounds, name, color, opacity, Motion.Lerp(22, 6, grow), glow, label);
    }

    // ── Where it goes ──

    private (Point From, Point C1, Point C2, Point To) Bow()
    {
        var from = new Point(_first.Center.X, _first.Top - 6);
        var to = new Point(_second.Center.X, _second.Top - 6);
        var lift = Math.Clamp(Math.Abs(to.X - from.X) * 0.32, 90, 220);
        var top = Math.Max(Math.Min(from.Y, to.Y) - lift, 70);
        return (from, new Point(from.X + (to.X - from.X) * 0.15, top), new Point(from.X + (to.X - from.X) * 0.85, top), to);
    }

    private ArcView? Arc(double time)
    {
        if (time < ArcFrom)
            return null;
        var (from, c1, c2, to) = Bow();
        var opacity = Motion.Visible(time, ArcFrom, 0.15, Solid + 0.3, 0.6);
        var progress = Motion.EaseInOutCubic(Motion.Progress(time, ArcFrom, 0.5));
        double? spark = time >= ArcFrom + 0.45 && time <= GhostLands ? Motion.EaseInOutCubic(Motion.Progress(time, ArcFrom + 0.45, GhostLands - ArcFrom - 0.45)) : null;
        return new ArcView(from, c1, c2, to, progress, opacity, spark);
    }

    private GhostView? Ghost(double time)
    {
        if (time < GhostLift)
            return null;
        var fly = Motion.EaseInOutCubic(Motion.Progress(time, GhostFly, GhostLands - GhostFly));
        var (from, c1, c2, to) = Bow();
        var lift = new Vector(0, from.Y - _first.Center.Y);
        // It rises from the first window, follows the bow and settles into the second.
        var along = Motion.Bezier(_first.Center, c1 - lift * 0.4, c2 - new Vector(0, to.Y - _second.Center.Y) * 0.4, _second.Center, fly);
        var size = new Size(Motion.Lerp(_ghostAtFirst.Width, _ghostAtSecond.Width, fly), Motion.Lerp(_ghostAtFirst.Height, _ghostAtSecond.Height, fly));
        var rise = Motion.EaseOutBack(Motion.Progress(time, GhostLift, 0.3));
        var scale = Motion.Lerp(0.9, 1, rise) * (1 + 0.04 * Motion.Hump(time, GhostFly, GhostLands - GhostFly));
        var bounds = Centered(along - new Vector(0, 14 * rise * (1 - fly)), new Size(size.Width * scale, size.Height * scale));

        var opacity = Math.Min(Motion.EaseOutCubic(Motion.Progress(time, GhostLift, 0.25)), 1 - Motion.EaseInOutCubic(Motion.Progress(time, Away, 0.5)));
        var solid = Motion.EaseOutCubic(Motion.Progress(time, Solid, 0.4));
        var glow = Math.Max(Motion.Hump(time, Solid, 0.75), 0.18 * (1 - solid));
        var breath = time > GhostLands && time < Solid ? 0.5 + 0.5 * Math.Sin(2 * Math.PI * (time - GhostLands) / 1.1) : 0;
        var check = Motion.Progress(time, Solid + 0.15, 0.35);
        return new GhostView(bounds, _picture, _firstName, opacity, solid, glow, breath, check);
    }

    private IReadOnlyList<PillView> Pills(double time)
    {
        var pills = new List<PillView>();
        var (from, c1, c2, to) = Bow();
        var apex = Motion.Bezier(from, c1, c2, to, 0.5);

        var plan = Motion.Visible(time, LetGo + 0.1, 0.3, Solid + 0.2, 0.4);
        if (plan > 0)
        {
            pills.Add(new PillView(new Point(apex.X, apex.Y - 26),
                [new PillPart(_firstName, Palette.That, Dot: true, Strong: true), new PillPart("→", Palette.TextDim), new PillPart(_secondName, Palette.There, Dot: true, Strong: true)],
                plan, 8 * (1 - plan), PillAlign.Center, 14));
        }

        var below = new Point(_second.Center.X, _ghostAtSecond.Bottom + 18);
        var hint = Motion.Visible(time, HintAt, 0.3, YesSaid + 0.1, 0.25);
        if (hint > 0)
            pills.Add(new PillView(below, [new PillPart("Sag „Ja“, dann übernehme ich es.", Palette.Yes, Dot: true)], hint, 6 * (1 - hint), PillAlign.TopCenter, 14));

        var answer = Motion.Visible(time, AnswerAt, 0.3, Away, 0.5);
        if (answer > 0)
            pills.Add(new PillView(below, [new PillPart("Erledigt. Es liegt jetzt da.", Palette.Yes, Dot: true, Strong: true)], answer, 6 * (1 - answer), PillAlign.TopCenter, 15));
        return pills;
    }

    // ── Geometry ──

    private Point Inside(Point point, double margin, double width = 0, double height = 0)
        => new(Math.Clamp(point.X, margin, Math.Max(margin, _stage.Width - margin - width)),
               Math.Clamp(point.Y, margin, Math.Max(margin, _stage.Height - margin - height)));

    private static Size Fit(double aspect, double maxWidth, double maxHeight)
        => aspect >= maxWidth / maxHeight ? new Size(maxWidth, maxWidth / aspect) : new Size(maxHeight * aspect, maxHeight);

    private static Rect Centered(Point center, Size size) => new(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height);
}
