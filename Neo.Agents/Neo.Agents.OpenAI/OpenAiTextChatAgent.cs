using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Neo.Agents.Core;

namespace Neo.Agents
{
    /// <summary>
    /// Agent, der über das OpenAI Chat-Interface eine Nachricht abschickt und
    /// die Antwort anhand eines übergebenen JSON-Schemas erzwingt.
    /// </summary>
    public class OpenAiTextChatAgent : AgentBase
    {
        private const string ApiUrl = "https://api.openai.com/v1/chat/completions";

        public override string Name => "OpenAiTextChatAgent";

        protected override AgentMetadata CreateMetadata()
        {
            var metadata = new AgentMetadata
            {
                Name = Name,
                Description = "Agent, der das OpenAI Chat-Interface nutzt, um JSON im gewünschten Schema zu generieren."
            };

            metadata.Options.Add(new Option<string>(
                name: "ApiKey", isRequired: true, defaultValue: null!,
                description: "Dein OpenAI API Key (Pflicht)."));

            metadata.Options.Add(new Option<string>(
                name: "Model", isRequired: true, defaultValue: "gpt-4o",
                description: "Das zu verwendende OpenAI-Modell (z.B. gpt-4o, gpt-4.1)."));

            metadata.Options.Add(new Option<float>(
                name: "Temperature", isRequired: false, defaultValue: 0.0f,
                description: "Sampling-Temperature."));

            metadata.Options.Add(new Option<float>(
                name: "TopP", isRequired: false, defaultValue: 0.9f,
                description: "Top-P für nucleus sampling."));

            metadata.Options.Add(new Option<int>(
                name: "TimeoutSeconds", isRequired: false, defaultValue: 600,
                description: "Timeout fuer den API-Request in Sekunden (0 = unendlich)."));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "SystemMessage", isRequired: true,
                description: "System-Instruction für das Chat-Model."));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "History", isRequired: false,
                description: "Bisherige Unterhaltung bzw. letztes Assistant-Statement."));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "Prompt", isRequired: true,
                description: "User-Eingabe oder Frage, die an das OpenAI-Model gesendet wird."));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "JsonSchema", isRequired: true,
                description: "Das JSON-Schema, das die Antwort des Modells strukturieren soll."));

            metadata.OutputParameters.Add(new OutputParameter<string>(
                name: "Result", isAlwaysProvided: true,
                description: "Die rohe JSON-Antwort des Modells (passt zum übergebenen Schema)."));

            return metadata;
        }

        public override void ValidateOptionsAndInputs()
        {
            if (string.IsNullOrWhiteSpace(GetOption<string>("ApiKey")))
                throw new ArgumentException("Die Option 'ApiKey' darf nicht leer sein.");
            if (string.IsNullOrWhiteSpace(GetOption<string>("Model")))
                throw new ArgumentException("Die Option 'Model' darf nicht leer sein.");
            if (string.IsNullOrWhiteSpace(GetInput<string>("SystemMessage")))
                throw new ArgumentException("Der Input 'SystemMessage' darf nicht leer sein.");
            if (string.IsNullOrWhiteSpace(GetInput<string>("Prompt")))
                throw new ArgumentException("Der Input 'Prompt' darf nicht leer sein.");
            if (string.IsNullOrWhiteSpace(GetInput<string>("JsonSchema")))
                throw new ArgumentException("Der Input 'JsonSchema' darf nicht leer sein.");
        }

        public override async Task ExecuteAsync(CancellationToken? cancellationToken = null)
        {
            ValidateOptionsAndInputs();

            var apiKey = GetOption<string>("ApiKey");
            var model = GetOption<string>("Model");
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
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var messages = new List<ApiMessage>
            {
                new() { Role = "system", Content = systemMessage },
                new() { Role = "assistant", Content = history },
                new() { Role = "user", Content = prompt }
            };

            var request = new ApiRequest
            {
                Model = model,
                MaxCompletionTokens = 4096 * 8,
                Messages = messages,
                ResponseFormat = new ApiResponseFormat
                {
                    Type = "json_schema",
                    JsonSchema = new ApiJsonSchemaFormat
                    {
                        Name = "user_defined_schema",
                        Strict = true,
                        Schema = JsonNode.Parse(jsonSchema)
                    }
                }
            };

            var response = await httpClient.PostAsJsonAsync(ApiUrl, request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"OpenAI API error ({response.StatusCode}): {Truncate(content, 500)}");

            var result = JsonSerializer.Deserialize<ApiResponse>(content);
            var text = result?.Choices?.FirstOrDefault()?.Message?.Content;

            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("No response from OpenAI API.");

            SetOutput("Result", text);
        }

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength] + "...";

        #region OpenAI REST DTOs

        private class ApiRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = "";

            [JsonPropertyName("max_completion_tokens")]
            public int MaxCompletionTokens { get; set; }

            [JsonPropertyName("messages")]
            public List<ApiMessage> Messages { get; set; } = [];

            [JsonPropertyName("response_format")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public ApiResponseFormat? ResponseFormat { get; set; }
        }

        private class ApiMessage
        {
            [JsonPropertyName("role")]
            public string Role { get; set; } = "";

            [JsonPropertyName("content")]
            public string Content { get; set; } = "";
        }

        private class ApiResponseFormat
        {
            [JsonPropertyName("type")]
            public string Type { get; set; } = "";

            [JsonPropertyName("json_schema")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public ApiJsonSchemaFormat? JsonSchema { get; set; }
        }

        private class ApiJsonSchemaFormat
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = "";

            [JsonPropertyName("strict")]
            public bool Strict { get; set; }

            [JsonPropertyName("schema")]
            public JsonNode? Schema { get; set; }
        }

        private class ApiResponse
        {
            [JsonPropertyName("choices")]
            public List<ApiChoice>? Choices { get; set; }
        }

        private class ApiChoice
        {
            [JsonPropertyName("message")]
            public ApiMessage? Message { get; set; }
        }

        #endregion
    }
}
