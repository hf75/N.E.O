using Neo.AssemblyForge;
using Neo.Collab;

namespace Neo.App
{
    /// <summary>
    /// The chat as the controller writes it, with a recorder attached: while a change is made in a
    /// shared session, the bubbles it produces (prompt, answer, errors) are collected so the other
    /// participants see the same conversation. Local info lines (hot reload results, undo notes)
    /// stay local — every participant writes its own.
    /// </summary>
    internal sealed class SessionChatRecorder : IAppLogger
    {
        private readonly IAppLogger _inner;
        private List<ChatLine>? _recording;

        public SessionChatRecorder(IAppLogger inner) => _inner = inner;

        public IDisposable Record(List<ChatLine> into)
        {
            _recording = into;
            return new Stop(this);
        }

        private sealed class Stop(SessionChatRecorder owner) : IDisposable
        {
            public void Dispose() => owner._recording = null;
        }

        public void LogMessage(string message, BubbleType bubbleType)
        {
            _inner.LogMessage(message, bubbleType);
            Capture(message, bubbleType, markdown: false);
        }

        public void LogMessageWithMarkdownFormating(string msg, BubbleType bubbleType)
        {
            _inner.LogMessageWithMarkdownFormating(msg, bubbleType);
            Capture(msg, bubbleType, markdown: true);
        }

        public void LogHistory(string message) => _inner.LogHistory(message);

        public void LogHistory(StructuredResponse response) => _inner.LogHistory(response);

        public void Clear() => _inner.Clear();

        private void Capture(string text, BubbleType type, bool markdown)
        {
            var kind = type switch
            {
                BubbleType.Prompt => "prompt",
                BubbleType.Answer => "answer",
                BubbleType.CompletionSuccess => "success",
                BubbleType.CompletionError => "error",
                _ => null,
            };
            if (kind != null && _recording != null && !string.IsNullOrWhiteSpace(text))
                _recording.Add(new ChatLine(kind, text, markdown));
        }

        public static BubbleType BubbleFor(string kind) => kind switch
        {
            "prompt" => BubbleType.Prompt,
            "answer" => BubbleType.Answer,
            "success" => BubbleType.CompletionSuccess,
            "error" => BubbleType.CompletionError,
            _ => BubbleType.Info,
        };
    }
}
