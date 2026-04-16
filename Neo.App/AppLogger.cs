namespace Neo.App
{
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
            _appState.History += LogFormatHelper.StructuredResponseToText(response);
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
    }
}
