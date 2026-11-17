using Avalonia;
using Avalonia.Media;

namespace Neo.Stage;

/// <summary>Everything on the stage at one moment. Coordinates in device-independent pixels of the stage window.</summary>
internal sealed record StageFrame(
    PointerView? Pointer,
    IReadOnlyList<TrailPoint> Trail,
    IReadOnlyList<TargetView> Targets,
    IReadOnlyList<CaptionView> Captions,
    ArcView? Arc,
    GhostView? Ghost,
    IReadOnlyList<PillView> Pills,
    AnswerView? Answer = null)
{
    public static readonly StageFrame Empty = new(null, [], [], [], null, null, []);
}

/// <summary>
/// The stage's answer: a glass card with the text as it comes, near what was talked about. While it thinks, three dots;
/// while it speaks, a soft light at its edge. <see cref="Anchor"/> is its top left corner, or with <see cref="Above"/> the
/// middle of its lower edge, so that it grows upwards.
/// </summary>
internal sealed record AnswerView(Point Anchor, string Text, double Opacity, bool Thinking, double Speaking, double Time, bool Above = false);

/// <summary>Where the pointer is. <see cref="RingScale"/> 1 is listening, smaller is waiting.</summary>
internal sealed record PointerView(Point Position, double Opacity, double RingScale, double Breath, double Spin, bool ShowArrow);

/// <summary>A point of the light trail and how long ago the pointer was there, in seconds.</summary>
internal readonly record struct TrailPoint(Point Position, double Age);

/// <summary>Something pointed at: its outline, its name and its colour.</summary>
internal sealed record TargetView(Rect Bounds, string Label, Color Color, double Opacity, double Inflate, double Glow, double LabelOpacity);

/// <summary>A spoken word. A word that points (das, da) becomes a chip in the colour of what it points at, tied to it by a line.</summary>
internal sealed record WordView(string Text, double Appear, Color? Chip = null, double ChipProgress = 0, double Pop = 0, int? Target = null, double Link = 0,
    double LinkOpacity = 1);

/// <summary>
/// The words as they are spoken. <see cref="Anchor"/> is the top left corner (beside the pointer), or with
/// <see cref="Centered"/> the middle of the lower edge, so that it widens to both sides.
/// </summary>
internal sealed record CaptionView(Point Anchor, IReadOnlyList<WordView> Words, double Opacity, bool Centered = false);

/// <summary>The bow from what was meant to where it goes; <see cref="Spark"/> is a light running along it.</summary>
internal sealed record ArcView(Point From, Point Control1, Point Control2, Point To, double Progress, double Opacity, double? Spark);

/// <summary>What will appear, as a translucent picture until the person says yes.</summary>
internal sealed record GhostView(Rect Bounds, IImage? Picture, string Label, double Opacity, double Solid, double Glow, double Breath, double Check);

/// <summary>A piece of a pill: text, or a coloured dot before it.</summary>
internal sealed record PillPart(string Text, Color Color, bool Dot = false, bool Strong = false);

internal enum PillAlign { Center, TopLeft, BottomLeft, TopCenter, Left }

/// <summary>A small glass label.</summary>
internal sealed record PillView(Point Anchor, IReadOnlyList<PillPart> Parts, double Opacity, double Rise = 0, PillAlign Align = PillAlign.Center, double FontSize = 14);
