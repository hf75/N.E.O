namespace Neo.App
{
    public class ChatMessage
    {
        public string Text { get; set; }
        public BubbleType Type { get; set; }

        // NEU:
        public bool IsMarkdown { get; set; }
        public bool IsCodeBlock { get; set; } // nur für Plain-Text-Codebubbles

        public ChatMessage(string text, BubbleType type)
        {
            Type = type;
            IsMarkdown = false;

            // Plaintext: evtl. Codefence zu "Code only" extrahieren (wie bisher)
            IsCodeBlock = text.Trim().StartsWith("```");
            Text = IsCodeBlock ? ExtractCode(text) : text;
        }

        public ChatMessage(string text, BubbleType type, bool isMarkdown)
        {
            Type = type;
            IsMarkdown = isMarkdown;

            // Bei Markdown NICHT Codefences entfernen – MdXaml rendert die sauber selbst.
            IsCodeBlock = false;
            Text = text;
        }

        private string ExtractCode(string input)
        {
            int startIndex = input.IndexOf("```") + 3;
            int firstNewLineAfterStart = input.IndexOf('\n', startIndex);
            startIndex = (firstNewLineAfterStart < 0) ? startIndex : firstNewLineAfterStart + 1;

            int endIndex = input.LastIndexOf("```");
            if (endIndex > startIndex) return input.Substring(startIndex, endIndex - startIndex).Trim();
            return input;
        }
    }
}
