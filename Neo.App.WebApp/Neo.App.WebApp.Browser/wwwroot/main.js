import { dotnet } from './_framework/dotnet.js'

const is_browser = typeof window != "undefined";
if (!is_browser) throw new Error(`Expected to be running in a browser`);

// The boot configuration stays a separate JSON file (WasmBootConfigFileName in the csproj):
// WasmCompiler reads the same file to find the reference assemblies for Roslyn. From .NET 10
// on the loader would otherwise look for an inlined config or for dotnet.boot.js.
const dotnetRuntime = await dotnet
    .withConfigSrc("blazor.boot.json")
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();

const config = dotnetRuntime.getConfig();

await dotnetRuntime.runMain(config.mainAssemblyName, [globalThis.location.href]);
