using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Neo.Agents.Core;

namespace Neo.Agents
{
    /// <summary>
    /// Agent for AI image generation via the Google Gemini API.
    /// Supports text-to-image and image editing (with reference image).
    /// Uses the Gemini REST API directly (responseModalities: IMAGE).
    /// </summary>
    public class GeminiImageGenAgent : AgentBase, IAppIntegratedAgent
    {
        private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

        public override string Name => "GeminiImageGenAgent";

        protected override AgentMetadata CreateMetadata()
        {
            var metadata = new AgentMetadata
            {
                Name = Name,
                Description = "Generates images using the Google Gemini API. " +
                              "Supports text-to-image and image-to-image editing."
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
                defaultValue: "gemini-3.1-flash-image-preview",
                description: "Gemini model with image generation support."
            ));

            metadata.Options.Add(new Option<float>(
                name: "Temperature",
                isRequired: false,
                defaultValue: 1.0f,
                description: "Sampling temperature (0.0 - 2.0). Higher values produce more creative results."
            ));

            metadata.Options.Add(new Option<float>(
                name: "TopP",
                isRequired: false,
                defaultValue: 0.95f,
                description: "Top-P for nucleus sampling."
            ));

            metadata.Options.Add(new Option<int>(
                name: "TimeoutSeconds",
                isRequired: false,
                defaultValue: 120,
                description: "HTTP request timeout in seconds."
            ));

            // --- Inputs ---

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "Prompt",
                isRequired: true,
                description: "Text description of the desired image."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "AspectRatio",
                isRequired: false,
                description: "Aspect ratio of the generated image. " +
                             "Supported values: '1:1', '16:9', '9:16', '4:3', '3:4'."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "SystemInstruction",
                isRequired: false,
                description: "Optional system instruction for style guidance (e.g. 'photorealistic', 'watercolor painting')."
            ));

            metadata.InputParameters.Add(new InputParameter<byte[]>(
                name: "ReferenceImage",
                isRequired: false,
                description: "Optional reference image bytes for image-to-image editing."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "ReferenceImageMimeType",
                isRequired: false,
                description: "MIME type of the reference image (e.g. 'image/png', 'image/jpeg'). " +
                             "Required when ReferenceImage is provided."
            ));

            // --- Outputs ---

            metadata.OutputParameters.Add(new OutputParameter<byte[]>(
                name: "ImageBytes",
                isAlwaysProvided: true,
                description: "The generated image as raw bytes."
            ));

            metadata.OutputParameters.Add(new OutputParameter<string>(
                name: "MimeType",
                isAlwaysProvided: true,
                description: "MIME type of the generated image (e.g. 'image/png')."
            ));

            metadata.OutputParameters.Add(new OutputParameter<string>(
                name: "TextResponse",
                isAlwaysProvided: false,
                description: "Optional text returned alongside the image."
            ));

            return metadata;
        }

        public override void ValidateOptionsAndInputs()
        {
            if (string.IsNullOrWhiteSpace(GetOption<string>("ApiKey")))
                throw new ArgumentException("Option 'ApiKey' must not be empty.");

            if (string.IsNullOrWhiteSpace(GetInput<string>("Prompt")))
                throw new ArgumentException("Input 'Prompt' must not be empty.");

            var aspectRatio = GetInput<string>("AspectRatio");
            if (!string.IsNullOrEmpty(aspectRatio))
            {
                var allowed = new[] { "1:1", "16:9", "9:16", "4:3", "3:4" };
                if (!allowed.Contains(aspectRatio))
                    throw new ArgumentException(
                        $"AspectRatio '{aspectRatio}' is not supported. Allowed: {string.Join(", ", allowed)}");
            }

            var refImage = GetInput<byte[]>("ReferenceImage");
            if (refImage != null && refImage.Length > 0)
            {
                var refMime = GetInput<string>("ReferenceImageMimeType");
                if (string.IsNullOrWhiteSpace(refMime))
                    throw new ArgumentException(
                        "Input 'ReferenceImageMimeType' is required when 'ReferenceImage' is provided.");
            }

            int timeout = GetOption<int>("TimeoutSeconds");
            if (timeout < 0)
                throw new ArgumentException("Option 'TimeoutSeconds' must not be negative.");
        }

        public override async Task ExecuteAsync(CancellationToken? cancellationToken = null)
        {
            ValidateOptionsAndInputs();

            var apiKey = GetOption<string>("ApiKey");
            var model = GetOption<string>("Model");
            var temperature = GetOption<float>("Temperature");
            var topP = GetOption<float>("TopP");
            var timeoutSeconds = GetOption<int>("TimeoutSeconds");
            var prompt = GetInput<string>("Prompt");
            var aspectRatio = GetInput<string>("AspectRatio");
            var systemInstruction = GetInput<string>("SystemInstruction");
            var referenceImage = GetInput<byte[]>("ReferenceImage");
            var referenceImageMimeType = GetInput<string>("ReferenceImageMimeType");
            var ct = cancellationToken ?? CancellationToken.None;

            // Build the user parts list (images first, then text prompt)
            var userParts = new List<GeminiPart>();

            // Reference image (for editing / image-to-image)
            if (referenceImage != null && referenceImage.Length > 0)
            {
                userParts.Add(new GeminiPart
                {
                    InlineData = new GeminiInlineData
                    {
                        MimeType = referenceImageMimeType ?? "image/png",
                        Data = Convert.ToBase64String(referenceImage)
                    }
                });
            }

            // Text prompt
            userParts.Add(new GeminiPart { Text = prompt });

            // Build generation config
            var generationConfig = new GeminiGenerationConfig
            {
                ResponseModalities = ["TEXT", "IMAGE"],
                Temperature = temperature,
                TopP = topP
            };

            if (!string.IsNullOrEmpty(aspectRatio))
            {
                generationConfig.ImageConfig = new GeminiImageConfig
                {
                    AspectRatio = aspectRatio
                };
            }

            // Build request body
            var requestBody = new GeminiRequest
            {
                Contents = [new GeminiContent { Role = "user", Parts = userParts }],
                GenerationConfig = generationConfig
            };

            // Add system instruction if provided
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

            // Extract image and optional text from response
            byte[]? imageBytes = null;
            string? imageMimeType = null;
            string? textResponse = null;

            if (geminiResponse?.Candidates is { Count: > 0 })
            {
                var parts = geminiResponse.Candidates[0].Content?.Parts;
                if (parts != null)
                {
                    foreach (var part in parts)
                    {
                        if (part.InlineData is { Data: not null } &&
                            part.InlineData.MimeType?.StartsWith("image/") == true)
                        {
                            imageBytes = Convert.FromBase64String(part.InlineData.Data);
                            imageMimeType = part.InlineData.MimeType;
                        }
                        else if (!string.IsNullOrWhiteSpace(part.Text))
                        {
                            textResponse = part.Text;
                        }
                    }
                }
            }

            if (imageBytes == null || imageBytes.Length == 0)
            {
                throw new InvalidOperationException(
                    "The Gemini API did not return an image. Response: " + Truncate(responseContent, 500));
            }

            SetOutput("ImageBytes", imageBytes);
            SetOutput("MimeType", imageMimeType ?? "image/png");
            if (!string.IsNullOrWhiteSpace(textResponse))
                SetOutput("TextResponse", textResponse);
        }

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength] + "...";

        // ── IAppIntegratedAgent ────────────────────────────────────────

        public string DisplayName => "Image Generation";
        public string SettingsKey => "ImageGen";
        public string? RequiredEnvVar => "GEMINI_API_KEY";
        public string DefaultModel => "gemini-3.1-flash-image-preview";

        public string? HelperTemplateCode =>
            AgentResourceLoader.LoadEmbeddedResource(typeof(GeminiImageGenAgent), "ImageGenHelper.cs");

        public IReadOnlyDictionary<string, string> TemplatePlaceholders { get; } =
            new Dictionary<string, string> { ["IMAGEGEN_MODEL_PLACEHOLDER"] = "ImageGen" };

        public string AgentDllName => "Neo.Agents.GeminiImageGen.dll";

        public string? SystemMessageDocs =>
            @"You also have access to AI image generation in the 'Neo.App' namespace:

            // Generate an image from a text description. Returns PNG bytes.
            // aspectRatio: '1:1', '16:9', '9:16', '4:3', '3:4' (optional)
            // systemInstruction: style guidance e.g. 'photorealistic', 'watercolor painting' (optional)
            public static async Task<byte[]> AIImageGen.GenerateImageAsync(string prompt, string? aspectRatio = null, string? systemInstruction = null, CancellationToken cancellationToken = default)

            // Edit an existing image based on a text prompt. Returns modified PNG bytes.
            public static async Task<byte[]> AIImageGen.EditImageAsync(string prompt, byte[] referenceImage, string referenceImageMimeType = ""image/png"", string? aspectRatio = null, string? systemInstruction = null, CancellationToken cancellationToken = default)

            When the user asks you to generate, create, or display an image, ALWAYS use AIImageGen.GenerateImageAsync (or EditImageAsync for modifications).
            Convert the returned byte[] to a BitmapImage via MemoryStream for display in an Image control.
            Never use stock photo URLs, placeholder images, or web scraping for image generation — always use AIImageGen.
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

                        if (!modelName.Contains("image", StringComparison.OrdinalIgnoreCase))
                            continue;

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
                Debug.WriteLine($"[GeminiImageGenAgent] Failed to fetch models: {ex.Message}");
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
            public List<string> ResponseModalities { get; set; } = ["TEXT", "IMAGE"];

            [JsonPropertyName("temperature")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public float? Temperature { get; set; }

            [JsonPropertyName("topP")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public float? TopP { get; set; }

            [JsonPropertyName("imageConfig")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public GeminiImageConfig? ImageConfig { get; set; }
        }

        private class GeminiImageConfig
        {
            [JsonPropertyName("aspectRatio")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? AspectRatio { get; set; }
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
