using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Neo.Agents;

namespace Neo.App
{
    /// <summary>A web page an answer comes from.</summary>
    public sealed record WebSource(string Title, string Url);

    /// <summary>An answer from the web with the pages it comes from. Without sources, no page backs it.</summary>
    public sealed record WebAnswer(string Text, IReadOnlyList<WebSource> Sources);

    /// <summary>An answer from the web as data, with the pages it comes from.</summary>
    public sealed record WebAnswer<T>(T Data, IReadOnlyList<WebSource> Sources);

    public static class AIWebSearch
    {
        private static string ApiKey => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY", EnvironmentVariableTarget.Process) ?? "";

        /// <summary>
        /// Answers a question from the current web, with the pages the answer comes from.
        /// </summary>
        public static async Task<WebAnswer> AskAsync(string question, CancellationToken cancellationToken = default)
        {
            var answer = await ClaudeWebSearch.AskAsync(ApiKey, "WEB_SEARCH_MODEL_PLACEHOLDER", question, cancellationToken);
            return new WebAnswer(answer.Text, answer.Sources.Select(s => new WebSource(s.Title, s.Url)).ToList());
        }

        /// <summary>
        /// Answers a question from the current web as data of the given type, with the pages it comes from.
        /// </summary>
        public static async Task<WebAnswer<T>> AskAsync<T>(string question, CancellationToken cancellationToken = default)
        {
            var answer = await ClaudeWebSearch.AskAsync<T>(ApiKey, "WEB_SEARCH_MODEL_PLACEHOLDER", question, cancellationToken);
            return new WebAnswer<T>(answer.Data, answer.Sources.Select(s => new WebSource(s.Title, s.Url)).ToList());
        }
    }
}
