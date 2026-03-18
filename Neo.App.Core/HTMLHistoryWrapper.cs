using Neo.App;
using System.Net;
using System.Text.RegularExpressions;

namespace Neo.App
{
    public static class HTMLHistoryWrapper
    {
        public static string HtmlPage = @"
    <!DOCTYPE html>
    <html lang=""de"" style=""height: 100%;"">
    <head>
        <meta http-equiv=""X-UA-Compatible"" content=""IE=edge"" />
        <meta charset=""UTF-8"">
        <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
        <title>Chat Layout</title>
        <link href=""https://fonts.googleapis.com/css2?family=Segoe+UI+Semilight"" rel=""stylesheet"">
        <style>
            html {
                height: 100%; /* Wichtig für %-Höhen bei Kindern */
            }
            body {
                font-family: ""Segoe UI Semilight"", Arial, sans-serif; /* Fallback Font hinzugefügt */
                margin: 0;
                padding: 0;
                background-color: #ffffff;
                height: 100%; /* Nimmt die volle Höhe des HTML-Elements */

            }
            .chat-container {

                height: 100%; /* Nimmt die volle Höhe des Body */
                box-sizing: border-box; /* Padding wird in die Höhe einbezogen */


                padding: 10px;
                overflow-y: auto; /* Scrollbar anzeigen, wenn Inhalte zu groß werden */
            }
            .bubble {
                line-height: 1.6;
                margin: 5px 0;
                padding: 5px 10px;
                border-radius: 15px; /* Evtl. etwas weniger, da IE es anders rendern könnte */
                overflow-wrap: anywhere; /* Besser als word-wrap für moderne Browser */
                display: block;  /* Sicherstellen, dass es ein Block ist für Stapelung */
                max-width: 80%; /* Verhindert, dass Bubbles zu breit werden */
                font-family: ""Segoe UI Semilight"", Arial, sans-serif; /* Schriftart explizit setzen */
                clear: both; /* Verhindert unerwünschtes Umfließen, falls float genutzt würde */
            }
            .prompt {
                background-color: #f0f0f0;
                color: #333333;

                /* text-align: justify; Blocksatz kann bei kurzen Zeilen seltsam aussehen, evtl. entfernen */
                white-space: pre-wrap; /* Behält Zeilenumbrüche bei */
                float: right; /* Lässt die Bubble nach rechts schweben */
                margin-left: auto; /* Schiebt sie nach rechts */
                margin-right: 0;
                /* Optional: Text im Prompt rechtsbündig */
                /* text-align: right; */
            }
            .answer {
                background-color: #f0f0f0;
                color: #333333;

                text-align: left;
                /* text-align: justify; Blocksatz kann bei kurzen Zeilen seltsam aussehen, evtl. entfernen */
                float: left; /* Lässt die Bubble nach links schweben */
                margin-right: auto; /* Schiebt sie nach links */
                margin-left: 0;
                white-space: pre-wrap; /* Behält Zeilenumbrüche bei */
                /* white-space: pre-wrap; fehlt hier, evtl. hinzufügen wenn nötig */
            }
            .error {
                background-color: #ff5252;
                color: #ffffff;

                text-align: left;
                /* text-align: justify; Blocksatz kann bei kurzen Zeilen seltsam aussehen, evtl. entfernen */
                float: left; /* Lässt die Bubble nach links schweben */
                margin-right: auto; /* Schiebt sie nach links */
                margin-left: 0;
                white-space: pre-wrap; /* Behält Zeilenumbrüche bei */
                /* white-space: pre-wrap; fehlt hier, evtl. hinzufügen wenn nötig */
            }
            .info {
                background-color: #5252ff;
                color: #ffffff;

                text-align: left;
                /* text-align: justify; Blocksatz kann bei kurzen Zeilen seltsam aussehen, evtl. entfernen */
                float: left; /* Lässt die Bubble nach links schweben */
                margin-right: auto; /* Schiebt sie nach links */
                margin-left: 0;
                white-space: pre-wrap; /* Behält Zeilenumbrüche bei */
                /* white-space: pre-wrap; fehlt hier, evtl. hinzufügen wenn nötig */
            }
            .success {
                background-color: #43A047;
                color: #ffffff;

                text-align: left;
                /* text-align: justify; Blocksatz kann bei kurzen Zeilen seltsam aussehen, evtl. entfernen */
                float: left; /* Lässt die Bubble nach links schweben */
                margin-right: auto; /* Schiebt sie nach links */
                margin-left: 0;
                white-space: pre-wrap; /* Behält Zeilenumbrüche bei */
                /* white-space: pre-wrap; fehlt hier, evtl. hinzufügen wenn nötig */
            }
            /* Optional: Wenn Blocksatz gewünscht ist */
            .bubble p {
                 margin: 0; /* Standard-Absatz-Margin entfernen, wenn nicht gewünscht */
                 text-align: justify;
            }
            /* Clearfix für den Container, damit er die Floats umschließt */
            .chat-container::after {
                content: """";
                display: table;
                clear: both;
            }
        </style>
    </head>
    <body style=""height: 100%;"">
        <div class=""chat-container""></div>
    </body>
    </html>";

        public static string CreateBubble(string text, BubbleType bubbleType)
        {
            string className = "prompt";
            if (bubbleType == BubbleType.Answer)
                className = "answer";
            else if (bubbleType == BubbleType.CompletionError)
                className = "error";
            else if (bubbleType == BubbleType.CompletionSuccess)
                className = "success";
            else if (bubbleType == BubbleType.Info)
                className = "info";

            // Hier ist die wichtige Änderung: Den Text vor dem Einfügen enkodieren.
            string encodedText = WebUtility.HtmlEncode(text);

            return $"<div class=\"bubble {className}\">{encodedText}</div>";
        }

        public static string AddUniqueIdToOuterDiv(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                throw new ArgumentException("HTML input cannot be null or empty", nameof(html));
            }

            // GUID generieren
            string uniqueId = "replaceid_" + Guid.NewGuid().ToString();

            // Regex, um das äußere <div> zu finden und ein id-Attribut einzufügen
            string pattern = @"<div\b"; // Sucht das erste <div>
            string replacement = $"<div id=\"{uniqueId}\"";

            // Nur das erste Vorkommen ersetzen
            string result = Regex.Replace(html, pattern, replacement, RegexOptions.IgnoreCase);

            return result;
        }

        public static string AppendDivToHtmlPage(string htmlPage, string divHtml)
        {
            // Sicherstellen, dass der Container existiert
            if (!htmlPage.Contains("chat-container"))
            {
                htmlPage = ReplaceBodyWithChatContainer(htmlPage);
            }

            // Füge das neue DIV direkt vor dem schließenden Container-Tag ein
            int containerEnd = htmlPage.LastIndexOf("</div>");
            if (containerEnd >= 0)
            {
                return htmlPage.Insert(containerEnd, divHtml);
            }

            throw new InvalidOperationException("Chat-Container nicht gefunden.");
        }

        public static string ExtractCode(string input)
        {
            // Finde den Beginn des ersten ``` und das Ende des letzten ```
            int startIndex = input.IndexOf("```") + 3;  // Start nach dem ersten ```
            int firstNewLineAfterStart = input.IndexOf('\n', startIndex);  // Finde das erste Newline nach der Sprache

            if (startIndex < 0 || firstNewLineAfterStart < 0)
            {
                return input; // Falls keine gültigen Markierungen gefunden werden
            }

            // Setze den Startindex auf die erste Zeile nach der Sprache
            startIndex = firstNewLineAfterStart + 1;

            int endIndex = input.LastIndexOf("```");  // Letztes ``` finden

            // Extrahiere den Codeblock und trimme Leerzeichen und Zeilenumbrüche am Anfang/Ende
            if (startIndex >= 0 && endIndex > startIndex)
            {
                string code = input.Substring(startIndex, endIndex - startIndex);
                return code.Trim();  // Entferne zusätzliche Leerzeichen oder Zeilenumbrüche am Rand
            }

            return input; // Rückgabe des Originals, falls keine Markierungen gefunden werden
        }

        public static string RemoveHtmlCodeIndicator(string html)
        {
            if (IsCode(html))
                html = ExtractCode(html);

            return html;
        }

        public static bool IsCode(string input)
        {
            return input.StartsWith("```");
        }

        public static string ReplaceBodyWithChatContainer(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return html;

            string pattern = @"<body[^>]*>.*?</body>";
            string replacement = @"<body><div class=""chat-container""></div></body>";

            return Regex.Replace(html, pattern, replacement, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        }
    }
}
