using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace Neo.App.WebApp.Services.Compilation;

/// <summary>
/// Sends the requested packages to Neo.Backend's <c>/api/nuget/resolve</c>,
/// which delegates to the exact same resolver Neo desktop and the MCP
/// preview server use. The backend returns a ZIP with every DLL (including
/// transitive deps) — we expand it into a set of <see cref="MetadataReference"/>s.
/// </summary>
public sealed class NuGetResolver
{
    private readonly HttpClient _http;

    public NuGetResolver(HttpClient http) => _http = http;

    /// <summary>
    /// TFM reported to the server. Defaults to net9.0 because that's the
    /// closest cross-platform TFM the WASM runtime can consume.
    /// </summary>
    public string TargetFramework { get; set; } = "net9.0";

    public sealed class ResolveResult
    {
        /// <summary>Roslyn metadata for the compile step.</summary>
        public List<MetadataReference> References { get; } = new();
        /// <summary>Raw DLL bytes keyed by assembly simple name — fed to the plugin ALC at load time.</summary>
        public Dictionary<string, byte[]> AssemblyBytes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Errors { get; } = new();
    }

    /// <summary>
    /// Resolves NuGet packages expressed in Neo.AssemblyForge's wire format:
    /// each entry is a <c>"Id|Version"</c> string (version may be <c>"default"</c>
    /// or a version range).
    /// </summary>
    public async Task<ResolveResult> ResolveAsync(IEnumerable<string> packageSpecs)
    {
        var result = new ResolveResult();

        var pkgDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in packageSpecs)
        {
            if (string.IsNullOrWhiteSpace(spec)) continue;
            var parts = spec.Split('|', 2);
            var id = parts[0].Trim();
            if (string.IsNullOrEmpty(id)) continue;
            var version = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
                ? parts[1].Trim()
                : "default";
            pkgDict[id] = version;
        }
        if (pkgDict.Count == 0) return result;

        // Cache hit uses the composite (packages → ZIP bytes) dictionary —
        // we can't reuse partial caches because the resolver picks consistent
        // versions across the whole graph.
        var cacheKey = BuildCacheKey(pkgDict);
        if (_rawZipCache.TryGetValue(cacheKey, out var cachedZip))
        {
            ExpandZipInto(cachedZip, result);
            return result;
        }

        var body = JsonSerializer.Serialize(new { packages = pkgDict, targetFramework = TargetFramework });
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/nuget/resolve")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));

        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            result.Errors.Add($"NuGet resolve failed ({(int)resp.StatusCode}): {err}");
            return result;
        }

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        _rawZipCache[cacheKey] = bytes;

        ExpandZipInto(bytes, result);
        return result;
    }

    private readonly Dictionary<string, byte[]> _rawZipCache = new(StringComparer.OrdinalIgnoreCase);

    private static void ExpandZipInto(byte[] zipBytes, ResolveResult result)
    {
        using var ms = new MemoryStream(zipBytes, writable: false);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            using var es = entry.Open();
            using var buf = new MemoryStream();
            es.CopyTo(buf);
            var raw = buf.ToArray();
            try
            {
                result.References.Add(MetadataReference.CreateFromImage(raw));
                var simple = System.IO.Path.GetFileNameWithoutExtension(entry.FullName);
                result.AssemblyBytes[simple] = raw;
            }
            catch { /* skip unreadable entries (e.g. resource satellites) */ }
        }
    }

    private static string BuildCacheKey(Dictionary<string, string> packages)
    {
        var sb = new StringBuilder();
        foreach (var kv in packages.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(kv.Key.ToLowerInvariant()).Append('@').Append(kv.Value).Append(';');
        return sb.ToString();
    }
}
