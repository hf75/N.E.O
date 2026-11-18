using Avalonia;
using Avalonia.Media;

namespace Neo.Stage;

/// <summary>The stage's colours and type. One colour per thing pointed at: amber for "das", cyan for "da".</summary>
internal static class Palette
{
    /// <summary>Glass of the pills: deep ink, mostly opaque so words stay readable on any desktop.</summary>
    public static readonly Color Ink = Color.FromArgb(0xE0, 0x0D, 0x11, 0x1F);
    public static readonly Color InkEdge = Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF);
    public static readonly Color Text = Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF);
    public static readonly Color TextDim = Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF);

    /// <summary>The ring while the stage listens: a soft lilac light.</summary>
    public static readonly Color Listen = Color.FromRgb(0xC4, 0xB8, 0xFF);

    /// <summary>What is meant by "das": warm amber.</summary>
    public static readonly Color That = Color.FromRgb(0xFF, 0xB4, 0x47);

    /// <summary>Where it goes, "da": cyan.</summary>
    public static readonly Color There = Color.FromRgb(0x4C, 0xD6, 0xFF);

    /// <summary>A yes: green.</summary>
    public static readonly Color Yes = Color.FromRgb(0x5E, 0xE6, 0x9B);

    /// <summary>The stage watches and notes ("Schau mir zu"): a soft red, like a recording light.</summary>
    public static readonly Color Record = Color.FromRgb(0xFF, 0x6E, 0x6E);

    private static readonly FontFamily s_inter = new("fonts:Inter#Inter");
    public static readonly Typeface Regular = new(s_inter, FontStyle.Normal, FontWeight.Medium);
    public static readonly Typeface Strong = new(s_inter, FontStyle.Normal, FontWeight.SemiBold);

    public static Color WithAlpha(this Color color, double alpha)
        => Color.FromArgb((byte)Math.Clamp(Math.Round(alpha * 255), 0, 255), color.R, color.G, color.B);

    public static Color Mix(Color a, Color b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte Channel(byte x, byte y) => (byte)Math.Round(x + (y - x) * amount);
        return Color.FromArgb(Channel(a.A, b.A), Channel(a.R, b.R), Channel(a.G, b.G), Channel(a.B, b.B));
    }

    /// <summary>The colour made lighter, for text on a tinted chip.</summary>
    public static Color Light(Color color) => Mix(color, Colors.White, 0.45);
}

/// <summary>Timing curves. Everything on the stage is a function of time, so a moment can be drawn again exactly.</summary>
internal static class Motion
{
    public static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    /// <summary>0 before <paramref name="start"/>, 1 after <paramref name="start"/> + <paramref name="duration"/>, linear between.</summary>
    public static double Progress(double time, double start, double duration)
        => duration <= 0 ? (time >= start ? 1 : 0) : Clamp01((time - start) / duration);

    public static double EaseOutCubic(double x) => 1 - Math.Pow(1 - Clamp01(x), 3);

    public static double EaseInOutCubic(double x)
    {
        x = Clamp01(x);
        return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
    }

    /// <summary>Overshoots a little before it settles: things that arrive.</summary>
    public static double EaseOutBack(double x, double overshoot = 1.5)
    {
        x = Clamp01(x);
        var c3 = overshoot + 1;
        return 1 + c3 * Math.Pow(x - 1, 3) + overshoot * Math.Pow(x - 1, 2);
    }

    /// <summary>Rises from 0 to 1 and falls back: a pulse between <paramref name="start"/> and its end.</summary>
    public static double Hump(double time, double start, double duration)
    {
        var x = Progress(time, start, duration);
        return x is <= 0 or >= 1 ? 0 : Math.Sin(Math.PI * x);
    }

    /// <summary>Fades in from <paramref name="appear"/>, stays, fades out from <paramref name="leave"/>.</summary>
    public static double Visible(double time, double appear, double fadeIn, double leave, double fadeOut)
        => Math.Min(EaseOutCubic(Progress(time, appear, fadeIn)), 1 - EaseInOutCubic(Progress(time, leave, fadeOut)));

    public static double Lerp(double a, double b, double x) => a + (b - a) * x;

    public static Point Lerp(Point a, Point b, double x) => new(Lerp(a.X, b.X, x), Lerp(a.Y, b.Y, x));

    public static Point Bezier(Point p0, Point p1, Point p2, Point p3, double x)
    {
        var u = 1 - x;
        return new Point(
            u * u * u * p0.X + 3 * u * u * x * p1.X + 3 * u * x * x * p2.X + x * x * x * p3.X,
            u * u * u * p0.Y + 3 * u * u * x * p1.Y + 3 * u * x * x * p2.Y + x * x * x * p3.Y);
    }
}
