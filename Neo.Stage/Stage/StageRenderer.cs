using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Neo.Stage;

/// <summary>
/// Draws a <see cref="StageFrame"/>. Light on glass: outlines and lines glow in the colour of what they stand for,
/// words sit on dark glass so they read on any desktop, and nothing covers what it points at.
/// </summary>
internal static class StageRenderer
{
    private const double CaptionHeight = 44, CaptionFont = 17, WordGap = 7, CaptionPadding = 16;
    private static readonly CultureInfo s_culture = CultureInfo.GetCultureInfo("de-DE");
    private static readonly IImmutableBrush s_ink = new ImmutableSolidColorBrush(Palette.Ink);
    private static readonly ImmutablePen s_inkEdge = new(new ImmutableSolidColorBrush(Palette.InkEdge), 1);
    private static readonly BoxShadows s_pillShadow = new(new BoxShadow { OffsetY = 8, Blur = 26, Color = Color.FromArgb(0x70, 0, 0, 0) });

    public static void Draw(DrawingContext context, StageFrame frame)
    {
        foreach (var target in frame.Targets)
            DrawTarget(context, target);
        if (frame.Arc is { } arc)
            DrawArc(context, arc);
        if (frame.Ghost is { } ghost)
            DrawGhost(context, ghost);
        DrawTrail(context, frame.Trail);
        if (frame.Pointer is { } pointer)
            DrawPointer(context, pointer);
        foreach (var caption in frame.Captions)
            DrawCaption(context, caption, frame.Targets);
        foreach (var target in frame.Targets.Where(target => target.LabelOpacity > 0.001))
            DrawLabel(context, target);
        foreach (var pill in frame.Pills)
            DrawPill(context, pill);
        if (frame.Answer is { } answer)
            DrawAnswer(context, answer);
    }

    // ── The answer ──

    private const double AnswerWidth = 440, AnswerWideWidth = 600, AnswerPadding = 18;

    private static void DrawAnswer(DrawingContext context, AnswerView answer)
    {
        if (answer.Opacity <= 0.001)
            return;
        var text = answer.Text.Length > 0 && !answer.Thinking
            ? new FormattedText(answer.Text, s_culture, FlowDirection.LeftToRight, Palette.Regular, 16.5, new ImmutableSolidColorBrush(Palette.Text))
            {
                MaxTextWidth = (answer.Above ? AnswerWideWidth : AnswerWidth) - 2 * AnswerPadding - 22,
                LineHeight = 24,
            }
            : null;
        var width = text != null ? Math.Min(answer.Above ? AnswerWideWidth : AnswerWidth, text.WidthIncludingTrailingWhitespace + 2 * AnswerPadding + 22) : 92;
        var height = text != null ? text.Height + 2 * AnswerPadding - 4 : 46;
        width = Math.Max(width, 92);
        var box = answer.Above
            ? new Rect(answer.Anchor.X - width / 2, answer.Anchor.Y - height, width, height)
            : new Rect(answer.Anchor.X, answer.Anchor.Y, width, height);

        using (context.PushOpacity(answer.Opacity))
        {
            var glow = new BoxShadows(new BoxShadow { OffsetY = 10, Blur = 30, Color = Color.FromArgb(0x80, 0, 0, 0) },
                [new BoxShadow { Blur = 18 + 14 * answer.Speaking, Color = Palette.Listen.WithAlpha(0.18 + 0.32 * answer.Speaking) }]);
            context.DrawRectangle(s_ink, s_inkEdge, new RoundedRect(box, 18), glow);
            // The stage's own mark: a small ring at the start, lit while it speaks.
            var mark = new Point(box.Left + AnswerPadding + 4, box.Top + (text != null ? AnswerPadding + 9 : box.Height / 2));
            context.DrawEllipse(new ImmutableSolidColorBrush(Palette.Listen.WithAlpha(0.25 + 0.45 * answer.Speaking)), null, mark, 8, 8);
            context.DrawEllipse(null, new ImmutablePen(new ImmutableSolidColorBrush(Palette.Listen), 2), mark, 5.5 + 1.5 * answer.Speaking, 5.5 + 1.5 * answer.Speaking);

            if (text != null)
                context.DrawText(text, new Point(box.Left + AnswerPadding + 22, box.Top + AnswerPadding - 2));
            else
            {
                // Thinking: three dots that rise in turn.
                for (var i = 0; i < 3; i++)
                {
                    var phase = Math.Sin(2 * Math.PI * (answer.Time * 1.4 - i * 0.18));
                    var dot = new Point(box.Left + 44 + i * 13, box.Center.Y - 2.5 * Math.Max(0, phase));
                    context.DrawEllipse(new ImmutableSolidColorBrush(Palette.Text.WithAlpha(0.45 + 0.5 * Math.Max(0, phase))), null, dot, 3.2, 3.2);
                }
            }
        }
    }

    // ── What is pointed at ──

    private static void DrawTarget(DrawingContext context, TargetView target)
    {
        if (target.Opacity <= 0.001)
            return;
        var outline = new RoundedRect(target.Bounds.Inflate(target.Inflate), 14);
        var glow = new BoxShadows(new BoxShadow
        {
            Blur = 20 + 34 * target.Glow,
            Spread = 1 + 3 * target.Glow,
            Color = target.Color.WithAlpha(0.42 + 0.38 * target.Glow),
        });
        using (context.PushOpacity(target.Opacity))
        {
            context.DrawRectangle(new ImmutableSolidColorBrush(target.Color.WithAlpha(0.06 + 0.08 * target.Glow)),
                new ImmutablePen(new ImmutableSolidColorBrush(Palette.Mix(target.Color, Colors.White, 0.15 * target.Glow)), 2.2), outline, glow);
            DrawCorners(context, outline.Rect, target.Color);
        }
    }

    // Bright corners make the outline read as "this one" even over a busy window.
    private static void DrawCorners(DrawingContext context, Rect rect, Color color)
    {
        var pen = new ImmutablePen(new ImmutableSolidColorBrush(Palette.Light(color)), 3.4, lineCap: PenLineCap.Round);
        const double arm = 16, inset = 5;
        var (l, t, r, b) = (rect.Left + inset, rect.Top + inset, rect.Right - inset, rect.Bottom - inset);
        context.DrawLine(pen, new Point(l, t + arm), new Point(l, t)); context.DrawLine(pen, new Point(l, t), new Point(l + arm, t));
        context.DrawLine(pen, new Point(r - arm, t), new Point(r, t)); context.DrawLine(pen, new Point(r, t), new Point(r, t + arm));
        context.DrawLine(pen, new Point(l, b - arm), new Point(l, b)); context.DrawLine(pen, new Point(l, b), new Point(l + arm, b));
        context.DrawLine(pen, new Point(r - arm, b), new Point(r, b)); context.DrawLine(pen, new Point(r, b), new Point(r, b - arm));
    }

    private static void DrawLabel(DrawingContext context, TargetView target)
    {
        var outline = target.Bounds.Inflate(target.Inflate);
        DrawPill(context, new PillView(new Point(outline.Left, outline.Top - 10), [new PillPart(target.Label, target.Color, Dot: true, Strong: true)],
            target.LabelOpacity * target.Opacity, (1 - target.LabelOpacity) * 6, PillAlign.BottomLeft, 13));
    }

    // ── The pointer, its ring and its trail ──

    private static void DrawPointer(DrawingContext context, PointerView pointer)
    {
        if (pointer.Opacity <= 0.001)
            return;
        var center = pointer.Position;
        var radius = 23 * pointer.RingScale * (1 + 0.055 * pointer.Breath);
        var strength = Math.Max(pointer.RingScale, 0.6);
        using (context.PushOpacity(pointer.Opacity))
        {
            var halo = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Palette.Listen.WithAlpha(0.46 * strength), 0),
                    new GradientStop(Palette.Listen.WithAlpha(0.2 * strength), 0.5),
                    new GradientStop(Palette.Listen.WithAlpha(0), 1),
                },
            };
            context.DrawEllipse(halo, null, center, radius * 2.4, radius * 2.4);

            if (pointer.RingScale > 0.02)
            {
                // A dark rim under the light keeps the ring visible on white windows.
                context.DrawEllipse(null, new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x50, 0x0D, 0x11, 0x1F)), 6), center, radius, radius);
                var sweep = new ConicGradientBrush
                {
                    Angle = pointer.Spin,
                    GradientStops =
                    {
                        new GradientStop(Palette.Listen.WithAlpha(0.25), 0),
                        new GradientStop(Colors.White.WithAlpha(0.95), 0.22),
                        new GradientStop(Palette.Listen.WithAlpha(0.55), 0.5),
                        new GradientStop(Colors.White.WithAlpha(0.85), 0.72),
                        new GradientStop(Palette.Listen.WithAlpha(0.25), 1),
                    },
                };
                context.DrawEllipse(null, new Pen(sweep, 3), center, radius, radius);
            }

            if (pointer.ShowArrow)
                DrawArrow(context, center);
            else
                context.DrawEllipse(Brushes.White, null, center, 2.6, 2.6);
        }
    }

    // A stand-in for the mouse pointer in a dry run, which never moves the real one.
    private static readonly Geometry s_arrow = Arrow();

    private static Geometry Arrow()
    {
        var geometry = new StreamGeometry();
        using var shape = geometry.Open();
        shape.BeginFigure(new Point(0, 0), true);
        foreach (var point in new[] { new Point(0, 17.5), new Point(4.3, 13.4), new Point(7.3, 19.9), new Point(10.2, 18.6), new Point(7.3, 12.3), new Point(12.6, 12.3) })
            shape.LineTo(point);
        shape.EndFigure(true);
        return geometry;
    }

    private static readonly ImmutablePen s_arrowEdge = new(new ImmutableSolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x22)), 1.2, lineJoin: PenLineJoin.Round);

    private static void DrawArrow(DrawingContext context, Point tip)
    {
        using (context.PushTransform(Matrix.CreateTranslation(tip.X, tip.Y)))
            context.DrawGeometry(Brushes.White, s_arrowEdge, s_arrow);
    }

    // A comet's tail: one tapering ribbon, filled from transparent at the old end to light at the pointer.
    private static void DrawTrail(DrawingContext context, IReadOnlyList<TrailPoint> trail)
    {
        const double life = 0.65;
        var points = new List<(Point Position, double Left)>();
        foreach (var point in trail)
        {
            var left = 1 - Motion.Clamp01(point.Age / life);
            if (left <= 0)
                continue;
            if (points.Count > 0 && Point.Distance(point.Position, points[^1].Position) < 0.8)
                points[^1] = (point.Position, left);
            else
                points.Add((point.Position, left));
        }
        if (points.Count < 2)
            return;
        DrawRibbon(context, points, 16, Palette.Listen.WithAlpha(0), Palette.Listen.WithAlpha(0.26));
        DrawRibbon(context, points, 5, Palette.Listen.WithAlpha(0), Colors.White.WithAlpha(0.92));
    }

    private static void DrawRibbon(DrawingContext context, List<(Point Position, double Left)> points, double width, Color tail, Color head)
    {
        var left = new List<Point>(points.Count);
        var right = new List<Point>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            Vector direction = points[Math.Min(points.Count - 1, i + 1)].Position - points[Math.Max(0, i - 1)].Position;
            var length = Math.Max(direction.Length, 0.001);
            var normal = new Vector(-direction.Y / length, direction.X / length);
            var half = width * (0.06 + 0.94 * points[i].Left) / 2;
            left.Add(points[i].Position + normal * half);
            right.Add(points[i].Position - normal * half);
        }
        var ribbon = new StreamGeometry();
        using (var shape = ribbon.Open())
        {
            shape.BeginFigure(left[0], true);
            foreach (var point in left.Skip(1))
                shape.LineTo(point);
            for (var i = right.Count - 1; i >= 0; i--)
                shape.LineTo(right[i]);
            shape.EndFigure(true);
        }
        context.DrawGeometry(Gradient(points[0].Position, points[^1].Position, tail, head), null, ribbon);
    }

    /// <summary>A gradient in the stage's own coordinates (absolute points stay where they are, whatever is drawn).</summary>
    private static LinearGradientBrush Gradient(Point start, Point end, Color from, Color to) => new()
    {
        StartPoint = new RelativePoint(start, RelativeUnit.Absolute),
        EndPoint = new RelativePoint(start == end ? end + new Vector(1, 0) : end, RelativeUnit.Absolute),
        GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
    };

    // ── Words, chips and the lines to what they point at ──

    private static void DrawCaption(DrawingContext context, CaptionView caption, IReadOnlyList<TargetView> targets)
    {
        if (caption.Opacity <= 0.001)
            return;

        var items = new List<(WordView Word, FormattedText Text, double X, double Width, double Pad)>();
        var x = CaptionPadding;
        foreach (var word in caption.Words.Where(word => word.Appear > 0))
        {
            var text = new FormattedText(word.Text, s_culture, FlowDirection.LeftToRight, word.Chip != null ? Palette.Strong : Palette.Regular, CaptionFont, Brushes.White);
            var pad = word.Chip != null ? 9 * Motion.EaseOutCubic(word.ChipProgress) : 0;
            var width = text.Width + 2 * pad;
            items.Add((word, text, x, width, pad));
            x += (width + WordGap) * Motion.EaseOutCubic(Math.Min(1, word.Appear * 1.4));
        }
        if (items.Count == 0)
            return;

        var pillWidth = Math.Max(x - WordGap + CaptionPadding, 2 * CaptionPadding);
        var pill = caption.Centered
            ? new Rect(caption.Anchor.X - pillWidth / 2, caption.Anchor.Y - CaptionHeight, pillWidth, CaptionHeight)
            : new Rect(caption.Anchor.X, caption.Anchor.Y, pillWidth, CaptionHeight);

        // The lines first, so the glass lies over their start.
        foreach (var item in items.Where(item => item.Word is { Chip: not null, Target: not null, Link: > 0 }))
        {
            if (item.Word.Target!.Value >= targets.Count)
                continue;
            var target = targets[item.Word.Target.Value];
            var chipCenter = new Point(pill.X + item.X + item.Width / 2, pill.Center.Y);
            DrawConnector(context, chipCenter, pill, target, item.Word.Chip!.Value, item.Word.Link, caption.Opacity * target.Opacity * item.Word.LinkOpacity);
        }

        using (context.PushOpacity(caption.Opacity))
        {
            context.DrawRectangle(s_ink, s_inkEdge, new RoundedRect(pill, CaptionHeight / 2), s_pillShadow);
            // A word slides in as the glass widens; it never stands out of it.
            using var inside = context.PushClip(new RoundedRect(pill.Inflate(new Thickness(0, 4)), CaptionHeight / 2));
            foreach (var (word, text, itemX, width, pad) in items)
            {
                var appear = Motion.EaseOutCubic(word.Appear);
                var left = pill.X + itemX;
                using (context.PushOpacity(appear))
                using (context.PushTransform(Matrix.CreateTranslation(0, 7 * (1 - appear))))
                {
                    var color = Palette.Text;
                    if (word.Chip is { } chip)
                    {
                        var chipness = Motion.EaseOutCubic(word.ChipProgress);
                        var box = new Rect(left, pill.Y + 7, width, CaptionHeight - 14);
                        var pop = 1 + 0.07 * Math.Sin(Math.PI * Motion.Clamp01(word.Pop));
                        using (context.PushTransform(Matrix.CreateTranslation(-box.Center.X, -box.Center.Y) * Matrix.CreateScale(pop, pop) * Matrix.CreateTranslation(box.Center.X, box.Center.Y)))
                        {
                            context.DrawRectangle(new ImmutableSolidColorBrush(chip.WithAlpha(0.24 * chipness)),
                                new ImmutablePen(new ImmutableSolidColorBrush(chip.WithAlpha(0.75 * chipness)), 1.2),
                                new RoundedRect(box, 9),
                                new BoxShadows(new BoxShadow { Blur = 16, Color = chip.WithAlpha(0.45 * chipness) }));
                            color = Palette.Mix(Palette.Text, Palette.Light(chip), chipness);
                            text.SetForegroundBrush(new ImmutableSolidColorBrush(color));
                            context.DrawText(text, new Point(left + pad, pill.Y + (CaptionHeight - text.Height) / 2));
                        }
                    }
                    else
                    {
                        text.SetForegroundBrush(new ImmutableSolidColorBrush(color));
                        context.DrawText(text, new Point(left, pill.Y + (CaptionHeight - text.Height) / 2));
                    }
                }
            }
        }
    }

    // From the chip to the nearest point of the outline: the shortest way to its edge, also when the words lie over it.
    private static void DrawConnector(DrawingContext context, Point chip, Rect pill, TargetView target, Color color, double progress, double opacity)
    {
        if (opacity <= 0.001)
            return;
        var outline = target.Bounds.Inflate(target.Inflate);
        var end = outline.Contains(chip)
            ? new[] { new Point(chip.X, outline.Top), new Point(chip.X, outline.Bottom), new Point(outline.Left, chip.Y), new Point(outline.Right, chip.Y) }
                .MinBy(point => Point.Distance(point, chip))
            : new Point(Math.Clamp(chip.X, outline.Left + 14, outline.Right - 14), Math.Clamp(chip.Y, outline.Top + 14, outline.Bottom - 14));
        if (!outline.Contains(chip) && end.Y > outline.Top + 14 && end.Y < outline.Bottom - 14)
            end = new Point(chip.X < outline.Left ? outline.Left : outline.Right, end.Y); // beside it: to the side edge
        else if (!outline.Contains(chip))
            end = new Point(end.X, chip.Y < outline.Top ? outline.Top : outline.Bottom);

        var down = end.Y >= chip.Y;
        var start = new Point(chip.X, down ? pill.Bottom - 2 : pill.Top + 2);
        if (Point.Distance(end, start) < 14)
            return;
        var vertical = Math.Abs(end.Y - start.Y) >= Math.Abs(end.X - start.X) * 0.6;
        var c1 = new Point(start.X, start.Y + (end.Y - start.Y) * 0.5);
        var c2 = vertical ? new Point(end.X, end.Y - (end.Y - start.Y) * 0.35) : new Point(end.X - (end.X - start.X) * 0.45, end.Y);
        DrawCurve(context, start, c1, c2, end, color, target.Color, progress, opacity, core: 1.7, glow: 8);
        if (progress >= 0.999)
        {
            context.DrawEllipse(new ImmutableSolidColorBrush(target.Color.WithAlpha(0.35 * opacity)), null, end, 7, 7);
            context.DrawEllipse(new ImmutableSolidColorBrush(Palette.Light(target.Color).WithAlpha(opacity)), null, end, 3.4, 3.4);
        }
    }

    /// <summary>A cubic curve drawn up to <paramref name="progress"/>: one stroke, its colour running from one end to the other, on a soft glow.</summary>
    private static void DrawCurve(DrawingContext context, Point p0, Point p1, Point p2, Point p3, Color from, Color to, double progress, double opacity,
        double core, double glow)
    {
        progress = Motion.Clamp01(progress);
        if (progress <= 0 || opacity <= 0.001)
            return;
        const int steps = 72;
        var curve = new StreamGeometry();
        using (var shape = curve.Open())
        {
            shape.BeginFigure(p0, false);
            var count = Math.Max(1, (int)Math.Ceiling(steps * progress));
            for (var i = 1; i <= count; i++)
                shape.LineTo(Motion.Bezier(p0, p1, p2, p3, Math.Min(progress, i / (double)steps)));
            shape.EndFigure(false);
        }
        Pen Stroke(Color a, Color b, double thickness) => new(Gradient(p0, p3, a, b), thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        context.DrawGeometry(null, Stroke(from.WithAlpha(0.08 * opacity), to.WithAlpha(0.08 * opacity), glow * 1.8), curve);
        context.DrawGeometry(null, Stroke(from.WithAlpha(0.18 * opacity), to.WithAlpha(0.18 * opacity), glow), curve);
        context.DrawGeometry(null, Stroke(Palette.Light(from).WithAlpha(0.96 * opacity), Palette.Light(to).WithAlpha(0.96 * opacity), core), curve);
    }

    // ── From there to there ──

    private static void DrawArc(DrawingContext context, ArcView arc)
    {
        if (arc.Opacity <= 0.001)
            return;
        DrawCurve(context, arc.From, arc.Control1, arc.Control2, arc.To, Palette.That, Palette.There, arc.Progress, arc.Opacity, core: 2.6, glow: 14);
        if (arc.Spark is { } spark)
        {
            var at = Motion.Bezier(arc.From, arc.Control1, arc.Control2, arc.To, spark);
            var color = Palette.Mix(Palette.That, Palette.There, spark);
            var light = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Colors.White.WithAlpha(0.95 * arc.Opacity), 0),
                    new GradientStop(color.WithAlpha(0.55 * arc.Opacity), 0.35),
                    new GradientStop(color.WithAlpha(0), 1),
                },
            };
            context.DrawEllipse(light, null, at, 22, 22);
        }
    }

    private static readonly IImmutableBrush s_card = new ImmutableSolidColorBrush(Color.FromArgb(0xF0, 0x16, 0x1C, 0x2E));

    private static void DrawGhost(DrawingContext context, GhostView ghost)
    {
        if (ghost.Opacity <= 0.001)
            return;
        var card = new RoundedRect(ghost.Bounds, 14);
        var glow = new BoxShadows(new BoxShadow
        {
            OffsetY = 10 * (1 - ghost.Solid),
            Blur = 24 + 46 * ghost.Glow,
            Spread = 2 * ghost.Glow,
            Color = Palette.There.WithAlpha(0.32 + 0.45 * ghost.Glow),
        });
        using (context.PushOpacity(ghost.Opacity * Motion.Lerp(0.58, 1, ghost.Solid)))
        {
            context.DrawRectangle(s_card, null, card, glow);
            using (context.PushClip(card))
            {
                if (ghost.Picture is { } picture)
                    context.DrawImage(picture, new Rect(picture.Size), ghost.Bounds);
                else
                    DrawPictureStandIn(context, ghost.Bounds);

                var strip = new Rect(ghost.Bounds.Left, ghost.Bounds.Bottom - 30, ghost.Bounds.Width, 30);
                context.FillRectangle(new ImmutableSolidColorBrush(Color.FromArgb(0xC8, 0x0D, 0x11, 0x1F)), strip);
                var label = new FormattedText(ghost.Label, s_culture, FlowDirection.LeftToRight, Palette.Strong, 12.5, new ImmutableSolidColorBrush(Palette.Text));
                context.DrawText(label, new Point(strip.Left + 12, strip.Top + (strip.Height - label.Height) / 2));
            }
            var border = Palette.Mix(Palette.There, Palette.Yes, ghost.Solid);
            context.DrawRectangle(null, new ImmutablePen(new ImmutableSolidColorBrush(border.WithAlpha(0.55 + 0.45 * Math.Max(ghost.Breath, ghost.Solid))), 2), card);
        }

        if (ghost.Check > 0.001)
        {
            var size = 15 * Motion.EaseOutBack(ghost.Check);
            var center = new Point(ghost.Bounds.Right - 4, ghost.Bounds.Top + 4);
            context.DrawEllipse(new ImmutableSolidColorBrush(Palette.Yes), new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0x0D, 0x11, 0x1F)), 2.5), center, size, size);
            var tick = new StreamGeometry();
            using (var shape = tick.Open())
            {
                shape.BeginFigure(new Point(center.X - size * 0.42, center.Y + size * 0.02), false);
                shape.LineTo(new Point(center.X - size * 0.1, center.Y + size * 0.34));
                shape.LineTo(new Point(center.X + size * 0.45, center.Y - size * 0.3));
                shape.EndFigure(false);
            }
            context.DrawGeometry(null, new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x1F)), 2.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), tick);
        }
    }

    // Without a picture of the window, a calm card that still reads as a window.
    private static void DrawPictureStandIn(DrawingContext context, Rect bounds)
    {
        context.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromRgb(0x23, 0x2B, 0x45), 0), new GradientStop(Color.FromRgb(0x1A, 0x21, 0x36), 1) },
        }, bounds);
        var line = new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        context.FillRectangle(new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), new Rect(bounds.Left, bounds.Top, bounds.Width, 22));
        for (var i = 0; i < 4; i++)
            context.FillRectangle(line, new Rect(bounds.Left + 16, bounds.Top + 40 + i * 18, (bounds.Width - 32) * (i % 2 == 0 ? 0.8 : 0.55), 7), 3.5f);
    }

    // ── Pills ──

    private static void DrawPill(DrawingContext context, PillView pill)
    {
        if (pill.Opacity <= 0.001)
            return;
        var texts = pill.Parts.Select(part => new FormattedText(part.Text, s_culture, FlowDirection.LeftToRight,
            part.Strong ? Palette.Strong : Palette.Regular, pill.FontSize, new ImmutableSolidColorBrush(part.Dot ? Palette.Text : Palette.Mix(Palette.Text, part.Color, 0.15)))).ToList();
        const double padding = 12, dot = 8, dotGap = 7, partGap = 8;
        var width = padding * 2 + texts.Sum(text => text.Width) + pill.Parts.Count(part => part.Dot) * (dot + dotGap) + partGap * (pill.Parts.Count - 1);
        var height = texts.Max(text => text.Height) + 14;
        var origin = pill.Align switch
        {
            PillAlign.TopLeft => pill.Anchor,
            PillAlign.BottomLeft => new Point(pill.Anchor.X, pill.Anchor.Y - height),
            PillAlign.TopCenter => new Point(pill.Anchor.X - width / 2, pill.Anchor.Y),
            PillAlign.Left => new Point(pill.Anchor.X, pill.Anchor.Y - height / 2),
            _ => new Point(pill.Anchor.X - width / 2, pill.Anchor.Y - height / 2),
        };
        var box = new Rect(origin.X, origin.Y + pill.Rise, width, height);

        using (context.PushOpacity(pill.Opacity))
        {
            context.DrawRectangle(s_ink, s_inkEdge, new RoundedRect(box, height / 2), s_pillShadow);
            var x = box.Left + padding;
            for (var i = 0; i < texts.Count; i++)
            {
                var part = pill.Parts[i];
                if (part.Dot)
                {
                    var center = new Point(x + dot / 2, box.Center.Y);
                    context.DrawEllipse(new ImmutableSolidColorBrush(part.Color.WithAlpha(0.35)), null, center, dot, dot);
                    context.DrawEllipse(new ImmutableSolidColorBrush(part.Color), null, center, dot / 2, dot / 2);
                    x += dot + dotGap;
                }
                context.DrawText(texts[i], new Point(x, box.Center.Y - texts[i].Height / 2));
                x += texts[i].Width + partGap;
            }
        }
    }
}
