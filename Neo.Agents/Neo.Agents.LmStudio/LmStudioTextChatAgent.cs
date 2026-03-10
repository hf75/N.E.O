using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Neo.Agents.Core;

namespace Neo.Agents
{
    /// <summary>
    /// Agent, der über ein LM-Studio-kompatibles OpenAI-Interface eine Nachricht abschickt und
    /// die Antwort anhand eines übergebenen JSON-Schemas erzwingt.
    /// LM Studio läuft lokal und benötigt keinen API-Key.
    /// </summary>
    public class LmStudioTextChatAgent : AgentBase
    {
        public override string Name => "LmStudioTextChatAgent";

        protected override AgentMetadata CreateMetadata()
        {
            var metadata = new AgentMetadata
            {
                Name = Name,
                Description = "Agent, der das LM Studio OpenAI-kompatible Interface nutzt, um JSON im gewünschten Schema zu generieren."
            };

            metadata.Options.Add(new Option<string>(
                name: "Endpoint", isRequired: false, defaultValue: "http://localhost:1234/v1/",
                description: "Die LM-Studio-Server-URL (Standard: http://localhost:1234/v1/)."));

            metadata.Options.Add(new Option<string>(
                name: "ApiKey", isRequired: false, defaultValue: "lm-studio",
                description: "API-Key (bei LM Studio in der Regel nicht benötigt)."));

            metadata.Options.Add(new Option<string>(
                name: "Model", isRequired: true, defaultValue: "",
                description: "Das zu verwendende LM-Studio-Modell."));

            metadata.Options.Add(new Option<float>(
                name: "Temperature", isRequired: false, defaultValue: 0.0f,
                description: "Sampling-Temperature (z.B. 0.7)."));

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
                description: "User-Eingabe oder Frage, die an das Modell gesendet wird."));

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

            var endpoint = GetOption<string>("Endpoint") ?? "http://localhost:1234/v1/";
            var apiKey = GetOption<string>("ApiKey");
            var model = GetOption<string>("Model");
            var timeoutSeconds = GetOption<int>("TimeoutSeconds");

            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = "lm-studio";

            var systemMessage = GetInput<string>("SystemMessage");
            var history = GetInput<string>("History") ?? string.Empty;
            var prompt = GetInput<string>("Prompt");
            var jsonSchema = GetInput<string>("JsonSchema");

            var timeout = timeoutSeconds == 0
                ? System.Threading.Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds(timeoutSeconds);

            var ct = cancellationToken ?? CancellationToken.None;

            // JSON-Schema im Prompt anhängen (lokale Modelle unterstützen kein strict JSON schema)
            var finalPrompt = new StringBuilder();
            finalPrompt.AppendLine(prompt);
            finalPrompt.AppendLine();
            finalPrompt.AppendLine("Please provide ONLY a valid JSON data object that strictly conforms to the provided schema. " +
                "Do NOT output the JSON schema itself, any explanation, or a description of the structure—output ONLY a JSON object that can be directly deserialized.");
            finalPrompt.AppendLine(jsonSchema);

            var url = endpoint.TrimEnd('/') + "/chat/completions";

            using var httpClient = new HttpClient { Timeout = timeout };
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var messages = new List<ApiMessage>
            {
                new() { Role = "system", Content = systemMessage },
                new() { Role = "assistant", Content = history },
                new() { Role = "user", Content = finalPrompt.ToString() }
            };

            var request = new ApiRequest
            {
                Model = model,
                MaxTokens = 4096 * 8,
                Messages = messages,
                ResponseFormat = new ApiResponseFormat { Type = "json_object" }
            };

            var response = await httpClient.PostAsJsonAsync(url, request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"LM Studio API error ({response.StatusCode}): {Truncate(content, 500)}");

            var result = JsonSerializer.Deserialize<ApiResponse>(content);
            var text = result?.Choices?.FirstOrDefault()?.Message?.Content;

            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("No response from LM Studio API.");

            // Markdown-Fences entfernen (lokale Modelle packen JSON manchmal in ```)
            SetOutput("Result", StripMarkdownFences(text));
        }

        private static string StripMarkdownFences(string text)
        {
            string result = text.Trim();
            if (result.StartsWith("```json"))
                result = result.Substring(7);
            else if (result.StartsWith("```"))
                result = result.Substring(3);
            if (result.EndsWith("```"))
                result = result.Substring(0, result.Length - 3);
            return result.Trim();
        }

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength] + "...";

        #region OpenAI-compatible REST DTOs

        private class ApiRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = "";

            [JsonPropertyName("max_tokens")]
            public int MaxTokens { get; set; }

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
