using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace Neo.App
{
    // IAppLogger.cs
    public interface IAppLogger
    {
        void LogMessage(string message, BubbleType bubbleType);
        void LogMessageWithMarkdownFormating(string msg, BubbleType bubbleType);

        void LogHistory(string message);

        public void LogHistory(StructuredResponse response);

        void Clear();
    }

    public class AppLogger : IAppLogger
    {
        private readonly ApplicationState _appState;
        private readonly ChatView _chatView;

        public AppLogger(ApplicationState appState, ChatView chatView)
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
            _appState.History += StructuredResponseToText(response);
        }

        public void LogMessage(string msg, BubbleType bubbleType)
        {
            if (string.IsNullOrEmpty(msg) || string.IsNullOrWhiteSpace(msg)) return;

            _chatView.AddMessage(msg, bubbleType);
        }

        public void LogMessageWithMarkdownFormating(string msg, BubbleType bubbleType)
        {
            if (string.IsNullOrEmpty(msg) || string.IsNullOrWhiteSpace(msg)) return;

            _chatView.AddMessageWithMarkdownFormatting(msg, bubbleType);
        }

        public void Clear()
        {
            _chatView.Clear();
            _appState.History = string.Empty;
        }

        public static string StructuredResponseToText(StructuredResponse response)
        {
            if( response == null ) return string.Empty;

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
        /// Entfernt die gemeinsame, minimale Einrückung aller nicht-leeren Zeilen.
        /// Die relativen Einrückungen innerhalb des Codes bleiben erhalten,
        /// aber das Snippet beginnt so weit wie möglich links.
        /// </summary>
        public static string NormalizeIndentation(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            // Zeilen vereinheitlichen: \r\n, \r -> \n
            var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = normalized.Split('\n');

            int minIndent = int.MaxValue;

            // 1. minimale Einrückung bestimmen
            foreach (var line in lines)
            {
                // Leere oder nur aus Whitespaces bestehende Zeilen ignorieren
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                int currentIndent = 0;

                // Zähle führende Spaces oder Tabs
                while (currentIndent < line.Length &&
                       (line[currentIndent] == ' ' || line[currentIndent] == '\t'))
                {
                    currentIndent++;
                }

                // Nur berücksichtigen, wenn die Zeile überhaupt Inhalt hat
                if (currentIndent < line.Length)
                {
                    if (currentIndent < minIndent)
                        minIndent = currentIndent;
                }
            }

            // Falls keine sinnvolle Einrückung gefunden wurde, Text unverändert zurückgeben
            if (minIndent == int.MaxValue || minIndent == 0)
                return text;

            // 2. diese Einrückung von allen Zeilen entfernen
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                if (line.Length >= minIndent)
                {
                    lines[i] = line.Substring(minIndent);
                }
                else
                {
                    // Zeile ist kürzer als minIndent → einfach komplett leeren
                    lines[i] = string.Empty;
                }
            }

            // 3. Zeilen mit System-spezifischem NewLine wieder zusammenfügen
            return string.Join(Environment.NewLine, lines);
        }
    }

}
