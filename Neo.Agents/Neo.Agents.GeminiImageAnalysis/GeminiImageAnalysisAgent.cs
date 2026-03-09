using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Neo.Agents.Core;

namespace Neo.Agents
{
    /// <summary>
    /// Agent for AI image analysis via the Google Gemini API.
    /// Sends an image with a text prompt and returns the AI's text analysis.
    /// Supports image description, OCR, data extraction, and custom analysis tasks.
    /// </summary>
    public class GeminiImageAnalysisAgent : AgentBase, IAppIntegratedAgent
    {
        private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

        public override string Name => "GeminiImageAnalysisAgent";

        protected override AgentMetadata CreateMetadata()
        {
            var metadata = new AgentMetadata
            {
                Name = Name,
                Description = "Analyzes images using the Google Gemini API. " +
                              "Supports image description, OCR, and custom analysis tasks."
            };

            // --- Options ---

            metadata.Options.Add(new Option<string>(
                name: "ApiKey",
                isRequired: true,
                defaultValue: null!,
                description: "Google AI API key."
            ));

            metadata.Options.Add(new Option<string>(
                name: "Model",
                isRequired: false,
                defaultValue: "gemini-3-flash-preview",
                description: "Gemini model with vision support."
            ));

            metadata.Options.Add(new Option<int>(
                name: "TimeoutSeconds",
                isRequired: false,
                defaultValue: 60,
                description: "HTTP request timeout in seconds."
            ));

            // --- Inputs ---

            metadata.InputParameters.Add(new InputParameter<byte[]>(
                name: "ImageData",
                isRequired: true,
                description: "Image content as byte array."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "ImageMimeType",
                isRequired: false,
                description: "MIME type of the image (e.g. 'image/png', 'image/jpeg'). Default: 'image/png'."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "Prompt",
                isRequired: false,
                description: "Analysis instruction. Default: 'Describe this image in detail.'"
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "SystemInstruction",
                isRequired: false,
                description: "Optional system prompt for specific tasks (e.g. 'Extract all text from this image.')."
            ));

            // --- Outputs ---

            metadata.OutputParameters.Add(new OutputParameter<string>(
                name: "AnalysisText",
                isAlwaysProvided: true,
                description: "The text analysis result from the AI."
            ));

            return metadata;
        }

        public override void ValidateOptionsAndInputs()
        {
            if (string.IsNullOrWhiteSpace(GetOption<string>("ApiKey")))
                throw new ArgumentException("Option 'ApiKey' must not be empty.");

            var imageData = GetInput<byte[]>("ImageData");
            if (imageData == null || imageData.Length == 0)
                throw new ArgumentException("Input 'ImageData' must not be empty.");

            int timeout = GetOption<int>("TimeoutSeconds");
            if (timeout < 0)
                throw new ArgumentException("Option 'TimeoutSeconds' must not be negative.");
        }

        public override async Task ExecuteAsync(CancellationToken? cancellationToken = null)
        {
            ValidateOptionsAndInputs();

            var apiKey = GetOption<string>("ApiKey");
            var model = GetOption<string>("Model");
            var timeoutSeconds = GetOption<int>("TimeoutSeconds");
            var imageData = GetInput<byte[]>("ImageData");
            var imageMimeType = GetInput<string>("ImageMimeType");
            var prompt = GetInput<string>("Prompt");
            var systemInstruction = GetInput<string>("SystemInstruction");
            var ct = cancellationToken ?? CancellationToken.None;

            if (string.IsNullOrWhiteSpace(imageMimeType))
                imageMimeType = "image/png";
            if (string.IsNullOrWhiteSpace(prompt))
                prompt = "Describe this image in detail.";

            // Build user parts: image first, then text prompt
            var userParts = new List<GeminiPart>
            {
                new GeminiPart
                {
                    InlineData = new GeminiInlineData
                    {
                        MimeType = imageMimeType,
                        Data = Convert.ToBase64String(imageData)
                    }
                },
                new GeminiPart { Text = prompt }
            };

            // Build request body
            var requestBody = new GeminiRequest
            {
                Contents = [new GeminiContent { Role = "user", Parts = userParts }],
                GenerationConfig = new GeminiGenerationConfig
                {
                    ResponseModalities = ["TEXT"]
                }
            };

            if (!string.IsNullOrWhiteSpace(systemInstruction))
            {
                requestBody.SystemInstruction = new GeminiContent
                {
                    Parts = [new GeminiPart { Text = systemInstruction }]
                };
            }

            // Send request
            var timeout = timeoutSeconds == 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds(timeoutSeconds);

            using var httpClient = new HttpClient { Timeout = timeout };
            var url = $"{BaseUrl}/{model}:generateContent?key={apiKey}";

            var response = await httpClient.PostAsJsonAsync(url, requestBody, ct);
            var responseContent = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Gemini API error ({response.StatusCode}): {Truncate(responseContent, 500)}");
            }

            var geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(responseContent);

            // Extract text from response
            string? analysisText = null;

            if (geminiResponse?.Candidates is { Count: > 0 })
            {
                var parts = geminiResponse.Candidates[0].Content?.Parts;
                if (parts != null)
                {
                    foreach (var part in parts)
                    {
                        if (!string.IsNullOrWhiteSpace(part.Text))
                        {
                            analysisText = (analysisText == null)
                                ? part.Text
                                : analysisText + part.Text;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(analysisText))
            {
                throw new InvalidOperationException(
                    "The Gemini API did not return a text response. Response: " + Truncate(responseContent, 500));
            }

            SetOutput("AnalysisText", analysisText);
        }

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength] + "...";

        // ── IAppIntegratedAgent ────────────────────────────────────────

        public string DisplayName => "Image Analysis";
        public string SettingsKey => "ImageAnalysis";
        public string? RequiredEnvVar => "GEMINI_API_KEY";
        public string DefaultModel => "gemini-3-flash-preview";

        public string? HelperTemplateCode =>
            AgentResourceLoader.LoadEmbeddedResource(typeof(GeminiImageAnalysisAgent), "ImageAnalysisHelper.cs");

        public IReadOnlyDictionary<string, string> TemplatePlaceholders { get; } =
            new Dictionary<string, string> { ["IMAGE_ANALYSIS_MODEL_PLACEHOLDER"] = "ImageAnalysis" };

        public string AgentDllName => "Neo.Agents.GeminiImageAnalysis.dll";

        public string? SystemMessageDocs =>
            @"You also have access to AI image analysis in the 'Neo.App' namespace:

            // Analyze an image — returns a text description or analysis result.
            // prompt: analysis instruction (e.g. 'What objects are in this image?', 'Describe the colors and mood')
            // imageMimeType: 'image/png', 'image/jpeg', 'image/webp', 'image/gif'
            // systemInstruction: optional style/focus guidance (e.g. 'You are an art critic', 'Focus on text content only')
            public static async Task<string> AIImageAnalysis.AnalyzeAsync(byte[] imageData, string prompt = ""Describe this image in detail."", string imageMimeType = ""image/png"", string? systemInstruction = null, CancellationToken cancellationToken = default)

            // Extract text from an image (OCR). Returns all visible text.
            public static async Task<string> AIImageAnalysis.ExtractTextAsync(byte[] imageData, string imageMimeType = ""image/png"", CancellationToken cancellationToken = default)

            When the user asks to analyze, describe, read, or extract text from an image, screenshot, or photo, ALWAYS use AIImageAnalysis.
            Use AnalyzeAsync for general image understanding (descriptions, object detection, scene analysis).
            Use ExtractTextAsync for OCR (reading text from images, screenshots, documents).
            To capture screenshots, use System.Drawing (add NuGet package System.Drawing.Common|default).
            To load images from files, use File.ReadAllBytes() or Image controls.
            Never use external OCR services or web APIs — always use AIImageAnalysis.
            ";

        public async Task<List<string>> FetchAvailableModelsAsync(string? apiKeyOrEndpoint)
        {
            if (string.IsNullOrWhiteSpace(apiKeyOrEndpoint))
                return new List<string>();

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var json = await http.GetStringAsync(
                    $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKeyOrEndpoint}");
                using var doc = JsonDocument.Parse(json);

                var models = new List<string>();
                if (doc.RootElement.TryGetProperty("models", out var modelsArray))
                {
                    foreach (var item in modelsArray.EnumerateArray())
                    {
                        if (!item.TryGetProperty("name", out var name)) continue;
                        var modelName = name.GetString()!;
                        if (modelName.StartsWith("models/"))
                            modelName = modelName.Substring("models/".Length);

                        // Only include models that support content generation
                        if (item.TryGetProperty("supportedGenerationMethods", out var methods))
                        {
                            bool supportsGenerate = false;
                            foreach (var method in methods.EnumerateArray())
                            {
                                if (method.GetString() == "generateContent")
                                { supportsGenerate = true; break; }
                            }
                            if (!supportsGenerate) continue;
                        }

                        models.Add(modelName);
                    }
                }

                models.Sort();
                return models;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GeminiImageAnalysisAgent] Failed to fetch models: {ex.Message}");
                return new List<string>();
            }
        }

        #region Gemini REST API DTOs

        private class GeminiRequest
        {
            [JsonPropertyName("contents")]
            public List<GeminiContent> Contents { get; set; } = [];

            [JsonPropertyName("generationConfig")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public GeminiGenerationConfig? GenerationConfig { get; set; }

            [JsonPropertyName("system_instruction")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public GeminiContent? SystemInstruction { get; set; }
        }

        private class GeminiContent
        {
            [JsonPropertyName("role")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Role { get; set; }

            [JsonPropertyName("parts")]
            public List<GeminiPart> Parts { get; set; } = [];
        }

        private class GeminiPart
        {
            [JsonPropertyName("text")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Text { get; set; }

            [JsonPropertyName("inlineData")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public GeminiInlineData? InlineData { get; set; }
        }

        private class GeminiInlineData
        {
            [JsonPropertyName("mimeType")]
            public string MimeType { get; set; } = string.Empty;

            [JsonPropertyName("data")]
            public string Data { get; set; } = string.Empty;
        }

        private class GeminiGenerationConfig
        {
            [JsonPropertyName("responseModalities")]
            public List<string> ResponseModalities { get; set; } = ["TEXT"];
        }

        private class GeminiResponse
        {
            [JsonPropertyName("candidates")]
            public List<GeminiCandidate>? Candidates { get; set; }
        }

        private class GeminiCandidate
        {
            [JsonPropertyName("content")]
            public GeminiContent? Content { get; set; }
        }

        #endregion
    }
}
