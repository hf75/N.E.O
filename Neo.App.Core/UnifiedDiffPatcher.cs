using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Neo.App
{
    public static class UnifiedDiffPatcher
    {
        public record ApplyResult(bool Success, string? PatchedText, string? ErrorMessage);

        public static ApplyResult TryApplyToCurrentCode(string originalText, string patchText)
        {
            if (originalText == null) originalText = string.Empty;
            if (string.IsNullOrWhiteSpace(patchText))
                return new ApplyResult(false, null, "Patch is empty.");

            var originalNewLine = DetectNewLine(originalText);
            var originalNormalized = NormalizeLineEndings(originalText);
            var patchNormalized = NormalizeLineEndings(StripCodeFences(patchText));

            var originalLines = SplitLines(originalNormalized);
            var hunks = ParseHunksForCurrentCode(patchNormalized);

            if (hunks.Count == 0)
            {
                if (TryExtractApplyPatchAddFileReplacement(patchNormalized, out var replacement))
                    return new ApplyResult(true, RestoreOriginalNewLines(replacement, originalNewLine), null);

                if (LooksLikeStandaloneCSharpFile(patchNormalized))
                    return new ApplyResult(true, RestoreOriginalNewLines(patchNormalized, originalNewLine), null);

                return new ApplyResult(false, null, "Patch contains no hunks for './currentcode.cs' (missing '@@' hunk headers or wrong patch format).");
            }

            int lineOffset = 0;
            foreach (var hunk in hunks)
            {
                int expectedIndex = Math.Max(0, hunk.OldStart - 1 + lineOffset);

                if (TryApplyHunkAt(originalLines, hunk, expectedIndex, out int delta, out _))
                {
                    lineOffset += delta;
                    continue;
                }

                var oldPattern = hunk.Lines
                    .Where(l => l.Op is ' ' or '-')
                    .Select(l => l.Text)
                    .ToList();

                int fuzzyIndex = FindBestHunkStart(originalLines, oldPattern, expectedIndex);
                if (fuzzyIndex >= 0 && TryApplyHunkAt(originalLines, hunk, fuzzyIndex, out delta, out _))
                {
                    lineOffset += delta;
                    continue;
                }

                return new ApplyResult(
                    false,
                    null,
                    $"Failed to apply hunk at -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} (context mismatch).");
            }

            var patched = string.Join(originalNewLine, originalLines);
            return new ApplyResult(true, patched, null);
        }

        private static string RestoreOriginalNewLines(string normalizedText, string originalNewLine)
            => normalizedText.Replace("\n", originalNewLine);

        private static bool LooksLikeStandaloneCSharpFile(string text)
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
                return false;

            // Avoid treating actual diffs as code.
            if (trimmed.Contains("diff --git", StringComparison.Ordinal) ||
                trimmed.Contains("\n@@", StringComparison.Ordinal) ||
                trimmed.Contains("\n--- ", StringComparison.Ordinal) ||
                trimmed.Contains("\n+++ ", StringComparison.Ordinal) ||
                trimmed.Contains("*** Begin Patch", StringComparison.Ordinal) ||
                trimmed.Contains("*** Update File:", StringComparison.Ordinal) ||
                trimmed.Contains("*** Add File:", StringComparison.Ordinal))
                return false;

            return trimmed.Contains("class DynamicUserControl", StringComparison.Ordinal);
        }

        private static bool TryExtractApplyPatchAddFileReplacement(string patchNormalized, out string replacement)
        {
            replacement = string.Empty;

            var lines = patchNormalized.Split('\n', StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.StartsWith("*** Add File:", StringComparison.Ordinal))
                    continue;

                if (!LineMentionsCurrentCode(line))
                    continue;

                var contentLines = new List<string>();
                for (int j = i + 1; j < lines.Length; j++)
                {
                    var l = lines[j];
                    if (l.StartsWith("*** ", StringComparison.Ordinal))
                        break;

                    if (!l.StartsWith("+", StringComparison.Ordinal))
                        break;

                    contentLines.Add(l.Length > 0 ? l.Substring(1) : string.Empty);
                }

                replacement = string.Join("\n", contentLines);
                return true;
            }

            return false;
        }

        private static string StripCodeFences(string text)
        {
            // Strip ```diff ... ``` or ``` ... ``` fences if present.
            var trimmed = text.Trim();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
                return text;

            var lines = NormalizeLineEndings(trimmed).Split('\n', StringSplitOptions.None).ToList();
            if (lines.Count < 2)
                return text;

            if (lines[0].StartsWith("```", StringComparison.Ordinal))
                lines.RemoveAt(0);

            if (lines.Count > 0 && lines[^1].StartsWith("```", StringComparison.Ordinal))
                lines.RemoveAt(lines.Count - 1);

            return string.Join("\n", lines);
        }

        private static string NormalizeLineEndings(string text)
            => text.Replace("\r\n", "\n").Replace("\r", "\n");

        private static string DetectNewLine(string originalText)
            => originalText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        private static List<string> SplitLines(string normalizedText)
            => normalizedText.Split('\n', StringSplitOptions.None).ToList();

        private static bool LinesEqual(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal))
                return true;

            return string.Equals(a.TrimEnd(), b.TrimEnd(), StringComparison.Ordinal);
        }

        private static bool TryApplyHunkAt(List<string> fileLines, Hunk hunk, int startIndex, out int delta, out string? error)
        {
            delta = 0;
            error = null;

            int index = startIndex;

            foreach (var line in hunk.Lines)
            {
                switch (line.Op)
                {
                    case ' ':
                        if (index >= fileLines.Count || !LinesEqual(fileLines[index], line.Text))
                        {
                            error = "Context mismatch.";
                            return false;
                        }
                        index++;
                        break;

                    case '-':
                        if (index >= fileLines.Count || !LinesEqual(fileLines[index], line.Text))
                        {
                            error = "Removal mismatch.";
                            return false;
                        }
                        fileLines.RemoveAt(index);
                        delta--;
                        break;

                    case '+':
                        fileLines.Insert(index, line.Text);
                        index++;
                        delta++;
                        break;

                    default:
                        error = $"Unexpected diff line op '{line.Op}'.";
                        return false;
                }
            }

            return true;
        }

        private static int FindBestHunkStart(List<string> fileLines, List<string> oldPattern, int expectedIndex)
        {
            if (oldPattern.Count == 0)
                return expectedIndex;

            int windowRadius = 250;
            int start = Math.Max(0, expectedIndex - windowRadius);
            int endExclusive = Math.Min(fileLines.Count, expectedIndex + windowRadius);

            int idx = FindPattern(fileLines, oldPattern, start, endExclusive);
            if (idx >= 0)
                return idx;

            return FindPattern(fileLines, oldPattern, 0, fileLines.Count);
        }

        private static int FindPattern(List<string> fileLines, List<string> pattern, int startInclusive, int endExclusive)
        {
            if (pattern.Count == 0)
                return startInclusive;

            int maxStart = Math.Min(endExclusive - pattern.Count, fileLines.Count - pattern.Count);
            for (int i = startInclusive; i <= maxStart; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Count; j++)
                {
                    if (!LinesEqual(fileLines[i + j], pattern[j]))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                    return i;
            }

            return -1;
        }

        private static List<Hunk> ParseHunksForCurrentCode(string patchNormalized)
        {
            var hunks = new List<Hunk>();

            bool hasAnyFileHeader = false;
            bool applyThisFile = true; // default: single-file patch without headers

            var lines = patchNormalized.Split('\n', StringSplitOptions.None);
            Hunk? currentHunk = null;

            foreach (var raw in lines)
            {
                var line = raw;

                if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    hasAnyFileHeader = true;
                    applyThisFile = LineMentionsCurrentCode(line);
                    currentHunk = null;
                    continue;
                }

                if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    hasAnyFileHeader = true;
                    applyThisFile = LineMentionsCurrentCode(line);
                    currentHunk = null;
                    continue;
                }

                // Codex patch tool style (*** Update File: path)
                if (line.StartsWith("*** Update File:", StringComparison.Ordinal) ||
                    line.StartsWith("*** Add File:", StringComparison.Ordinal) ||
                    line.StartsWith("*** Delete File:", StringComparison.Ordinal) ||
                    line.StartsWith("*** Move to:", StringComparison.Ordinal))
                {
                    hasAnyFileHeader = true;
                    applyThisFile = LineMentionsCurrentCode(line);
                    currentHunk = null;
                    continue;
                }

                // Support both standard unified-diff hunks ("@@ -l,s +l,s @@") and
                // Codex-style patch hunks that may only use "@@" as a separator.
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    if (!TryParseHunkHeader(line, out var header))
                        continue;

                    if (!applyThisFile && hasAnyFileHeader)
                    {
                        currentHunk = null;
                        continue;
                    }

                    currentHunk = new Hunk(
                        OldStart: header.OldStart,
                        OldCount: header.OldCount,
                        NewStart: header.NewStart,
                        NewCount: header.NewCount,
                        Lines: new List<OpLine>());

                    hunks.Add(currentHunk);
                    continue;
                }

                if (currentHunk == null)
                    continue;

                if (line.StartsWith("\\ No newline at end of file", StringComparison.Ordinal))
                    continue;

                if (line.Length == 0)
                    continue;

                char op = line[0];
                if (op is ' ' or '+' or '-')
                {
                    currentHunk.Lines.Add(new OpLine(op, line.Substring(1)));
                }
            }

            return hunks;
        }

        private static bool LineMentionsCurrentCode(string line)
        {
            // Accept common diff notations like:
            // diff --git a/currentcode.cs b/currentcode.cs
            // --- a/currentcode.cs
            // +++ b/./currentcode.cs
            var lowered = line.ToLowerInvariant();
            return lowered.Contains("currentcode.cs");
        }

        private static bool TryParseHunkHeader(string line, out (int OldStart, int OldCount, int NewStart, int NewCount) header)
        {
            header = default;

            // @@ -l,s +l,s @@
            var m = Regex.Match(line, @"^@@\s+-(\d+)(?:,(\d+))?\s+\+(\d+)(?:,(\d+))?\s+@@");
            if (!m.Success)
            {
                // Non-numeric hunk headers are accepted (e.g. "@@" or "@@ some-context") and
                // will be applied via fuzzy context matching.
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    header = (OldStart: 1, OldCount: 0, NewStart: 1, NewCount: 0);
                    return true;
                }

                return false;
            }

            int oldStart = int.Parse(m.Groups[1].Value);
            int oldCount = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
            int newStart = int.Parse(m.Groups[3].Value);
            int newCount = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1;

            header = (oldStart, oldCount, newStart, newCount);
            return true;
        }

        private sealed record OpLine(char Op, string Text);

        private sealed record Hunk(int OldStart, int OldCount, int NewStart, int NewCount, List<OpLine> Lines);
    }
}
