namespace Neo.Backend.Services;

public sealed record Provider(string Id, string Name, string EnvVar, string DefaultBaseUrl, string DefaultModel);

public sealed class ProviderRegistry
{
    public static readonly IReadOnlyList<Provider> All = new[]
    {
        new Provider("claude",   "Anthropic Claude", "ANTHROPIC_API_KEY", "https://api.anthropic.com", "claude-opus-4-7"),
        new Provider("openai",   "OpenAI ChatGPT",   "OPENAI_API_KEY",    "https://api.openai.com",    "gpt-4o-mini"),
        new Provider("gemini",   "Google Gemini",    "GEMINI_API_KEY",    "https://generativelanguage.googleapis.com", "gemini-1.5-flash"),
        new Provider("ollama",   "Ollama (local)",   "OLLAMA_HOST",       "http://localhost:11434",   "llama3.1:latest"),
        new Provider("lmstudio", "LM Studio (local)","LMSTUDIO_HOST",     "http://localhost:1234",    "local-model"),
    };

    public Provider? Get(string id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public bool IsAvailable(Provider p)
    {
        var value = Environment.GetEnvironmentVariable(p.EnvVar);
        return !string.IsNullOrWhiteSpace(value);
    }

    public string? GetApiKey(Provider p) => Environment.GetEnvironmentVariable(p.EnvVar);

    public IEnumerable<object> Snapshot() => All.Select(p => new
    {
        id = p.Id,
        name = p.Name,
        envVar = p.EnvVar,
        available = IsAvailable(p),
        defaultModel = p.DefaultModel,
    });
}
