using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
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
        internal static readonly string ModelsUrl = BaseUrl + "/v1/models";
        internal const string ApiVersion = "2023-06-01";
        private const string FallbackRequestBeta = "server-side-fallback-2026-07-01";
        private const string FallbackListingBeta = "server-side-fallback-2026-06-01";

        // Direct search. The newer versions filter the results through code execution: on a simple
        // question that took more than twice as long, cost twice as much and left out the citations.
        private const string WebSearchTool = "web_search_20250305";

        // A search turn this many pauses long has gone wrong; the docs suggest about this limit.
        private const int MaxPauses = 5;

        // What the API reports about a model, asked once per model so that a new model needs no code change.
        private static readonly ConcurrentDictionary<string, ModelCapabilities> KnownModels = new();

        // Lets tests answer in place of the real API.
        internal HttpMessageHandler? Handler { get; set; }

        /// <summary>What the model is searching for right now; null when that search is done.</summary>
        public Action<string?>? WebSearchProgress { get; set; }

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
            metadata.Options.Add(new Option<string>("Effort", false, "", "Wie gründlich das Modell arbeitet: low, medium, high, xhigh, max. Leer: die Vorgabe des Modells."));
            metadata.Options.Add(new Option<int>("WebSearchMaxUses", false, 0, "So oft darf das Modell im Web suchen. 0: gar nicht."));

            metadata.InputParameters.Add(new InputParameter<string>("SystemMessage", true, "System-Instruction für das Modell."));
            metadata.InputParameters.Add(new InputParameter<string>("History", false, "Bisherige Unterhaltung."));
            metadata.InputParameters.Add(new InputParameter<string>("Prompt", true, "User-Eingabe."));
            metadata.InputParameters.Add(new InputParameter<string>("JsonSchema", false, "Das JSON-Schema für die Antwort."));
            metadata.InputParameters.Add(new InputParameter<string>("WebSearchGuidance", false, "Kommt zur System-Instruction, wenn das Modell suchen darf."));
            metadata.InputParameters.Add(new InputParameter<List<byte[]>>("Images", false, "PNG-Bilder, die zum Prompt gehören; sie stehen vor seinem Text."));

            metadata.OutputParameters.Add(new OutputParameter<string>(
                name: "Result",
                isAlwaysProvided: true,
                description: "Die rohe Antwort des Modells."
            ));
            metadata.OutputParameters.Add(new OutputParameter<List<WebSource>>(
                name: "WebSources",
                isAlwaysProvided: true,
                description: "Die Seiten, auf die sich die Antwort stützt: die zitierten, sonst alle gefundenen."
            ));
            metadata.OutputParameters.Add(new OutputParameter<List<string>>(
                name: "WebSearches",
                isAlwaysProvided: true,
                description: "Wonach das Modell gesucht hat."
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
            var images = GetInput<List<byte[]>>("Images");

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
            messages.Add(new ApiMessage { Role = "user", Content = UserContent(prompt, images) });

            var capabilities = await GetCapabilitiesAsync(httpClient, model, ct);

            // A failed lookup says nothing about the model; the request that follows reports what is wrong.
            var webSearches = GetOption<int>("WebSearchMaxUses");
            var schemaAllowsSearch = capabilities.StructuredOutput || string.IsNullOrEmpty(jsonSchema);
            if (webSearches > 0 && capabilities.Known && !(capabilities.WebSearch && schemaAllowsSearch))
                throw new WebSearchUnavailableException($"The model {model} cannot search the web.");

            var request = new ApiRequest
            {
                Model = model,
                MaxTokens = 64000,
                System = systemMessage,
                Messages = messages,
                // A safety classifier can decline harmless code requests; re-run those on the model Anthropic recommends.
                Fallbacks = capabilities.ServerSideFallback ? "default" : null
            };

            if (webSearches > 0 && schemaAllowsSearch)
            {
                request.Tools = [new ApiTool { Type = WebSearchTool, Name = "web_search", MaxUses = webSearches }];
                request.System += GetInput<string>("WebSearchGuidance");
            }

            // Only where the model knows this level: older ones answer the field with a 400.
            var effort = GetOption<string>("Effort");
            if (!string.IsNullOrEmpty(effort) && capabilities.EffortLevels?.Contains(effort) == true)
                request.OutputConfig = new ApiOutputConfig { Effort = effort };

            string rawJsonResponse;
            ApiResponse result;

            if (string.IsNullOrEmpty(jsonSchema))
            {
                // Plain text mode
                result = await PostUntilFinishedAsync(httpClient, request, timeout, ct);
                rawJsonResponse = AnswerText(result)
                    ?? throw new InvalidOperationException("No text response from Anthropic API.");
            }
            else if (capabilities.StructuredOutput)
            {
                // Only here the API guarantees the schema. A forced tool does not (Haiku 5.5 sent arrays
                // as strings), and Opus 5.5, Sonnet 5.5 and Fable 5.1 reject it with a 400.
                request.OutputConfig ??= new ApiOutputConfig();
                request.OutputConfig.Format = new ApiOutputFormat { Schema = JsonNode.Parse(jsonSchema) };

                result = await PostUntilFinishedAsync(httpClient, request, timeout, ct);
                ThrowIfCutOff(result);
                rawJsonResponse = AnswerText(result)
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

                result = await PostAsync(httpClient, request, timeout, ct);
                ThrowIfCutOff(result);
                var toolUse = result.Content.LastOrDefault(c => c.Type == "tool_use");
                rawJsonResponse = toolUse?.Input?.ToJsonString()
                    ?? throw new InvalidOperationException("No tool_use response from Anthropic API.");
            }

            SetOutput("Result", rawJsonResponse);
            SetOutput("WebSources", SourcesOf(result));
            SetOutput("WebSearches", SearchesOf(result));
        }

        // A picture goes before the text it belongs to; without one the prompt stays a plain text.
        private static JsonNode UserContent(string prompt, List<byte[]>? images)
        {
            if (images is not { Count: > 0 }) return prompt;
            var blocks = new JsonArray();
            foreach (var png in images)
                blocks.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = Convert.ToBase64String(png) },
                });
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = prompt });
            return blocks;
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
                    info?.AllowedFallbackModels is { Count: > 0 },
                    info?.Capabilities?.Effort?
                        .Where(level => level.Value.ValueKind == JsonValueKind.Object
                            && level.Value.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.True)
                        .Select(level => level.Key)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase),
                    info?.Capabilities?.ServerTools?.WebSearch?.Supported == true,
                    Known: true);
            }
            catch (HttpRequestException)
            {
                // The Messages request that follows reports what is really wrong.
                return default;
            }
        }

        // Always streams: an answer can take many minutes, and only silence should end the wait.
        private async Task<ApiResponse> PostAsync(HttpClient httpClient, ApiRequest request, TimeSpan silenceLimit, CancellationToken ct)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, ApiUrl) { Content = JsonContent.Create(request) };
            if (request.Fallbacks != null)
                message.Headers.Add("anthropic-beta", FallbackRequestBeta);

            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                if (response.StatusCode == HttpStatusCode.BadRequest && request.Tools?.Any(t => t.Type == WebSearchTool) == true && SaysSearchIsSwitchedOff(content))
                    throw new WebSearchUnavailableException("Web search is switched off for this Anthropic organization. An administrator can switch it on in the Claude Console.");
                throw new HttpRequestException($"Anthropic API error ({response.StatusCode}): {Truncate(content, 500)}");
            }

            var result = await ReadAnswerAsync(response, silenceLimit, ct);

            if (result.StopReason == "refusal")
                throw new InvalidOperationException($"Claude declined this request ({result.StopCategory ?? "no reason given"}). Rephrase the prompt or choose another model.");

            return result;
        }

        private static bool SaysSearchIsSwitchedOff(string error) =>
            error.Contains("not enabled", StringComparison.OrdinalIgnoreCase)
            && (error.Contains("web search", StringComparison.OrdinalIgnoreCase) || error.Contains("web_search", StringComparison.OrdinalIgnoreCase));

        // A long search can pause the turn. It goes on where it stopped when the paused part comes back
        // unchanged, and each request answers only with what is new.
        private async Task<ApiResponse> PostUntilFinishedAsync(HttpClient httpClient, ApiRequest request, TimeSpan silenceLimit, CancellationToken ct)
        {
            var whole = await PostAsync(httpClient, request, silenceLimit, ct);
            var part = whole;

            for (int pauses = 0; part.StopReason == "pause_turn" && pauses < MaxPauses; pauses++)
            {
                request.Messages.Add(new ApiMessage
                {
                    Role = "assistant",
                    Content = new JsonArray(part.Content.Select(block => (JsonNode?)block.Raw.DeepClone()).ToArray()),
                });

                part = await PostAsync(httpClient, request, silenceLimit, ct);
                whole.Content.AddRange(part.Content);
                whole.StopReason = part.StopReason;
                whole.StopCategory = part.StopCategory;
            }

            return whole;
        }

        private async Task<ApiResponse> ReadAnswerAsync(HttpResponseMessage response, TimeSpan silenceLimit, CancellationToken ct)
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
                            result.Content.Add(StartBlock(data?["content_block"]));
                            buffer.Clear();
                            break;

                        case "content_block_delta":
                            if (result.Content.LastOrDefault() is { } open)
                                ApplyDelta(open, data?["delta"], buffer);
                            break;

                        case "content_block_stop":
                            if (result.Content.LastOrDefault() is { } block)
                            {
                                FinishBlock(block, buffer.ToString());
                                if (block.Type == "server_tool_use" && (string?)block.Input?["query"] is { } query)
                                    WebSearchProgress?.Invoke(query);
                                else if (block.Type == "web_search_tool_result")
                                    WebSearchProgress?.Invoke(null);
                            }
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

        // The stream announces a block with empty fields, and a null in a block sent back is a 400.
        private static ApiContentBlock StartBlock(JsonNode? announced)
        {
            var raw = announced?.DeepClone() as JsonObject ?? new JsonObject();
            foreach (var empty in raw.Where(field => field.Value == null).Select(field => field.Key).ToList())
                raw.Remove(empty);

            return new ApiContentBlock { Type = (string?)raw["type"] ?? "", Raw = raw };
        }

        private static void ApplyDelta(ApiContentBlock block, JsonNode? delta, StringBuilder buffer)
        {
            switch ((string?)delta?["type"])
            {
                case "signature_delta":
                    block.Raw["signature"] = (string?)delta?["signature"];
                    break;

                case "citations_delta":
                    if (block.Raw["citations"] is not JsonArray citations)
                        block.Raw["citations"] = citations = new JsonArray();
                    citations.Add(delta?["citation"]?.DeepClone());
                    break;

                default:
                    // text_delta, input_json_delta or thinking_delta
                    buffer.Append((string?)delta?["text"] ?? (string?)delta?["partial_json"] ?? (string?)delta?["thinking"]);
                    break;
            }
        }

        private static void FinishBlock(ApiContentBlock block, string content)
        {
            switch (block.Type)
            {
                case "text":
                    block.Text = content;
                    block.Raw["text"] = content;
                    break;

                case "thinking":
                    block.Raw["thinking"] = content;
                    break;

                case "tool_use":
                case "server_tool_use":
                    // JSON cut off at max_tokens does not parse; ThrowIfCutOff reports that.
                    try
                    {
                        block.Input = JsonNode.Parse(content);
                        block.Raw["input"] = block.Input?.DeepClone();
                    }
                    catch (JsonException) { }
                    if (block.Raw["input"] == null)
                        block.Raw["input"] = new JsonObject();
                    break;
            }
        }

        // Thinking counts toward max_tokens on newer models; cut-off JSON must not pass as a finished answer.
        private static void ThrowIfCutOff(ApiResponse result)
        {
            if (result.StopReason == "max_tokens")
                throw new InvalidOperationException("Claude's answer reached the output limit and was cut off.");
            if (result.StopReason == "pause_turn")
                throw new InvalidOperationException("Claude's web search did not finish.");
        }

        // What comes after the last search: before it the model says what it is about to do ("I'll
        // search for ..."). All blocks together: a fallback model that takes over mid-answer continues the text.
        private static string? AnswerText(ApiResponse result)
        {
            var lastSearch = result.Content.FindLastIndex(c => c.Type == "web_search_tool_result");
            var texts = result.Content.Skip(lastSearch + 1).Where(c => c.Type == "text").Select(c => c.Text).ToList();
            return texts.Count > 0 ? string.Concat(texts) : null;
        }

        // The cited pages; an answer in a schema has no citations, so then the pages found.
        private static List<WebSource> SourcesOf(ApiResponse result)
        {
            var cited = result.Content.Where(c => c.Type == "text")
                .SelectMany(c => c.Raw["citations"] as JsonArray ?? [])
                .Where(citation => (string?)citation?["type"] == "web_search_result_location")
                .ToList();

            // After an error the content is one object instead of a list of pages.
            var found = result.Content.Where(c => c.Type == "web_search_tool_result")
                .SelectMany(c => c.Raw["content"] as JsonArray ?? []);

            return (cited.Count > 0 ? cited : found)
                .Select(page => (Url: (string?)page?["url"], Title: (string?)page?["title"]))
                .Where(page => !string.IsNullOrEmpty(page.Url))
                .DistinctBy(page => page.Url)
                .Select(page => new WebSource(string.IsNullOrEmpty(page.Title) ? page.Url! : page.Title, page.Url!))
                .ToList();
        }

        // Only what really ran. Past max_uses the model still tries, once with the query "placeholder".
        private static List<string> SearchesOf(ApiResponse result)
        {
            var failed = result.Content.Where(c => c.Type == "web_search_tool_result" && c.Raw["content"] is not JsonArray)
                .Select(c => (string?)c.Raw["tool_use_id"])
                .ToHashSet();

            return result.Content.Where(c => c.Type == "server_tool_use" && (string?)c.Raw["name"] == "web_search" && !failed.Contains((string?)c.Raw["id"]))
                .Select(c => (string?)c.Input?["query"])
                .OfType<string>()
                .ToList();
        }

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength] + "...";

        private readonly record struct ModelCapabilities(bool StructuredOutput, bool ServerSideFallback, IReadOnlySet<string>? EffortLevels, bool WebSearch, bool Known);

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

            // {"supported":true,"low":{"supported":true},…}
            [JsonPropertyName("effort")]
            public Dictionary<string, JsonElement>? Effort { get; set; }

            [JsonPropertyName("server_tools")]
            public ApiServerTools? ServerTools { get; set; }
        }

        private class ApiServerTools
        {
            [JsonPropertyName("web_search")]
            public ApiSupported? WebSearch { get; set; }
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
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public ApiOutputFormat? Format { get; set; }

            [JsonPropertyName("effort")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Effort { get; set; }
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

            // A text, or the blocks of a paused answer.
            [JsonPropertyName("content")]
            public JsonNode Content { get; set; } = "";
        }

        private class ApiTool
        {
            // Only a server tool has one; such a tool has no input schema.
            [JsonPropertyName("type")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Type { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; } = "";

            [JsonPropertyName("description")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Description { get; set; }

            [JsonPropertyName("input_schema")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public JsonNode? InputSchema { get; set; }

            [JsonPropertyName("max_uses")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public int? MaxUses { get; set; }
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

            // The block as the API sent it, for sending it back after a pause.
            public JsonObject Raw { get; set; } = new();
        }

        #endregion
    }
}
