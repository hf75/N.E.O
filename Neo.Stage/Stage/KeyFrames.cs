using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

namespace Neo.Stage;

/// <summary>
/// Moments of the dry run as pictures (--frames &lt;folder&gt;), over a drawn desktop: to judge the look without a
/// screenshot of the real screen, which would show whatever is open there.
/// </summary>
internal static class KeyFrames
{
    public static readonly double[] Times = [0.6, 1.25, 1.75, 2.15, 2.7, 3.45, 4.0, 4.45, 5.0, 5.9, 6.7, 7.1, 7.9, 9.6];

    public static void Render(string folder, double[]? times = null)
    {
        Directory.CreateDirectory(folder);
        var size = new Size(1600, 900);
        var first = new Rect(150, 230, 560, 380);
        var second = new Rect(920, 330, 540, 380);
        var run = new DryRun(size, first, "Microsoft Word", second, "Windows-Explorer", picture: null);
        foreach (var time in times ?? Times)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height), new Vector(96, 96));
            using (var context = bitmap.CreateDrawingContext())
            {
                Desktop(context, size, first, "Unterricht Netzwerke.docx – Word", second, "Unterlagen – Explorer");
                StageRenderer.Draw(context, run.Frame(time));
            }
            using var file = File.Create(Path.Combine(folder, $"stage-{time.ToString("00.00", CultureInfo.InvariantCulture)}.png"));
            bitmap.Save(file, PngBitmapEncoderOptions.Default);
        }
    }

    // A calm wallpaper and two windows where the dry run points.
    private static void Desktop(DrawingContext context, Size size, Rect first, string firstTitle, Rect second, string secondTitle)
    {
        context.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromRgb(0x1E, 0x3A, 0x5F), 0), new GradientStop(Color.FromRgb(0x2D, 0x24, 0x4F), 0.6), new GradientStop(Color.FromRgb(0x14, 0x1A, 0x2E), 1) },
        }, new Rect(size));
        Window(context, first, firstTitle, Color.FromRgb(0x2B, 0x57, 0x9A));
        Window(context, second, secondTitle, Color.FromRgb(0xF0, 0xC0, 0x4B));
        context.FillRectangle(new ImmutableSolidColorBrush(Color.FromArgb(0xE6, 0x1B, 0x1F, 0x2A)), new Rect(0, size.Height - 48, size.Width, 48));
    }

    private static void Window(DrawingContext context, Rect bounds, string title, Color accent)
    {
        context.DrawRectangle(new ImmutableSolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)), null, new RoundedRect(bounds, 8),
            new BoxShadows(new BoxShadow { OffsetY = 10, Blur = 30, Color = Color.FromArgb(0x80, 0, 0, 0) }));
        context.FillRectangle(new ImmutableSolidColorBrush(Color.FromRgb(0xE9, 0xEC, 0xF1)), new Rect(bounds.Left, bounds.Top, bounds.Width, 34), 8);
        context.FillRectangle(new ImmutableSolidColorBrush(accent), new Rect(bounds.Left + 12, bounds.Top + 11, 12, 12), 3);
        var text = new FormattedText(title, CultureInfo.GetCultureInfo("de-DE"), FlowDirection.LeftToRight, new Typeface("fonts:Inter#Inter"), 12.5,
            new ImmutableSolidColorBrush(Color.FromRgb(0x33, 0x38, 0x44)));
        context.DrawText(text, new Point(bounds.Left + 32, bounds.Top + 17 - text.Height / 2));
        var line = new ImmutableSolidColorBrush(Color.FromRgb(0xDC, 0xE0, 0xE7));
        for (var i = 0; i < 9; i++)
            context.FillRectangle(line, new Rect(bounds.Left + 28, bounds.Top + 62 + i * 30, (bounds.Width - 56) * (i % 3 == 2 ? 0.55 : 0.9), 10), 5);
    }
}
