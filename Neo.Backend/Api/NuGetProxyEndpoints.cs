using System.IO.Compression;
using System.Text.Json;
using Neo.AssemblyForge.Services;

namespace Neo.Backend.Api;

/// <summary>
/// Single NuGet endpoint that delegates to the same resolver Neo desktop and
/// Neo.McpServer use (<see cref="NuGetPackageService"/> backed by
/// <c>NuGetPackageLoaderAgent</c>). Does full transitive-dependency
/// resolution, TFM/RID matching, and hands the browser back a ZIP of all
/// DLLs it needs as Roslyn references.
/// </summary>
public sealed record NuGetResolveRequest(
    Dictionary<string, string> Packages,
    string? TargetFramework);

public static class NuGetProxyEndpoints
{
    public static IEndpointRouteBuilder MapNuGetProxy(this IEndpointRouteBuilder app)
    {
        // POST /api/nuget/resolve
        //   Body: { "packages": { "MathNet.Numerics": "5.0.0", "NodaTime": "default" },
        //           "targetFramework": "net9.0" }
        //   Response: application/zip — every entry is a .dll (flat).
        app.MapPost("/api/nuget/resolve", async (HttpContext ctx) =>
        {
            NuGetResolveRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<NuGetResolveRequest>(
                    ctx.Request.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "invalid JSON body" });
                return;
            }

            if (req is null || req.Packages is null || req.Packages.Count == 0)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "packages required" });
                return;
            }

            var tfm = string.IsNullOrWhiteSpace(req.TargetFramework) ? "net9.0" : req.TargetFramework;
            // Stable cache per TFM — persists across requests so second-time
            // resolves are instant. ~100 MB ceiling per TFM in practice.
            var cache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Neo.Backend", "nuget-cache", tfm);
            Directory.CreateDirectory(cache);

            try
            {
                var service = new NuGetPackageService(
                    nuGetPackageDirectory: cache,
                    targetFramework: tfm,
                    referenceAssemblyDirectories: Array.Empty<string>());

                var result = await service.LoadPackagesAsync(
                    req.Packages,
                    existingDlls: Array.Empty<string>(),
                    ctx.RequestAborted);

                // Zip every DLL the resolver produced (root + transitive).
                // Kestrel disallows synchronous I/O; ZipArchive writes its
                // central directory synchronously, so we build the ZIP in a
                // MemoryStream first and then stream the bytes out async.
                using var ms = new MemoryStream();
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var dll in result.DllPaths)
                    {
                        var name = Path.GetFileName(dll);
                        if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                        using var entryStream = entry.Open();
                        using var file = File.OpenRead(dll);
                        await file.CopyToAsync(entryStream, ctx.RequestAborted);
                    }
                }
                ms.Position = 0;

                ctx.Response.ContentType = "application/zip";
                ctx.Response.ContentLength = ms.Length;
                ctx.Response.Headers["X-Neo-Package-Count"] = result.PackageVersions.Count.ToString();
                ctx.Response.Headers["X-Neo-Dll-Count"] = result.DllPaths.Count.ToString();
                await ms.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            }
            // Intentionally NO finally cleanup — cache is shared across requests.
        });

        return app;
    }
}
