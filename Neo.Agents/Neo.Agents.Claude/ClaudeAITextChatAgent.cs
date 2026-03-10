using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Neo.Agents.Core;

namespace Neo.Agents
{
    public class AnthropicTextChatAgent : AgentBase
    {
        private const string ApiUrl = "https://api.anthropic.com/v1/messages";
        private const string ApiVersion = "2023-06-01";

        public override string Name => "AnthropicTextChatAgent";

        protected override AgentMetadata CreateMetadata()
        {
            var metadata = new AgentMetadata
            {
                Name = Name,
                Description = "Agent, der das Anthropic Chat-Interface nutzt, um JSON im gewünschten Schema zu generieren."
            };

            metadata.Options.Add(new Option<string>("ApiKey", true, null!, "Dein Anthropic API Key (Pflicht)."));
            metadata.Options.Add(new Option<string>("Model", true, "claude-3-7-sonnet-latest", "Das zu verwendende Modell."));
            metadata.Options.Add(new Option<float>("Temperature", false, 0.0f, "Sampling-Temperature."));
            metadata.Options.Add(new Option<int>("TimeoutSeconds", false, 600, "Timeout fuer den API-Request in Sekunden (0 = unendlich)."));
            metadata.Options.Add(new Option<float>("TopP", false, 0.9f, "Top-P für nucleus sampling."));

            metadata.InputParameters.Add(new InputParameter<string>("SystemMessage", true, "System-Instruction für das Modell."));
            metadata.InputParameters.Add(new InputParameter<string>("History", false, "Bisherige Unterhaltung."));
            metadata.InputParameters.Add(new InputParameter<string>("Prompt", true, "User-Eingabe."));
            metadata.InputParameters.Add(new InputParameter<string>("JsonSchema", false, "Das JSON-Schema für die Antwort."));

            metadata.OutputParameters.Add(new OutputParameter<string>(
                name: "Result",
                isAlwaysProvided: true,
                description: "Die rohe Antwort des Modells."
            ));

            return metadata;
        }

        public override void ValidateOptionsAndInputs()
        {
            if (string.IsNullOrWhiteSpace(GetOption<string>("ApiKey")))
                throw new ArgumentException("Die Option 'ApiKey' darf nicht leer sein.");
            if (string.IsNullOrWhiteSpace(GetOption<string>("Model")))
                throw new ArgumentException("Die Option 'Model' darf nicht leer sein.");

            var timeoutSeconds = GetOption<int>("TimeoutSeconds");
            if (timeoutSeconds < 0)
                throw new ArgumentException("Die Option 'TimeoutSeconds' darf nicht negativ sein.");
            if (string.IsNullOrWhiteSpace(GetInput<string>("SystemMessage")))
                throw new ArgumentException("Der Input 'SystemMessage' darf nicht leer sein.");
            if (string.IsNullOrWhiteSpace(GetInput<string>("Prompt")))
                throw new ArgumentException("Der Input 'Prompt' darf nicht leer sein.");
        }

        public override async Task ExecuteAsync(CancellationToken? cancellationToken = null)
        {
            ValidateOptionsAndInputs();

            var apiKey = GetOption<string>("ApiKey");
            var model = GetOption<string>("Model");
            var temperature = GetOption<float>("Temperature");
            var timeoutSeconds = GetOption<int>("TimeoutSeconds");

            var systemMessage = GetInput<string>("SystemMessage");
            var history = GetInput<string>("History") ?? string.Empty;
            var prompt = GetInput<string>("Prompt");
            var jsonSchema = GetInput<string>("JsonSchema");

            var timeout = timeoutSeconds == 0
                ? System.Threading.Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds(timeoutSeconds);

            var ct = cancellationToken ?? CancellationToken.None;

            using var httpClient = new HttpClient { Timeout = timeout };
            httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
            httpClient.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);

            // Build messages
            var messages = new List<ApiMessage>();
            if (!string.IsNullOrEmpty(history))
                messages.Add(new ApiMessage { Role = "assistant", Content = history });
            messages.Add(new ApiMessage { Role = "user", Content = prompt });

            string rawJsonResponse;

            if (!string.IsNullOrEmpty(jsonSchema))
            {
                // Tool Use mode for structured JSON output
                var request = new ApiRequest
                {
                    Model = model,
                    MaxTokens = 64000,
                    System = systemMessage,
                    Temperature = temperature,
                    Messages = messages,
                    Tools = [new ApiTool
                    {
                        Name = "record_summary",
                        Description = "Generate JSON output based on the given schema.",
                        InputSchema = JsonNode.Parse(jsonSchema)
                    }],
                    ToolChoice = new ApiToolChoice
                    {
                        Type = "tool",
                        Name = "record_summary"
                    }
                };

                var response = await httpClient.PostAsJsonAsync(ApiUrl, request, ct);
                var content = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Anthropic API error ({response.StatusCode}): {Truncate(content, 500)}");

                var result = JsonSerializer.Deserialize<ApiResponse>(content);
                var toolUse = result?.Content?.FirstOrDefault(c => c.Type == "tool_use");
                rawJsonResponse = toolUse?.Input?.ToJsonString()
                    ?? throw new InvalidOperationException("No tool_use response from Anthropic API.");
            }
            else
            {
                // Plain text mode
                var request = new ApiRequest
                {
                    Model = model,
                    MaxTokens = 64000,
                    System = systemMessage,
                    Temperature = temperature,
                    Messages = messages
                };

                var response = await httpClient.PostAsJsonAsync(ApiUrl, request, ct);
                var content = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Anthropic API error ({response.StatusCode}): {Truncate(content, 500)}");

                var result = JsonSerializer.Deserialize<ApiResponse>(content);
                var textBlock = result?.Content?.FirstOrDefault(c => c.Type == "text");
                rawJsonResponse = textBlock?.Text
                    ?? throw new InvalidOperationException("No text response from Anthropic API.");
            }

            SetOutput("Result", rawJsonResponse);
        }

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength] + "...";

        #region Anthropic REST DTOs

        private class ApiRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = "";

            [JsonPropertyName("max_tokens")]
            public int MaxTokens { get; set; }

            [JsonPropertyName("system")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? System { get; set; }

            [JsonPropertyName("temperature")]
            public float Temperature { get; set; }

            [JsonPropertyName("messages")]
            public List<ApiMessage> Messages { get; set; } = [];

            [JsonPropertyName("tools")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<ApiTool>? Tools { get; set; }

            [JsonPropertyName("tool_choice")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public ApiToolChoice? ToolChoice { get; set; }
        }

        private class ApiMessage
        {
            [JsonPropertyName("role")]
            public string Role { get; set; } = "";

            [JsonPropertyName("content")]
            public string Content { get; set; } = "";
        }

        private class ApiTool
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = "";

            [JsonPropertyName("description")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Description { get; set; }

            [JsonPropertyName("input_schema")]
            public JsonNode? InputSchema { get; set; }
        }

        private class ApiToolChoice
        {
            [JsonPropertyName("type")]
            public string Type { get; set; } = "";

            [JsonPropertyName("name")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Name { get; set; }
        }

        private class ApiResponse
        {
            [JsonPropertyName("content")]
            public List<ApiContentBlock>? Content { get; set; }
        }

        private class ApiContentBlock
        {
            [JsonPropertyName("type")]
            public string Type { get; set; } = "";

            [JsonPropertyName("text")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Text { get; set; }

            [JsonPropertyName("input")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public JsonNode? Input { get; set; }
        }

        #endregion
    }
}
