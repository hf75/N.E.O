using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Neo.App
{
    public static class ModelListService
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        public static async Task<List<string>> FetchAnthropicModelsAsync(string apiKey)
        {
            try
            {
                using var http = new HttpClient { Timeout = RequestTimeout };
                http.DefaultRequestHeaders.Add("x-api-key", apiKey);
                http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

                var json = await http.GetStringAsync("https://api.anthropic.com/v1/models");
                using var doc = JsonDocument.Parse(json);

                var models = new List<string>();
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var id))
                            models.Add(id.GetString()!);
                    }
                }

                models.Sort();
                return models;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModelListService] Failed to fetch Anthropic models: {ex.Message}");
                return new List<string>();
            }
        }

        public static async Task<List<string>> FetchOpenAiModelsAsync(string apiKey)
        {
            try
            {
                using var http = new HttpClient { Timeout = RequestTimeout };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var json = await http.GetStringAsync("https://api.openai.com/v1/models");
                using var doc = JsonDocument.Parse(json);

                var models = new List<string>();
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var id))
                        {
                            var modelId = id.GetString()!;
                            if (modelId.StartsWith("gpt-") ||
                                modelId.StartsWith("o1-") ||
                                modelId.StartsWith("o3-") ||
                                modelId.StartsWith("o4-"))
                            {
                                models.Add(modelId);
                            }
                        }
                    }
                }

                models.Sort();
                return models;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModelListService] Failed to fetch OpenAI models: {ex.Message}");
                return new List<string>();
            }
        }

        public static async Task<List<string>> FetchGeminiModelsAsync(string apiKey)
        {
            try
            {
                using var http = new HttpClient { Timeout = RequestTimeout };

                var json = await http.GetStringAsync(
                    $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}");
                using var doc = JsonDocument.Parse(json);

                var models = new List<string>();
                if (doc.RootElement.TryGetProperty("models", out var modelsArray))
                {
                    foreach (var item in modelsArray.EnumerateArray())
                    {
                        // Only include models that support content generation
                        if (item.TryGetProperty("supportedGenerationMethods", out var methods))
                        {
                            bool supportsGenerate = false;
                            foreach (var method in methods.EnumerateArray())
                            {
                                if (method.GetString() == "generateContent")
                                {
                                    supportsGenerate = true;
                                    break;
                                }
                            }
                            if (!supportsGenerate) continue;
                        }

                        if (item.TryGetProperty("name", out var name))
                        {
                            var modelName = name.GetString()!;
                            // Strip "models/" prefix
                            if (modelName.StartsWith("models/"))
                                modelName = modelName.Substring("models/".Length);
                            models.Add(modelName);
                        }
                    }
                }

                models.Sort();
                return models;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModelListService] Failed to fetch Gemini models: {ex.Message}");
                return new List<string>();
            }
        }
        public static async Task<List<string>> FetchOllamaModelsAsync(string endpoint)
        {
            try
            {
                using var http = new HttpClient { Timeout = RequestTimeout };
                var url = endpoint.TrimEnd('/') + "/models";
                var json = await http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                var models = new List<string>();
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var id))
                            models.Add(id.GetString()!);
                    }
                }

                models.Sort();
                return models;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModelListService] Failed to fetch Ollama models: {ex.Message}");
                return new List<string>();
            }
        }

        public static async Task<List<string>> FetchLmStudioModelsAsync(string endpoint)
        {
            try
            {
                using var http = new HttpClient { Timeout = RequestTimeout };
                var url = endpoint.TrimEnd('/') + "/models";
                var json = await http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                var models = new List<string>();
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var id))
                            models.Add(id.GetString()!);
                    }
                }

                models.Sort();
                return models;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModelListService] Failed to fetch LM Studio models: {ex.Message}");
                return new List<string>();
            }
        }

    }
}
