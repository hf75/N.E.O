using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Neo.App.WebApp.Services.Compilation;

public sealed class CompileResult
{
    public bool Success { get; init; }
    public byte[]? AssemblyBytes { get; init; }
    public byte[]? PdbBytes { get; init; }
    public string[] Diagnostics { get; init; } = Array.Empty<string>();
    public long CompileTimeMs { get; init; }
    public long AssemblySize { get; init; }
}

/// <summary>
/// Compiles C# source to a .NET assembly entirely inside the browser via Roslyn.
/// Reference assemblies are resolved lazily from the WASM runtime's _framework
/// folder, guided by blazor.boot.json.
/// </summary>
public sealed class WasmCompiler
{
    private readonly HttpClient _http;
    private List<MetadataReference>? _references;
    private Dictionary<string, string>? _bootManifest;

    /// <summary>
    /// Base set of reference assemblies for an Avalonia UserControl plugin.
    /// Missing entries are silently skipped so the same list works across
    /// runtime package variants.
    /// </summary>
    public List<string> ReferenceAssemblyNames { get; } = new()
    {
        // Core BCL
        "System.Private.CoreLib",
        "System.Runtime",
        "System.Console",
        "System.Collections",
        "System.Collections.Concurrent",
        "System.Linq",
        "System.ObjectModel",
        "System.ComponentModel",
        "System.ComponentModel.Primitives",
        "System.Net.Http",
        "System.Text.Json",
        "System.Threading",
        "System.Threading.Tasks",
        "netstandard",
        // Avalonia
        "Avalonia.Base",
        "Avalonia.Controls",
        "Avalonia.Layout",
        "Avalonia.Interactivity",
        "Avalonia.Visuals",
        "Avalonia.Markup",
    };

    public WasmCompiler(HttpClient http) => _http = http;

    private async Task<Dictionary<string, string>> GetBootManifestAsync()
    {
        if (_bootManifest is not null) return _bootManifest;

        var json = await _http.GetStringAsync("_framework/blazor.boot.json");
        using var doc = JsonDocument.Parse(json);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (doc.RootElement.TryGetProperty("resources", out var resources))
        {
            foreach (var prop in resources.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var res in prop.Value.EnumerateObject())
                {
                    var fileName = res.Name;
                    var baseName = StripFingerprintAndExt(fileName);
                    if (baseName is not null && !map.ContainsKey(baseName))
                        map[baseName] = fileName;
                }
            }
        }

        _bootManifest = map;
        return map;
    }

    private static string? StripFingerprintAndExt(string fileName)
    {
        if (!fileName.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase) &&
            !fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return null;

        var dot = fileName.LastIndexOf('.');
        var noExt = fileName.Substring(0, dot);

        var lastDot = noExt.LastIndexOf('.');
        if (lastDot > 0)
        {
            var tail = noExt.Substring(lastDot + 1);
            if (tail.Length >= 8 && tail.Length <= 16 &&
                tail.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
                return noExt.Substring(0, lastDot);
        }
        return noExt;
    }

    public async Task<IReadOnlyList<MetadataReference>> GetReferencesAsync()
    {
        if (_references is not null) return _references;

        var manifest = await GetBootManifestAsync();
        var refs = new List<MetadataReference>();

        foreach (var name in ReferenceAssemblyNames)
        {
            if (!manifest.TryGetValue(name, out var fileName)) continue;
            try
            {
                var bytes = await _http.GetByteArrayAsync($"_framework/{fileName}");
                refs.Add(MetadataReference.CreateFromImage(bytes));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WasmCompiler] Failed to fetch {fileName}: {ex.Message}");
            }
        }

        _references = refs;
        return refs;
    }

    /// <summary>If true (default), refuses to compile code that trips the SecurityAnalyzer.</summary>
    public bool EnforceSecurityAnalyzer { get; set; } = true;

    public Task<CompileResult> CompileAsync(string source, string assemblyName = "GeneratedApp")
        => CompileAsync(source, assemblyName, extraReferences: null);

    public async Task<CompileResult> CompileAsync(
        string source,
        string assemblyName,
        IReadOnlyList<MetadataReference>? extraReferences)
    {
        var sw = Stopwatch.StartNew();

        if (EnforceSecurityAnalyzer)
        {
            var findings = SecurityAnalyzer.Analyze(source);
            if (findings.Count > 0)
            {
                return new CompileResult
                {
                    Success = false,
                    Diagnostics = findings.Select(f => $"[{f.Rule}] {f.Message}").ToArray(),
                    CompileTimeMs = sw.ElapsedMilliseconds,
                };
            }
        }

        var tree = CSharpSyntaxTree.ParseText(source);
        var baseRefs = await GetReferencesAsync();
        var refs = extraReferences is null
            ? (IEnumerable<MetadataReference>)baseRefs
            : baseRefs.Concat(extraReferences);

        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { tree },
            refs,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                concurrentBuild: false));

        using var dllStream = new MemoryStream();
        EmitResult emit = compilation.Emit(dllStream);
        sw.Stop();

        if (!emit.Success)
        {
            var diags = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToArray();
            return new CompileResult
            {
                Success = false,
                Diagnostics = diags,
                CompileTimeMs = sw.ElapsedMilliseconds
            };
        }

        var bytes = dllStream.ToArray();
        return new CompileResult
        {
            Success = true,
            AssemblyBytes = bytes,
            CompileTimeMs = sw.ElapsedMilliseconds,
            AssemblySize = bytes.Length
        };
    }
}
