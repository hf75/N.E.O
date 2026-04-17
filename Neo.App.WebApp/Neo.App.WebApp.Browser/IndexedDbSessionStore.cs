using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using Neo.App.WebApp.Services.Sessions;

namespace Neo.App.WebApp.Browser;

/// <summary>
/// Stores .neo sessions in the browser's IndexedDB via the JavaScript helpers
/// registered in wwwroot/neo-storage.js.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed partial class IndexedDbSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    [JSImport("neo_storage_list", "neo-storage.js")]
    internal static partial Task<string> ListAsyncJs();

    [JSImport("neo_storage_get", "neo-storage.js")]
    internal static partial Task<string> GetAsyncJs(string name);

    [JSImport("neo_storage_put", "neo-storage.js")]
    internal static partial Task PutAsyncJs(string name, string json);

    [JSImport("neo_storage_delete", "neo-storage.js")]
    internal static partial Task DeleteAsyncJs(string name);

    private static async Task EnsureImportedAsync()
    {
        // Relative to the runtime loader base (wwwroot/_framework/), so ../
        // reaches wwwroot/ where neo-storage.js lives.
        await JSHost.ImportAsync("neo-storage.js", "../neo-storage.js");
    }

    public async Task<IReadOnlyList<string>> ListAsync()
    {
        await EnsureImportedAsync();
        var raw = await ListAsyncJs();
        if (string.IsNullOrEmpty(raw)) return Array.Empty<string>();
        return JsonSerializer.Deserialize<List<string>>(raw, Json) ?? new List<string>();
    }

    public async Task<NeoSession?> LoadAsync(string name)
    {
        await EnsureImportedAsync();
        var raw = await GetAsyncJs(name);
        if (string.IsNullOrEmpty(raw)) return null;
        return JsonSerializer.Deserialize<NeoSession>(raw, Json);
    }

    public async Task SaveAsync(NeoSession session)
    {
        await EnsureImportedAsync();
        var json = JsonSerializer.Serialize(session, Json);
        await PutAsyncJs(session.Name, json);
    }

    public async Task DeleteAsync(string name)
    {
        await EnsureImportedAsync();
        await DeleteAsyncJs(name);
    }
}
