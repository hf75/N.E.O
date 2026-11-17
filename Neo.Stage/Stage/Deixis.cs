namespace Neo.Stage;

/// <summary>
/// A spoken word, when it was said (stage clock, seconds), whether it points ("das hier", "da", "dahin"), and the index of
/// its first raw word (two raw words make one pointing word).
/// </summary>
internal sealed record SpokenWord(string Text, double Time, bool Points, int First);

/// <summary>
/// Which words of a German sentence point at something, and what each of them meant: the thing the pointer rested
/// on around the moment the word was said. People often point a little before they speak (Oviatt), so a word looks
/// back further than ahead.
/// </summary>
internal static class Deixis
{
    // Words that point by themselves.
    private static readonly HashSet<string> s_pointing = new(StringComparer.OrdinalIgnoreCase)
    {
        "hier", "da", "dort", "dahin", "dorthin", "hierhin", "hierher", "drüben", "daneben", "dahinter", "darunter", "darüber",
        "dies", "diese", "dieser", "dieses", "diesem", "diesen", "jenes", "jene",
    };

    // Words that point only together with one of the above ("das hier", "die da") or at the end ("Was ist das?").
    private static readonly HashSet<string> s_articles = new(StringComparer.OrdinalIgnoreCase) { "das", "dem", "den", "die", "der", "des" };

    private static readonly HashSet<string> s_here = new(StringComparer.OrdinalIgnoreCase) { "hier", "da", "dort", "drüben" };

    /// <summary>
    /// The words, with pointing ones joined: "das hier" becomes one word that points. <paramref name="times"/> gives
    /// each raw word's time.
    /// </summary>
    public static List<SpokenWord> Read(IReadOnlyList<string> words, IReadOnlyList<double> times)
    {
        var result = new List<SpokenWord>();
        for (var i = 0; i < words.Count; i++)
        {
            var bare = Bare(words[i]);
            var next = i + 1 < words.Count ? Bare(words[i + 1]) : null;
            if (s_articles.Contains(bare) && next != null && s_here.Contains(next))
            {
                result.Add(new SpokenWord(words[i] + " " + words[i + 1], times[i], true, i));
                i++;
                continue;
            }
            var last = i == words.Count - 1;
            var points = s_pointing.Contains(bare) || (last && s_articles.Contains(bare) && words[i].TrimEnd().EndsWith('?'));
            result.Add(new SpokenWord(words[i], times[i], points, i));
        }
        return result;
    }

    /// <summary>The index of what a word said at <paramref name="time"/> pointed at, or null when nothing was pointed at.</summary>
    public static int? Meant(double time, IReadOnlyList<Pointing> pointings)
    {
        int? best = null;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < pointings.Count; i++)
        {
            // Resting on it shortly before the word counts most; a little after still counts.
            var offset = pointings[i].Time - time;
            var distance = offset <= 0.35 ? Math.Abs(offset + 0.25) : (offset - 0.35) * 2.5 + 0.6;
            if (offset > 1.2 || offset < -6)
                continue;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    private static string Bare(string word) => word.Trim().TrimEnd('.', ',', '!', '?', ';', ':').TrimStart('„', '"', '(').TrimEnd('“', '"', ')');
}
