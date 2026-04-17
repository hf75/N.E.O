using Neo.Backend.Services;

namespace Neo.Backend.Tests;

public class ProviderRegistryTests
{
    [Fact]
    public void All_ContainsExpectedProviders()
    {
        var ids = ProviderRegistry.All.Select(p => p.Id).ToArray();
        Assert.Contains("claude", ids);
        Assert.Contains("openai", ids);
        Assert.Contains("gemini", ids);
        Assert.Contains("ollama", ids);
        Assert.Contains("lmstudio", ids);
    }

    [Fact]
    public void Get_ReturnsProvider_CaseInsensitive()
    {
        var reg = new ProviderRegistry();
        Assert.NotNull(reg.Get("claude"));
        Assert.NotNull(reg.Get("CLAUDE"));
        Assert.NotNull(reg.Get("Claude"));
    }

    [Fact]
    public void Get_ReturnsNull_ForUnknown()
    {
        var reg = new ProviderRegistry();
        Assert.Null(reg.Get("xxx-not-a-provider"));
    }

    [Fact]
    public void IsAvailable_ReflectsEnvVar()
    {
        var reg = new ProviderRegistry();
        var envName = "NEO_TEST_FAKE_" + Guid.NewGuid().ToString("N");
        var fake = new Provider("fake", "Fake", envName, "http://localhost", "fake-model");

        Assert.False(reg.IsAvailable(fake));

        try
        {
            Environment.SetEnvironmentVariable(envName, "some-value");
            Assert.True(reg.IsAvailable(fake));
            Assert.Equal("some-value", reg.GetApiKey(fake));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    [Fact]
    public void IsAvailable_TreatsWhitespaceAsMissing()
    {
        var reg = new ProviderRegistry();
        var envName = "NEO_TEST_WS_" + Guid.NewGuid().ToString("N");
        var fake = new Provider("fake", "Fake", envName, "http://localhost", "fake-model");

        try
        {
            Environment.SetEnvironmentVariable(envName, "   ");
            Assert.False(reg.IsAvailable(fake));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    [Fact]
    public void Snapshot_ReportsAvailability()
    {
        var reg = new ProviderRegistry();
        var snap = reg.Snapshot().ToArray();
        Assert.Equal(ProviderRegistry.All.Count, snap.Length);
    }
}
