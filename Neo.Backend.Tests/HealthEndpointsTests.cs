using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Neo.Backend.Tests;

public class HealthEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public HealthEndpointsTests(WebApplicationFactory<Program> f) => _factory = f;

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Providers_ReturnsArrayWithExpectedProviders()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/providers");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var arr = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);

        var ids = arr.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray();
        Assert.Contains("claude", ids);
        Assert.Contains("openai", ids);
        Assert.Contains("gemini", ids);

        foreach (var e in arr.EnumerateArray())
        {
            Assert.True(e.TryGetProperty("available", out _));
            Assert.True(e.TryGetProperty("envVar", out _));
            Assert.True(e.TryGetProperty("defaultModel", out _));
        }
    }
}
