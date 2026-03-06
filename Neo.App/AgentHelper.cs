using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using Neo.Agents;

namespace Neo.App
{
    public static class AIQuery
    {
        static AnthropicTextChatAgent _aiAgent = new AnthropicTextChatAgent();

        public static async Task<string> ExecuteAIQuery(string prompt, string history, string systemMessage)
        {
            _aiAgent.SetOption("ApiKey", Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY", EnvironmentVariableTarget.Process));
            _aiAgent.SetOption("Model", "claude-sonnet-4-5");
            _aiAgent.SetOption("Temperature", 0.1f);

            _aiAgent.SetInput("SystemMessage", systemMessage);
            _aiAgent.SetInput("Prompt", prompt);
            _aiAgent.SetInput("History", history);

            await _aiAgent.ExecuteAsync();

            string result = _aiAgent.GetOutput<string>("Result");

            return result;
        }
    }
}
