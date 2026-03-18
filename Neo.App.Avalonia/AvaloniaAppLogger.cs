using Avalonia.Threading;

namespace Neo.App
{
    /// <summary>
    /// Static helper referenced by AppController for converting StructuredResponse to text.
    /// Must exist in the Neo.App namespace with the name AppLogger.
    /// </summary>
    public static class AppLogger
    {
        public static string StructuredResponseToText(StructuredResponse? response)
        {
            if (response == null) return string.Empty;

            string finalString = string.Empty;
            if (!string.IsNullOrWhiteSpace(response.Patch))
            {
                finalString = "\n\nPatch:\n\n```diff\n" +
                              IndentHelper.NormalizeIndentation(response.Patch) +
                              "\n```";
            }
            else if (!string.IsNullOrWhiteSpace(response.Code))
            {
                finalString = "\n\nCode:\n\n```csharp\n" +
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

    /// <summary>
    /// Avalonia implementation of IAppLogger.
    /// Logs messages to the Avalonia ChatView control.
    /// </summary>
    public class AvaloniaAppLogger : IAppLogger
    {
        private readonly ApplicationState _appState;
        private readonly ChatView _chatView;

        public AvaloniaAppLogger(ApplicationState appState, ChatView chatView)
        {
            _appState = appState;
            _chatView = chatView;
        }

        public void LogHistory(string msg)
        {
            if (string.IsNullOrEmpty(msg) || string.IsNullOrWhiteSpace(msg)) return;
            _appState.History += "\n\n" + msg;
        }

        public void LogHistory(StructuredResponse response)
        {
            _appState.History += AppLogger.StructuredResponseToText(response);
        }

        public void LogMessage(string msg, BubbleType bubbleType)
        {
            if (string.IsNullOrEmpty(msg) || string.IsNullOrWhiteSpace(msg)) return;

            if (Dispatcher.UIThread.CheckAccess())
                _chatView.AddMessage(msg, bubbleType);
            else
                Dispatcher.UIThread.Post(() => _chatView.AddMessage(msg, bubbleType));
        }

        public void LogMessageWithMarkdownFormating(string msg, BubbleType bubbleType)
        {
            if (string.IsNullOrEmpty(msg) || string.IsNullOrWhiteSpace(msg)) return;

            if (Dispatcher.UIThread.CheckAccess())
                _chatView.AddMessageWithMarkdownFormatting(msg, bubbleType);
            else
                Dispatcher.UIThread.Post(() => _chatView.AddMessageWithMarkdownFormatting(msg, bubbleType));
        }

        public void Clear()
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                _chatView.Clear();
                _appState.History = string.Empty;
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _chatView.Clear();
                    _appState.History = string.Empty;
                });
            }
        }

    }

    /// <summary>
    /// Normalizes code indentation by removing common leading whitespace.
    /// </summary>
    public static class IndentHelper
    {
        public static string NormalizeIndentation(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = normalized.Split('\n');

            int minIndent = int.MaxValue;
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int currentIndent = 0;
                while (currentIndent < line.Length && (line[currentIndent] == ' ' || line[currentIndent] == '\t'))
                    currentIndent++;
                if (currentIndent < line.Length && currentIndent < minIndent)
                    minIndent = currentIndent;
            }

            if (minIndent == int.MaxValue || minIndent == 0) return text;

            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = lines[i].Length >= minIndent ? lines[i].Substring(minIndent) : string.Empty;
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}
