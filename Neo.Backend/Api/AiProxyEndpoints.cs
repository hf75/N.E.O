using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Neo.Backend.Services;

namespace Neo.Backend.Api;

public sealed record AiRequest(
    string Prompt,
    string? Model,
    int? MaxTokens,
    string? SystemPrompt,
    ChatTurn[]? History);

public sealed record ChatTurn(string Role, string Content);

public static class AiProxyEndpoints
{
    public static IEndpointRouteBuilder MapAiProxy(this IEndpointRouteBuilder app)
    {
        // Unified streaming endpoint: /api/ai/{providerId}/stream
        // Body: AiRequest
        // Response: text/event-stream — SSE events compatible with each provider's native stream shape.
        app.MapPost("/api/ai/{providerId}/stream", async (
            string providerId,
            HttpContext ctx,
            ProviderRegistry registry,
            IHttpClientFactory httpFactory) =>
        {
            var provider = registry.Get(providerId);
            if (provider is null)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new { error = $"unknown provider '{providerId}'" });
                return;
            }

            if (!registry.IsAvailable(provider))
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsJsonAsync(new { error = $"provider not configured — set env var {provider.EnvVar}" });
                return;
            }

            AiRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<AiRequest>(
                    ctx.Request.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "invalid JSON body" });
                return;
            }

            if (req is null || string.IsNullOrWhiteSpace(req.Prompt))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "prompt required" });
                return;
            }

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var http = httpFactory.CreateClient("ai");
            var apiKey = registry.GetApiKey(provider)!;

            try
            {
                await (provider.Id switch
                {
                    "claude" => StreamClaudeAsync(ctx, http, provider, apiKey, req),
                    "openai" => StreamOpenAiAsync(ctx, http, provider, apiKey, req),
                    "gemini" => StreamGeminiAsync(ctx, http, provider, apiKey, req),
                    "ollama" => StreamOpenAiLikeAsync(ctx, http, provider.DefaultBaseUrl, apiKey: "", req),
                    "lmstudio" => StreamOpenAiLikeAsync(ctx, http, provider.DefaultBaseUrl, apiKey: "", req),
                    _ => Task.CompletedTask
                });
            }
            catch (Exception ex)
            {
                await WriteSseAsync(ctx, "error", JsonSerializer.Serialize(new { message = ex.Message }));
            }
        });

        return app;
    }

    private static async Task StreamClaudeAsync(HttpContext ctx, HttpClient http, Provider provider, string apiKey, AiRequest req)
    {
        var messages = new List<object>();
        if (req.History is not null)
        {
            foreach (var h in req.History)
            {
                // Claude accepts "user" and "assistant" only.
                var role = h.Role == "assistant" ? "assistant" : "user";
                messages.Add(new { role, content = h.Content });
            }
        }
        messages.Add(new { role = "user", content = req.Prompt });

        var body = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrEmpty(req.Model) ? provider.DefaultModel : req.Model,
            ["max_tokens"] = req.MaxTokens ?? 2048,
            ["stream"] = true,
            ["messages"] = messages
        };
        if (!string.IsNullOrEmpty(req.SystemPrompt))
            body["system"] = req.SystemPrompt;

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{provider.DefaultBaseUrl}/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        msg.Headers.Add("x-api-key", apiKey);
        msg.Headers.Add("anthropic-version", "2023-06-01");

        await RelaySseAsync(ctx, http, msg);
    }

    private static async Task StreamOpenAiAsync(HttpContext ctx, HttpClient http, Provider provider, string apiKey, AiRequest req)
    {
        await StreamOpenAiLikeAsync(ctx, http, provider.DefaultBaseUrl, apiKey, req,
            defaultModel: provider.DefaultModel);
    }

    private static async Task StreamOpenAiLikeAsync(HttpContext ctx, HttpClient http, string baseUrl, string apiKey, AiRequest req, string? defaultModel = null)
    {
        var messages = new List<object>();
        if (!string.IsNullOrEmpty(req.SystemPrompt))
            messages.Add(new { role = "system", content = req.SystemPrompt });
        if (req.History is not null)
        {
            foreach (var h in req.History)
            {
                var role = h.Role == "assistant" ? "assistant" : "user";
                messages.Add(new { role, content = h.Content });
            }
        }
        messages.Add(new { role = "user", content = req.Prompt });

        var body = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrEmpty(req.Model) ? (defaultModel ?? "gpt-4o-mini") : req.Model,
            ["stream"] = true,
            ["messages"] = messages,
        };
        if (req.MaxTokens.HasValue) body["max_tokens"] = req.MaxTokens.Value;

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(apiKey))
            msg.Headers.Add("Authorization", $"Bearer {apiKey}");

        await RelaySseAsync(ctx, http, msg);
    }

    private static async Task StreamGeminiAsync(HttpContext ctx, HttpClient http, Provider provider, string apiKey, AiRequest req)
    {
        var model = string.IsNullOrEmpty(req.Model) ? provider.DefaultModel : req.Model;
        var url = $"{provider.DefaultBaseUrl}/v1beta/models/{model}:streamGenerateContent?alt=sse&key={apiKey}";

        var contents = new List<object>();
        if (!string.IsNullOrEmpty(req.SystemPrompt))
            contents.Add(new { role = "user", parts = new[] { new { text = req.SystemPrompt } } });
        if (req.History is not null)
        {
            foreach (var h in req.History)
            {
                // Gemini uses "user" and "model" roles.
                var role = h.Role == "assistant" ? "model" : "user";
                contents.Add(new { role, parts = new[] { new { text = h.Content } } });
            }
        }
        contents.Add(new { role = "user", parts = new[] { new { text = req.Prompt } } });

        var body = new
        {
            contents,
            generationConfig = new { maxOutputTokens = req.MaxTokens ?? 2048 }
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

        await RelaySseAsync(ctx, http, msg);
    }

    private static async Task RelaySseAsync(HttpContext ctx, HttpClient http, HttpRequestMessage msg)
    {
        var sw = Stopwatch.StartNew();
        long firstByteMs = -1;

        using var response = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ctx.RequestAborted);
            await WriteSseAsync(ctx, "error", JsonSerializer.Serialize(new { status = (int)response.StatusCode, body = errBody }));
            return;
        }

        using var upstream = await response.Content.ReadAsStreamAsync(ctx.RequestAborted);
        using var reader = new StreamReader(upstream, Encoding.UTF8);

        string? line;
        int events = 0;
        while ((line = await reader.ReadLineAsync(ctx.RequestAborted)) is not null)
        {
            if (firstByteMs < 0)
            {
                firstByteMs = sw.ElapsedMilliseconds;
                await WriteSseAsync(ctx, "meta", $"{{\"ttfb_ms\":{firstByteMs}}}");
            }

            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("data:"))
            {
                events++;
                await ctx.Response.WriteAsync(line + "\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
        }

        sw.Stop();
        await WriteSseAsync(ctx, "done",
            $"{{\"total_ms\":{sw.ElapsedMilliseconds},\"events\":{events}}}");
    }

    private static async Task WriteSseAsync(HttpContext ctx, string evt, string data)
    {
        await ctx.Response.WriteAsync($"event: {evt}\ndata: {data}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }
}
