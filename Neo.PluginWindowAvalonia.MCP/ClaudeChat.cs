using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Neo.Agents;
using Neo.Agents.Core;
using Neo.App;
using Neo.AssemblyForge;

namespace Neo.PluginWindowAvalonia.MCP;

/// <summary>
/// Embedded Claude client for the chat overlay.
/// Sends user requests + current code to Claude and receives patches or full code.
/// </summary>
internal sealed class ClaudeChat
{
    private string? _apiKey;
    private string _model = "claude-sonnet-5-5";
    private readonly System.Collections.Generic.List<(string role, string content)> _history = new();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);
    public string? ConfigError => _apiKey == null
        ? "ANTHROPIC_API_KEY environment variable is not set."
        : null;

    public ClaudeChat()
    {
        _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    }

    public record ChatResult(bool Success, string? Code, string? Patch, string? Explanation, string? Error);

    /// <summary>
    /// Sends the user's request to Claude with the current app code.
    /// Returns either a full code replacement or a unified diff patch.
    /// </summary>
    public async Task<ChatResult> SendAsync(string userRequest, string currentCode, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new ChatResult(false, null, null, null, ConfigError);

        try
        {
            var agent = new AnthropicTextChatAgent();
            agent.SetOption("ApiKey", _apiKey!);
            agent.SetOption("Model", _model);
            agent.SetOption("Temperature", 0.2f);
            agent.SetOption("TimeoutSeconds", 120);
            agent.SetOption("Effort", "low");

            var systemPrompt = BuildSystemPrompt();

            var prompt = $"Here is the current code of the running Avalonia app:\n\n```csharp\n{currentCode}\n```\n\n" +
                         $"User request: {userRequest}\n\n" +
                         "Answer with EITHER a unified diff patch in Patch (preferred for small changes), " +
                         "in standard unified diff format targeting './currentcode.cs', " +
                         "OR the complete replacement code in Code.";

            // Build history string
            var historyText = "";
            if (_history.Count > 0)
            {
                historyText = "Previous conversation:\n";
                foreach (var (role, content) in _history)
                    historyText += $"{role}: {content}\n";
            }

            agent.SetInput("Prompt", prompt);
            agent.SetInput("History", historyText);
            agent.SetInput("SystemMessage", systemPrompt);
            agent.SetInput("JsonSchema", JsonSchemata.JsonSchemaResponse);

            await agent.ExecuteAsync(ct);

            var result = agent.GetOutput<string>("Result");
            if (string.IsNullOrWhiteSpace(result))
                return new ChatResult(false, null, null, null, "Claude returned an empty response.");

            // Remember conversation
            _history.Add(("user", userRequest));
            _history.Add(("assistant", result.Length > 200 ? result[..200] + "..." : result));

            var response = JsonSerializer.Deserialize<StructuredResponse>(result, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (!string.IsNullOrWhiteSpace(response?.Patch))
                return new ChatResult(true, null, response.Patch, response.Explanation, null);
            if (!string.IsNullOrWhiteSpace(response?.Code))
                return new ChatResult(true, response.Code, null, response.Explanation, null);

            return new ChatResult(false, null, null, null, string.IsNullOrWhiteSpace(response?.Chat) ? "Claude returned no code or patch." : response.Chat);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClaudeChat] Error: {ex.Message}");
            return new ChatResult(false, null, null, null, ex.Message);
        }
    }

    public void ClearHistory() => _history.Clear();

    private static string BuildSystemPrompt()
    {
        return AISystemMessages.GetSystemMessage(useAvalonia: true) +
            "\n\nIMPORTANT: You are running INSIDE the app you are modifying. " +
            "The user sees the app in real-time and is asking you to modify it via a chat overlay. " +
            "The class MUST be named DynamicUserControl and inherit from UserControl.";
    }
}
