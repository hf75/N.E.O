using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Neo.Agents.Core;

namespace Neo.Agents
{
    public class AnthropicTextChatAgent : AgentBase
    {
        // NEO_ANTHROPIC_BASE_URL: for tests that answer in place of the API from another process. Deliberately not
        // the SDKs' ANTHROPIC_BASE_URL: a Claude session sets that for the programs it starts, and the user's
        // key must not go wherever that points.
        private static readonly string BaseUrl = Environment.GetEnvironmentVariable("NEO_ANTHROPIC_BASE_URL") ?? "https://api.anthropic.com";
        private static readonly string ApiUrl = BaseUrl + "/v1/messages";
        private static readonly string ModelsUrl = BaseUrl + "/v1/models";
        private const string ApiVersion = "2023-06-01";
        private const string FallbackRequestBeta = "server-side-fallback-2026-07-01";
        private const string FallbackListingBeta = "server-side-fallback-2026-06-01";

        // What the API reports about a model, asked once per model so that a new model needs no code change.
        private static readonly ConcurrentDictionary<string, ModelCapabilities> KnownModels = new();

        // Lets tests answer in place of the real API.
        internal HttpMessageHandler? Handler { get; set; }

        public override string Name => "AnthropicTextChatAgent";

        protected override AgentMetadata CreateMetadata()
        {
            var metadata = new AgentMetadata
            {
                Name = Name,
                Description = "Agent, der das Anthropic Chat-Interface nutzt, um JSON im gewünschten Schema zu generieren."
            };

            metadata.Options.Add(new Option<string>("ApiKey", true, null!, "Dein Anthropic API Key (Pflicht)."));
            metadata.Options.Add(new Option<string>("Model", true, "claude-sonnet-5-5", "Das zu verwendende Modell."));
            // Accepted because every caller sets it on all providers, but never sent: Claude 4.7+ answers 'temperature' with a 400.
            metadata.Options.Add(new Option<float>("Temperature", false, 0.0f, "Wird nicht an Claude gesendet."));
            metadata.Options.Add(new Option<int>("TimeoutSeconds", false, 600, "So lange darf die API schweigen, in Sekunden (0 = unendlich). Die Antwort selbst darf beliebig lange dauern."));
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
            var timeoutSeconds = GetOption<int>("TimeoutSeconds");

            var systemMessage = GetInput<string>("SystemMessage");
            var history = GetInput<string>("History") ?? string.Empty;
            var prompt = GetInput<string>("Prompt");
            var jsonSchema = GetInput<string>("JsonSchema");

            var timeout = timeoutSeconds == 0
                ? System.Threading.Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds(timeoutSeconds);

            var ct = cancellationToken ?? CancellationToken.None;

            using var httpClient = Handler != null
                ? new HttpClient(Handler, disposeHandler: false) { Timeout = timeout }
                : new HttpClient { Timeout = timeout };
            httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
            httpClient.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);

            // Build messages
            var messages = new List<ApiMessage>();
            if (!string.IsNullOrEmpty(history))
                messages.Add(new ApiMessage { Role = "assistant", Content = history });
            messages.Add(new ApiMessage { Role = "user", Content = prompt });

            var capabilities = await GetCapabilitiesAsync(httpClient, model, ct);

            var request = new ApiRequest
            {
                Model = model,
                MaxTokens = 64000,
                System = systemMessage,
                Messages = messages,
                // A safety classifier can decline harmless code requests; re-run those on the model Anthropic recommends.
                Fallbacks = capabilities.ServerSideFallback ? "default" : null
            };

            string rawJsonResponse;

            if (string.IsNullOrEmpty(jsonSchema))
            {
                // Plain text mode
                var result = await PostAsync(httpClient, request, timeout, ct);
                rawJsonResponse = AllText(result)
                    ?? throw new InvalidOperationException("No text response from Anthropic API.");
            }
            else if (capabilities.StructuredOutput)
            {
                // Only here the API guarantees the schema. A forced tool does not (Haiku 5.5 sent arrays
                // as strings), and Opus 5.5, Sonnet 5.5 and Fable 5.1 reject it with a 400.
                request.OutputConfig = new ApiOutputConfig
                {
                    Format = new ApiOutputFormat { Schema = JsonNode.Parse(jsonSchema) }
                };

                var result = await PostAsync(httpClient, request, timeout, ct);
                ThrowIfCutOff(result);
                rawJsonResponse = AllText(result)
                    ?? throw new InvalidOperationException("No structured output from Anthropic API.");
            }
            else
            {
                // Tool Use mode for structured JSON output on models without structured outputs
                request.Tools = [new ApiTool
                {
                    Name = "record_summary",
                    Description = "Generate JSON output based on the given schema.",
                    InputSchema = JsonNode.Parse(jsonSchema)
                }];
                request.ToolChoice = new ApiToolChoice
                {
                    Type = "tool",
                    Name = "record_summary"
                };

                var result = await PostAsync(httpClient, request, timeout, ct);
                ThrowIfCutOff(result);
                var toolUse = result.Content.LastOrDefault(c => c.Type == "tool_use");
                rawJsonResponse = toolUse?.Input?.ToJsonString()
                    ?? throw new InvalidOperationException("No tool_use response from Anthropic API.");
            }

            SetOutput("Result", rawJsonResponse);
        }

        private static async Task<ModelCapabilities> GetCapabilitiesAsync(HttpClient httpClient, string model, CancellationToken ct)
        {
            if (KnownModels.TryGetValue(model, out var known))
                return known;

            using var message = new HttpRequestMessage(HttpMethod.Get, $"{ModelsUrl}/{Uri.EscapeDataString(model)}");
            message.Headers.Add("anthropic-beta", FallbackListingBeta);

            try
            {
                using var response = await httpClient.SendAsync(message, ct);
                if (!response.IsSuccessStatusCode)
                    return default;

                var info = JsonSerializer.Deserialize<ApiModelInfo>(await response.Content.ReadAsStringAsync(ct));
                return KnownModels[model] = new ModelCapabilities(
                    info?.Capabilities?.StructuredOutputs?.Supported == true,
                    info?.AllowedFallbackModels is { Count: > 0 });
            }
            catch (HttpRequestException)
            {
                // The Messages request that follows reports what is really wrong.
                return default;
            }
        }

        // Always streams: an answer can take many minutes, and only silence should end the wait.
        private static async Task<ApiResponse> PostAsync(HttpClient httpClient, ApiRequest request, TimeSpan silenceLimit, CancellationToken ct)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, ApiUrl) { Content = JsonContent.Create(request) };
            if (request.Fallbacks != null)
                message.Headers.Add("anthropic-beta", FallbackRequestBeta);

            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Anthropic API error ({response.StatusCode}): {Truncate(content, 500)}");
            }

            var result = await ReadAnswerAsync(response, silenceLimit, ct);

            if (result.StopReason == "refusal")
                throw new InvalidOperationException($"Claude declined this request ({result.StopCategory ?? "no reason given"}). Rephrase the prompt or choose another model.");

            return result;
        }

        private static async Task<ApiResponse> ReadAnswerAsync(HttpResponseMessage response, TimeSpan silenceLimit, CancellationToken ct)
        {
            using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct);
            silence.CancelAfter(silenceLimit);

            var result = new ApiResponse();
            var buffer = new StringBuilder();
            bool complete = false;

            try
            {
                using var stream = await response.Content.ReadAsStreamAsync(silence.Token);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (!complete && await reader.ReadLineAsync(silence.Token) is { } line)
                {
                    silence.CancelAfter(silenceLimit);

                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                        continue;

                    var data = JsonNode.Parse(line["data:".Length..]);
                    switch ((string?)data?["type"])
                    {
                        case "content_block_start":
                            result.Content.Add(new ApiContentBlock { Type = (string?)data?["content_block"]?["type"] ?? "" });
                            buffer.Clear();
                            break;

                        case "content_block_delta":
                            // text_delta or input_json_delta; thinking is not kept.
                            buffer.Append((string?)data?["delta"]?["text"] ?? (string?)data?["delta"]?["partial_json"]);
                            break;

                        case "content_block_stop":
                            if (result.Content.LastOrDefault() is { } block)
                                FinishBlock(block, buffer.ToString());
                            break;

                        case "message_delta":
                            result.StopReason = (string?)data?["delta"]?["stop_reason"] ?? result.StopReason;
                            result.StopCategory = (string?)data?["delta"]?["stop_details"]?["category"]
                                ?? (string?)data?["stop_details"]?["category"]
                                ?? result.StopCategory;
                            break;

                        case "message_stop":
                            complete = true;
                            break;

                        case "error":
                            throw new HttpRequestException($"Anthropic API error ({(string?)data?["error"]?["type"]}): {(string?)data?["error"]?["message"]}");
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"The Anthropic API sent nothing for {silenceLimit.TotalSeconds:0} seconds.");
            }

            if (!complete)
                throw new InvalidOperationException("The connection to the Anthropic API ended before the answer was complete.");

            return result;
        }

        private static void FinishBlock(ApiContentBlock block, string content)
        {
            if (block.Type == "text")
            {
                block.Text = content;
            }
            else if (block.Type == "tool_use")
            {
                // JSON cut off at max_tokens does not parse; ThrowIfCutOff reports that.
                try { block.Input = JsonNode.Parse(content); }
                catch (JsonException) { }
            }
        }

        // Thinking counts toward max_tokens on newer models; cut-off JSON must not pass as a finished answer.
        private static void ThrowIfCutOff(ApiResponse result)
        {
            if (result.StopReason == "max_tokens")
                throw new InvalidOperationException("Claude's answer reached the output limit and was cut off.");
        }

        // All blocks together: a fallback model that takes over mid-answer continues the text.
        private static string? AllText(ApiResponse result)
        {
            var texts = result.Content.Where(c => c.Type == "text").Select(c => c.Text).ToList();
            return texts.Count > 0 ? string.Concat(texts) : null;
        }

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength] + "...";

        private readonly record struct ModelCapabilities(bool StructuredOutput, bool ServerSideFallback);

        #region Anthropic REST DTOs

        private class ApiModelInfo
        {
            [JsonPropertyName("capabilities")]
            public ApiModelCapabilities? Capabilities { get; set; }

            [JsonPropertyName("allowed_fallback_models")]
            public List<JsonNode>? AllowedFallbackModels { get; set; }
        }

        private class ApiModelCapabilities
        {
            [JsonPropertyName("structured_outputs")]
            public ApiSupported? StructuredOutputs { get; set; }
        }

        private class ApiSupported
        {
            [JsonPropertyName("supported")]
            public bool Supported { get; set; }
        }

        private class ApiRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = "";

            [JsonPropertyName("max_tokens")]
            public int MaxTokens { get; set; }

            [JsonPropertyName("stream")]
            public bool Stream { get; set; } = true;

            [JsonPropertyName("system")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? System { get; set; }

            [JsonPropertyName("messages")]
            public List<ApiMessage> Messages { get; set; } = [];

            [JsonPropertyName("tools")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<ApiTool>? Tools { get; set; }

            [JsonPropertyName("tool_choice")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public ApiToolChoice? ToolChoice { get; set; }

            [JsonPropertyName("output_config")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public ApiOutputConfig? OutputConfig { get; set; }

            [JsonPropertyName("fallbacks")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Fallbacks { get; set; }
        }

        private class ApiOutputConfig
        {
            [JsonPropertyName("format")]
            public ApiOutputFormat? Format { get; set; }
        }

        private class ApiOutputFormat
        {
            [JsonPropertyName("type")]
            public string Type { get; set; } = "json_schema";

            [JsonPropertyName("schema")]
            public JsonNode? Schema { get; set; }
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

        // The answer as ReadAnswerAsync puts it together from the stream.
        private class ApiResponse
        {
            public List<ApiContentBlock> Content { get; } = [];
            public string? StopReason { get; set; }
            public string? StopCategory { get; set; }
        }

        private class ApiContentBlock
        {
            public string Type { get; set; } = "";
            public string? Text { get; set; }
            public JsonNode? Input { get; set; }
        }

        #endregion
    }
}
