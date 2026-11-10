using System.Text.Json;

namespace Neo.Agents
{
    /// <summary>How a Claude Code job ended.</summary>
    /// <param name="Succeeded">Claude Code finished the task without an error.</param>
    /// <param name="Text">Its closing words, or what stopped it.</param>
    /// <param name="StructuredOutputJson">The data it filled in, when a schema was asked for.</param>
    /// <param name="Ending">"success", Claude Code's reason for an error, or "cancelled", "timeout", "no result".</param>
    public sealed record ClaudeCodeJobResult(
        bool Succeeded,
        string Text,
        string? StructuredOutputJson,
        string? SessionId,
        double CostUsd,
        int Turns,
        TimeSpan Duration,
        string Ending);

    /// <summary>
    /// Reads what Claude Code writes in print mode with --output-format stream-json, one JSON object per line,
    /// and turns it into short lines for people and, at the end, the result.
    /// </summary>
    internal sealed class ClaudeCodeTranscript
    {
        private readonly Dictionary<string, string> _toolNames = new();

        public List<string> Progress { get; } = new();
        public string LastText { get; private set; } = "";
        public string? SessionId { get; private set; }
        public ClaudeCodeJobResult? Result { get; private set; }

        public void Add(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(line);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return; // not one of Claude Code's events
            }

            if (root.ValueKind != JsonValueKind.Object)
                return;

            switch (Text(root, "type"))
            {
                case "system" when Text(root, "subtype") == "init":
                    SessionId = Text(root, "session_id") ?? SessionId;
                    var model = Text(root, "model");
                    Progress.Add(model == null ? "Claude Code started." : $"Claude Code started ({model}).");
                    break;

                case "assistant":
                    foreach (var block in Blocks(root))
                    {
                        switch (Text(block, "type"))
                        {
                            case "text" when Text(block, "text") is { } text && !string.IsNullOrWhiteSpace(text):
                                LastText = text.Trim();
                                Progress.Add(OneLine(text, 240));
                                break;
                            case "tool_use":
                                var name = Text(block, "name") ?? "a tool";
                                if (Text(block, "id") is { } id)
                                    _toolNames[id] = name;
                                var input = block.TryGetProperty("input", out var value) ? value : default;
                                if (DescribeTool(name, input) is { } step)
                                    Progress.Add(step);
                                break;
                        }
                    }
                    break;

                case "user":
                    foreach (var block in Blocks(root))
                    {
                        if (Text(block, "type") == "tool_result" && IsTrue(block, "is_error"))
                        {
                            var tool = Text(block, "tool_use_id") is { } id && _toolNames.TryGetValue(id, out var name) ? name : "A step";
                            Progress.Add($"{tool} failed: {OneLine(ResultText(block), 200)}");
                        }
                    }
                    break;

                case "result":
                    Result = ReadResult(root);
                    Progress.Add(Result.Succeeded ? "Done." : $"Stopped: {Result.Ending}.");
                    break;
            }
        }

        private ClaudeCodeJobResult ReadResult(JsonElement root)
        {
            var ending = Text(root, "subtype") ?? "unknown";
            var succeeded = ending == "success" && !IsTrue(root, "is_error");

            var text = Text(root, "result");
            if (string.IsNullOrWhiteSpace(text))
                text = LastText;
            if (ending != "success")
            {
                var reason = ending switch
                {
                    "error_max_budget_usd" => "Claude Code stopped: the budget for this task is used up.",
                    "error_max_turns" => "Claude Code stopped: the task took too many steps.",
                    "error_during_execution" => "Claude Code stopped because of an error.",
                    _ => $"Claude Code stopped ({ending}).",
                };
                text = string.IsNullOrWhiteSpace(text) ? reason : reason + "\n\n" + text;
            }

            if (root.TryGetProperty("permission_denials", out var denials) && denials.ValueKind == JsonValueKind.Array && denials.GetArrayLength() > 0)
            {
                var tools = denials.EnumerateArray().Select(denial => Text(denial, "tool_name") ?? "a tool").Distinct();
                Progress.Add($"Not allowed here: {string.Join(", ", tools)}.");
            }

            string? structured = root.TryGetProperty("structured_output", out var data)
                                 && data.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? data.GetRawText()
                : null;

            return new ClaudeCodeJobResult(
                succeeded,
                text ?? "",
                structured,
                Text(root, "session_id") ?? SessionId,
                Number(root, "total_cost_usd"),
                (int)Number(root, "num_turns"),
                TimeSpan.FromMilliseconds(Number(root, "duration_ms")),
                ending);
        }

        private static string? DescribeTool(string name, JsonElement input) => name switch
        {
            "Read" => $"Reads {FileName(input, "file_path")}",
            "Write" => $"Writes {FileName(input, "file_path")}",
            "Edit" or "MultiEdit" => $"Changes {FileName(input, "file_path")}",
            "NotebookEdit" => $"Changes {FileName(input, "notebook_path")}",
            "Bash" or "PowerShell" => $"Runs {OneLine(Text(input, "command") ?? "a command", 160)}",
            "Glob" => $"Looks for files: {Text(input, "pattern")}",
            "Grep" => $"Searches the files for {Text(input, "pattern")}",
            "WebSearch" => $"Searches the web: {Text(input, "query")}",
            "WebFetch" => $"Reads {Text(input, "url")}",
            "TodoWrite" => Plan(input),
            "Task" or "Agent" => $"Hands a part to a helper: {Text(input, "description")}",
            "StructuredOutput" => null, // how the data comes back, not a step of the work
            _ => $"Uses {name}",
        };

        private static string? Plan(JsonElement input)
        {
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("todos", out var todos) || todos.ValueKind != JsonValueKind.Array)
                return null;

            var current = todos.EnumerateArray().FirstOrDefault(todo => Text(todo, "status") == "in_progress");
            if (current.ValueKind == JsonValueKind.Object)
                return "Now: " + (Text(current, "activeForm") ?? Text(current, "content"));

            var done = todos.EnumerateArray().Count(todo => Text(todo, "status") == "completed");
            return $"Plan: {done} of {todos.GetArrayLength()} steps done";
        }

        private static string FileName(JsonElement input, string property)
        {
            var path = Text(input, property);
            return string.IsNullOrWhiteSpace(path) ? "a file" : Path.GetFileName(path.TrimEnd('\\', '/'));
        }

        private static string ResultText(JsonElement block)
        {
            if (!block.TryGetProperty("content", out var content))
                return "";
            if (content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? "";
            if (content.ValueKind == JsonValueKind.Array)
                return string.Join(" ", content.EnumerateArray().Select(part => Text(part, "text")).Where(text => text != null));
            return "";
        }

        private static IEnumerable<JsonElement> Blocks(JsonElement root)
            => root.TryGetProperty("message", out var message)
               && message.ValueKind == JsonValueKind.Object
               && message.TryGetProperty("content", out var content)
               && content.ValueKind == JsonValueKind.Array
                ? content.EnumerateArray()
                : [];

        private static string? Text(JsonElement element, string property)
            => element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(property, out var value)
               && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static double Number(JsonElement element, string property)
            => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

        private static bool IsTrue(JsonElement element, string property)
            => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

        internal static string OneLine(string text, int length)
        {
            var line = string.Join(" ", text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return line.Length <= length ? line : line[..(length - 1)] + "…";
        }
    }
}
