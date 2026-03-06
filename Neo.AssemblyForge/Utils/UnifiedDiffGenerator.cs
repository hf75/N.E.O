using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Neo.AssemblyForge;


public static class UnifiedDiffGenerator
{
    private enum OpType { Equal, Insert, Delete }
    private sealed record Op(OpType Type, string Text);

    public static string CreatePatch(string filePath, string oldText, string newText, int contextLines = 3)
    {
        filePath ??= "./currentcode.cs";
        oldText ??= string.Empty;
        newText ??= string.Empty;

        var oldNorm = NormalizeLineEndings(oldText);
        var newNorm = NormalizeLineEndings(newText);

        var oldLines = SplitLines(oldNorm);
        var newLines = SplitLines(newNorm);

        if (oldLines.SequenceEqual(newLines, StringComparer.Ordinal))
            return "No changes.";

        var ops = MyersDiff(oldLines, newLines);
        return BuildUnifiedDiff(filePath, ops, contextLines);
    }

    private static string BuildUnifiedDiff(string filePath, List<Op> ops, int contextLines)
    {
        contextLines = Math.Max(0, contextLines);

        var oldPos = new int[ops.Count + 1];
        var newPos = new int[ops.Count + 1];

        int oldLine = 1;
        int newLine = 1;
        for (int i = 0; i < ops.Count; i++)
        {
            oldPos[i] = oldLine;
            newPos[i] = newLine;

            var t = ops[i].Type;
            if (t is OpType.Equal or OpType.Delete) oldLine++;
            if (t is OpType.Equal or OpType.Insert) newLine++;
        }
        oldPos[ops.Count] = oldLine;
        newPos[ops.Count] = newLine;

        var sb = new StringBuilder();
        sb.Append("diff --git a/").Append(filePath).Append(" b/").Append(filePath).AppendLine();
        sb.Append("--- a/").Append(filePath).AppendLine();
        sb.Append("+++ b/").Append(filePath).AppendLine();

        var hunks = BuildHunks(ops, contextLines);
        foreach (var (start, endExclusive) in hunks)
        {
            int hunkOldStart = oldPos[start];
            int hunkNewStart = newPos[start];

            int hunkOldCount = 0;
            int hunkNewCount = 0;

            for (int i = start; i < endExclusive; i++)
            {
                var t = ops[i].Type;
                if (t is OpType.Equal or OpType.Delete) hunkOldCount++;
                if (t is OpType.Equal or OpType.Insert) hunkNewCount++;
            }

            sb.Append("@@ -")
              .Append(hunkOldStart).Append(',').Append(hunkOldCount)
              .Append(" +")
              .Append(hunkNewStart).Append(',').Append(hunkNewCount)
              .AppendLine(" @@");

            for (int i = start; i < endExclusive; i++)
            {
                var op = ops[i];
                char prefix = op.Type switch
                {
                    OpType.Equal => ' ',
                    OpType.Delete => '-',
                    OpType.Insert => '+',
                    _ => ' '
                };

                sb.Append(prefix).AppendLine(op.Text);
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static List<(int Start, int EndExclusive)> BuildHunks(List<Op> ops, int contextLines)
    {
        var hunks = new List<(int Start, int EndExclusive)>();

        int i = 0;
        while (i < ops.Count)
        {
            while (i < ops.Count && ops[i].Type == OpType.Equal) i++;
            if (i >= ops.Count) break;

            int hunkStart = Math.Max(0, i - contextLines);
            int j = i;
            int equalRun = 0;

            while (j < ops.Count)
            {
                if (ops[j].Type == OpType.Equal)
                {
                    equalRun++;
                    if (equalRun > 2 * contextLines)
                    {
                        int endExclusive = j - equalRun + contextLines + 1;
                        hunks.Add((hunkStart, Math.Min(endExclusive, ops.Count)));
                        i = Math.Max(endExclusive, hunkStart + 1);
                        goto NextHunk;
                    }
                }
                else
                {
                    equalRun = 0;
                }

                j++;
            }

            hunks.Add((hunkStart, ops.Count));
            break;

        NextHunk:
            continue;
        }

        return hunks;
    }

    private static List<Op> MyersDiff(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int n = a.Count;
        int m = b.Count;
        int max = n + m;
        int offset = max;

        var v = new int[2 * max + 1];
        var trace = new List<int[]>(capacity: max + 1);

        for (int d = 0; d <= max; d++)
        {
            trace.Add((int[])v.Clone());

            for (int k = -d; k <= d; k += 2)
            {
                int idx = k + offset;

                int x;
                if (k == -d || (k != d && v[idx - 1] < v[idx + 1]))
                    x = v[idx + 1];        // insertion
                else
                    x = v[idx - 1] + 1;    // deletion

                int y = x - k;

                while (x < n && y < m && string.Equals(a[x], b[y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                v[idx] = x;

                if (x >= n && y >= m)
                    return Backtrack(trace, a, b, x, y, offset);
            }
        }

        return Backtrack(trace, a, b, n, m, offset);
    }

    private static List<Op> Backtrack(
        List<int[]> trace,
        IReadOnlyList<string> a,
        IReadOnlyList<string> b,
        int x,
        int y,
        int offset)
    {
        var edits = new List<Op>();

        for (int d = trace.Count - 1; d > 0; d--)
        {
            var v = trace[d];
            int k = x - y;
            int idx = k + offset;

            int prevK;
            if (k == -d || (k != d && v[idx - 1] < v[idx + 1]))
                prevK = k + 1;
            else
                prevK = k - 1;

            int prevX = v[prevK + offset];
            int prevY = prevX - prevK;

            while (x > prevX && y > prevY)
            {
                edits.Add(new Op(OpType.Equal, a[x - 1]));
                x--;
                y--;
            }

            if (x == prevX)
            {
                edits.Add(new Op(OpType.Insert, b[y - 1]));
                y--;
            }
            else
            {
                edits.Add(new Op(OpType.Delete, a[x - 1]));
                x--;
            }
        }

        while (x > 0 && y > 0)
        {
            edits.Add(new Op(OpType.Equal, a[x - 1]));
            x--;
            y--;
        }

        while (x > 0)
        {
            edits.Add(new Op(OpType.Delete, a[x - 1]));
            x--;
        }

        while (y > 0)
        {
            edits.Add(new Op(OpType.Insert, b[y - 1]));
            y--;
        }

        edits.Reverse();
        return edits;
    }

    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n").Replace("\r", "\n");

    private static List<string> SplitLines(string normalizedText)
        => normalizedText.Split('\n', StringSplitOptions.None).ToList();
}
