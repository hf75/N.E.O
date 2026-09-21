using Neo.App.WebApp.Services.Compilation;

namespace Neo.App.WebApp.Tests;

/// <summary>
/// WasmCompiler finds Roslyn's reference assemblies through <c>_framework/blazor.boot.json</c>.
/// The shape of that file changed with .NET 10 (arrays of objects instead of objects keyed by
/// file name). A parser that does not understand the manifest returns an empty map, the browser
/// client then compiles nothing — and nothing fails before the user's first prompt.
/// </summary>
public class WasmBootManifestTests
{
    [Fact]
    public void Net10_manifest_maps_virtual_paths_to_fingerprinted_files()
    {
        // Trimmed from a real net10.0-browser publish.
        var json = """
            {
              "mainAssemblyName": "Neo.App.WebApp.Browser",
              "resources": {
                "hash": "sha256-abc",
                "jsModuleNative": [ { "name": "dotnet.native.z0bramyx0a.js" } ],
                "wasmNative": [ { "name": "dotnet.native.f8ry4st1s8.wasm", "hash": "sha256-x", "cache": "force-cache" } ],
                "coreAssembly": [
                  { "virtualPath": "System.Private.CoreLib.dll", "name": "System.Private.CoreLib.icby9g21tz.dll", "hash": "sha256-y" }
                ],
                "assembly": [
                  { "virtualPath": "Avalonia.Base.dll", "name": "Avalonia.Base.5d2jkk9qdl.dll", "hash": "sha256-z" },
                  { "virtualPath": "My.lowercasetail.dll", "name": "My.lowercasetail.q1w2e3r4t5.dll", "hash": "sha256-w" }
                ],
                "satelliteResources": { "de": [ { "virtualPath": "X.resources.dll", "name": "X.resources.abcdefgh12.dll" } ] }
              }
            }
            """;

        var map = WasmCompiler.ParseBootManifest(json);

        Assert.Equal("Avalonia.Base.5d2jkk9qdl.dll", map["Avalonia.Base"]);
        Assert.Equal("System.Private.CoreLib.icby9g21tz.dll", map["System.Private.CoreLib"]);
        // The un-fingerprinted virtualPath wins: a lower-case last name segment is not a fingerprint.
        Assert.Equal("My.lowercasetail.q1w2e3r4t5.dll", map["My.lowercasetail"]);
        // Script modules are not assemblies and never end up in the map.
        Assert.DoesNotContain(map.Values, file => file.EndsWith(".js", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pre_net10_manifest_keyed_by_file_name_still_works()
    {
        var json = """
            {
              "resources": {
                "assembly": {
                  "Avalonia.Base.5d2jkk9qdl.dll": "sha256-z",
                  "Plain.dll": "sha256-p"
                },
                "coreAssembly": { "System.Private.CoreLib.icby9g21tz.wasm": "sha256-y" }
              }
            }
            """;

        var map = WasmCompiler.ParseBootManifest(json);

        Assert.Equal("Avalonia.Base.5d2jkk9qdl.dll", map["Avalonia.Base"]);
        Assert.Equal("Plain.dll", map["Plain"]);
        Assert.Equal("System.Private.CoreLib.icby9g21tz.wasm", map["System.Private.CoreLib"]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "resources": [] }""")]
    [InlineData("""{ "resources": { "assembly": [ 42, { "hash": "no name" } ] } }""")]
    public void Unknown_shapes_yield_an_empty_map_instead_of_throwing(string json)
    {
        Assert.Empty(WasmCompiler.ParseBootManifest(json));
    }
}
