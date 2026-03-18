namespace Neo.App
{
    public interface IAppLogger
    {
        void LogMessage(string message, BubbleType bubbleType);
        void LogMessageWithMarkdownFormating(string msg, BubbleType bubbleType);
        void LogHistory(string message);
        void LogHistory(StructuredResponse response);
        void Clear();
    }
}
