using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Neo.Agents;
using Neo.Agents.Core;
using Neo.App;

namespace Neo.PluginWindowAvalonia.MCP;

/// <summary>
/// Embedded Claude client for the chat overlay.
/// Sends user requests + current code to Claude and receives patches or full code.
/// </summary>
internal sealed class ClaudeChat
{
    private string? _apiKey;
    private string _model = "claude-sonnet-4-20250514";
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

            var systemPrompt = BuildSystemPrompt();

            var prompt = $"Here is the current code of the running Avalonia app:\n\n```csharp\n{currentCode}\n```\n\n" +
                         $"User request: {userRequest}\n\n" +
                         "Respond with EITHER a unified diff patch (preferred for small changes) " +
                         "OR complete replacement code. Do NOT include any explanation outside the code/patch. " +
                         "If you provide a patch, use standard unified diff format targeting './currentcode.cs'.";

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

            await agent.ExecuteAsync(ct);

            var result = agent.GetOutput<string>("Result");
            if (string.IsNullOrWhiteSpace(result))
                return new ChatResult(false, null, null, null, "Claude returned an empty response.");

            // Remember conversation
            _history.Add(("user", userRequest));
            _history.Add(("assistant", result.Length > 200 ? result[..200] + "..." : result));

            // Determine if it's a patch or full code
            var trimmed = result.Trim();

            // Strip markdown code fences if present
            if (trimmed.StartsWith("```"))
            {
                var lines = trimmed.Split('\n').ToList();
                if (lines.Count > 1) lines.RemoveAt(0);
                if (lines.Count > 0 && lines[^1].TrimStart().StartsWith("```"))
                    lines.RemoveAt(lines.Count - 1);
                trimmed = string.Join("\n", lines);
            }

            bool isPatch = trimmed.Contains("@@") &&
                (trimmed.Contains("---") || trimmed.Contains("+++") || trimmed.Contains("*** "));

            if (isPatch)
                return new ChatResult(true, null, trimmed, null, null);
            else
                return new ChatResult(true, trimmed, null, null, null);
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
            "Respond ONLY with code — either a unified diff patch (preferred for small changes) " +
            "or complete C# code for the DynamicUserControl. " +
            "Do NOT include explanations, markdown headings, or JSON. Just raw code or diff. " +
            "The class MUST be named DynamicUserControl and inherit from UserControl.";
    }
}
