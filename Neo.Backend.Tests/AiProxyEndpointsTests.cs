using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Neo.Backend.Tests;

public class AiProxyEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public AiProxyEndpointsTests(WebApplicationFactory<Program> f) => _factory = f;

    [Fact]
    public async Task UnknownProvider_Returns404()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/ai/not-a-provider/stream",
            new StringContent("{\"prompt\":\"hi\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task MissingEnvVar_Returns503()
    {
        // Ensure env var is absent
        var savedClaude = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            // Need a fresh factory because env vars are read through ProviderRegistry at request time, that's fine.
            var client = _factory.CreateClient();
            var resp = await client.PostAsync("/api/ai/claude/stream",
                new StringContent("{\"prompt\":\"hi\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("ANTHROPIC_API_KEY", body);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", savedClaude);
        }
    }

    [Fact]
    public async Task EmptyPrompt_Returns400()
    {
        var saved = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "fake-key-for-test");
            var client = _factory.CreateClient();
            var resp = await client.PostAsync("/api/ai/claude/stream",
                new StringContent("{\"prompt\":\"\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", saved);
        }
    }

    [Fact]
    public async Task InvalidJson_Returns400()
    {
        var saved = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "fake-key-for-test");
            var client = _factory.CreateClient();
            var resp = await client.PostAsync("/api/ai/claude/stream",
                new StringContent("{ not valid json", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", saved);
        }
    }
}
