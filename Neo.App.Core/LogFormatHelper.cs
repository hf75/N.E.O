namespace Neo.App
{
    /// <summary>
    /// Pure text formatting utilities extracted from AppLogger for use in Core.
    /// No UI dependencies — works with StructuredResponse and plain strings.
    /// </summary>
    public static class LogFormatHelper
    {
        public static string StructuredResponseToText(StructuredResponse response)
        {
            if (response == null) return string.Empty;

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

            if (response.NuGetPackages != null)
                finalString += "\n\nUsed nuget packages:\n" + string.Join(", ", response.NuGetPackages);

            if (response.Explanation != null)
                finalString += "\n\nExplanation:\n" + response.Explanation;

            return finalString;
        }
    }

    public static class IndentHelper
    {
        /// <summary>
        /// Removes the common minimum indentation from all non-empty lines.
        /// Relative indentation within the code is preserved.
        /// </summary>
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
                while (currentIndent < line.Length &&
                       (line[currentIndent] == ' ' || line[currentIndent] == '\t'))
                {
                    currentIndent++;
                }

                if (currentIndent < line.Length)
                {
                    if (currentIndent < minIndent)
                        minIndent = currentIndent;
                }
            }

            if (minIndent == int.MaxValue || minIndent == 0)
                return text;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                lines[i] = line.Length >= minIndent ? line.Substring(minIndent) : string.Empty;
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}
