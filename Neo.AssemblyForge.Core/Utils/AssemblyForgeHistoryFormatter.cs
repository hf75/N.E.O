using System;
using System.Linq;

namespace Neo.AssemblyForge;

public static class AssemblyForgeHistoryFormatter
{
    public static string StructuredResponseToText(StructuredResponse response)
    {
        if (response is null) return string.Empty;

        string finalString = string.Empty;
        if (!string.IsNullOrWhiteSpace(response.Patch))
        {
            finalString = "\n\nPatch:\n\n" +
                          "```diff\n" +
                          IndentHelper.NormalizeIndentation(response.Patch) +
                          "\n```";
        }
        else if (!string.IsNullOrWhiteSpace(response.Code))
        {
            finalString = "\n\nCode:\n\n" +
                          "```csharp\n" +
                          IndentHelper.NormalizeIndentation(response.Code) +
                          "\n```";
        }

        if (response.NuGetPackages != null && response.NuGetPackages.Count > 0)
            finalString += "\n\nUsed nuget packages:\n" + string.Join(", ", response.NuGetPackages);

        if (!string.IsNullOrWhiteSpace(response.Explanation))
            finalString += "\n\nExplanation:\n" + response.Explanation;

        return finalString;
    }

    private static class IndentHelper
    {
        public static string NormalizeIndentation(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = normalized.Split('\n');

            int minIndent = int.MaxValue;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                int currentIndent = 0;
                while (currentIndent < line.Length && (line[currentIndent] == ' ' || line[currentIndent] == '\t'))
                    currentIndent++;

                if (currentIndent < line.Length && currentIndent < minIndent)
                    minIndent = currentIndent;
            }

            if (minIndent == int.MaxValue || minIndent == 0)
                return normalized;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                lines[i] = line.Length >= minIndent ? line.Substring(minIndent) : line;
            }

            return string.Join("\n", lines);
        }
    }
}
