using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Neo.App
{
    /// <summary>
    /// N.E.O. as the app builder of the stage (Neo.Stage, the voice). Started hidden with <c>--stage &lt;pipe&gt;</c>
    /// (and <c>--title &lt;name&gt;</c> for its app window), it takes prompts through a named pipe of the same user, one JSON
    /// line each, builds or changes its app as if they were typed into it, and answers with how that went. Only the app
    /// window is seen; Ctrl+Shift+N shows N.E.O. beside it, as with a living app. Closing the app window ends it.
    /// </summary>
    public sealed class StageLink
    {
        private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

        private StageLink(string pipeName, string? title)
        {
            PipeName = pipeName;
            Title = title;
        }

        public string PipeName { get; }

        /// <summary>What the app window is called until the app names itself.</summary>
        public string? Title { get; }

        public static StageLink? Detect(string[] arguments)
        {
            var index = Array.IndexOf(arguments, "--stage");
            if (index < 0 || index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                return null;
            var titleIndex = Array.IndexOf(arguments, "--title");
            var title = titleIndex >= 0 && titleIndex + 1 < arguments.Length ? arguments[titleIndex + 1] : null;
            return new StageLink(arguments[index + 1], title);
        }

        /// <summary>Answers one connection after the other until <paramref name="cancellationToken"/> ends it.</summary>
        public async Task ServeAsync(Func<string, Task<StageReply>> handle, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(cancellationToken);
                    using var reader = new StreamReader(pipe, s_utf8);
                    using var writer = new StreamWriter(pipe, s_utf8) { AutoFlush = true };
                    string? line;
                    while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                    {
                        StageReply reply;
                        try
                        {
                            var request = JsonSerializer.Deserialize<StageRequest>(line);
                            reply = string.IsNullOrWhiteSpace(request?.Prompt)
                                ? new StageReply(false, false, "No prompt.")
                                : await handle(request.Prompt);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            reply = new StageReply(false, false, exception.Message);
                        }
                        await writer.WriteLineAsync(JsonSerializer.Serialize(reply).AsMemory(), cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // The stage went away in the middle; wait for the next one.
                }
            }
        }
    }

    public sealed record StageRequest(string Prompt);

    /// <summary>How a prompt went: whether it ran, whether an app is loaded now, and what N.E.O. wrote to its chat meanwhile.</summary>
    public sealed record StageReply(bool Done, bool AppLoaded, string Chat);

    /// <summary>The chat as N.E.O. writes it, with what each prompt adds kept for the stage's answer.</summary>
    internal sealed class StageChatTap : IAppLogger
    {
        private readonly IAppLogger _inner;
        private readonly List<string> _lines = [];

        public StageChatTap(IAppLogger inner) => _inner = inner;

        /// <summary>What was written since the last call: answers and errors, not the prompt itself.</summary>
        public string Take()
        {
            lock (_lines)
            {
                var text = string.Join(Environment.NewLine, _lines.Where(line => line.Length > 0));
                _lines.Clear();
                return text.Length <= 2000 ? text : text[..2000] + " …";
            }
        }

        // Words for the stage, not code: a patch or a code block says nothing to someone who listens.
        private void Keep(string message, BubbleType type)
        {
            var code = message.Contains("```", StringComparison.Ordinal) || message.TrimStart().StartsWith("Patch:", StringComparison.Ordinal)
                || message.Contains("diff --git", StringComparison.Ordinal);
            if (type is BubbleType.Answer or BubbleType.CompletionError or BubbleType.CompletionSuccess && !code)
            {
                lock (_lines)
                    _lines.Add((type == BubbleType.CompletionError ? "Error: " : "") + message.Trim());
            }
        }

        public void LogMessage(string message, BubbleType bubbleType)
        {
            _inner.LogMessage(message, bubbleType);
            Keep(message, bubbleType);
        }

        public void LogMessageWithMarkdownFormating(string msg, BubbleType bubbleType)
        {
            _inner.LogMessageWithMarkdownFormating(msg, bubbleType);
            Keep(msg, bubbleType);
        }

        public void LogHistory(string message) => _inner.LogHistory(message);

        public void LogHistory(Neo.AssemblyForge.StructuredResponse response) => _inner.LogHistory(response);

        public void Clear() => _inner.Clear();
    }
}
