using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Neo.App.WebApp.Services.Ai;

public sealed class AiProviderInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string EnvVar { get; init; } = "";
    public bool Available { get; init; }
    public string DefaultModel { get; init; } = "";
}

public sealed class AiChunk
{
    public string Kind { get; init; } = ""; // "text", "meta", "done", "error"
    public string Data { get; init; } = "";
}

/// <summary>Wire-format chat turn passed to the backend as conversation history.</summary>
public sealed record ChatTurn(string Role, string Content);

/// <summary>
/// Browser-side client for Neo.Backend's AI proxy. Streams SSE events and
/// yields text chunks as they arrive.
/// </summary>
public sealed class AiClient
{
    private readonly HttpClient _http;
    public AiClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<AiProviderInfo>> GetProvidersAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("api/providers", ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var list = JsonSerializer.Deserialize<List<AiProviderInfo>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return list ?? new List<AiProviderInfo>();
    }

    public async IAsyncEnumerable<AiChunk> StreamAsync(
        string providerId,
        string prompt,
        string? model = null,
        string? systemPrompt = null,
        int? maxTokens = null,
        IReadOnlyList<ChatTurn>? history = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            prompt,
            model,
            systemPrompt,
            maxTokens,
            history,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"api/ai/{providerId}/stream")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            yield return new AiChunk { Kind = "error", Data = err };
            yield break;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? currentEvent = null;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                currentEvent = null;
                continue;
            }
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                currentEvent = line.Substring(6).Trim();
                continue;
            }
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var data = line.Substring(5).Trim();
                var kind = currentEvent ?? "data";
                if (kind == "done")
                {
                    yield return new AiChunk { Kind = "done", Data = data };
                    yield break;
                }
                if (kind == "error")
                {
                    yield return new AiChunk { Kind = "error", Data = data };
                    yield break;
                }
                if (kind == "meta")
                {
                    yield return new AiChunk { Kind = "meta", Data = data };
                    continue;
                }
                // Data event with provider-specific payload — try to extract text.
                var text = ExtractDelta(providerId, data);
                if (!string.IsNullOrEmpty(text))
                    yield return new AiChunk { Kind = "text", Data = text };
            }
        }
    }

    private static string? ExtractDelta(string provider, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (provider == "claude")
            {
                // content_block_delta -> delta.text
                if (root.TryGetProperty("type", out var type) &&
                    type.GetString() == "content_block_delta" &&
                    root.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("text", out var text))
                    return text.GetString();
            }
            else if (provider == "gemini")
            {
                // candidates[0].content.parts[0].text
                if (root.TryGetProperty("candidates", out var cands) &&
                    cands.ValueKind == JsonValueKind.Array && cands.GetArrayLength() > 0)
                {
                    var first = cands[0];
                    if (first.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts) &&
                        parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() > 0 &&
                        parts[0].TryGetProperty("text", out var t))
                        return t.GetString();
                }
            }
            else
            {
                // OpenAI, Ollama, LMStudio: choices[0].delta.content
                if (json == "[DONE]") return null;
                if (root.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("content", out var c))
                        return c.GetString();
                }
            }
        }
        catch
        {
            // Malformed events — skip silently.
        }
        return null;
    }
}
