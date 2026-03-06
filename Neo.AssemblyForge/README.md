# Neo.AssemblyForge

Reusable prompt -> C# code -> compiled DLL/EXE pipeline extracted from `N.E.O.`.

## Minimal usage (DLL/UserControl)

```csharp
using Neo.Agents.Core;
using Neo.AssemblyForge;
using Neo.AssemblyForge.Completion;

var agent = new AnthropicTextChatAgent();
agent.SetOption("ApiKey", Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY", EnvironmentVariableTarget.User));
agent.SetOption("Model", "claude-sonnet-4-5");

var workspace = AssemblyForgeDefaults.CreateLocalAppDataWorkspace("MyApp", AssemblyForgeUiFramework.Wpf);
var client = new AssemblyForgeClient(new AiApiAgentCompletionProvider(agent), workspace);
var session = client.CreateSession(new AssemblyForgeSessionOptions { UiFramework = AssemblyForgeUiFramework.Wpf });

var result = await session.RunAsync("Create a simple login form.", CancellationToken.None);
if (result.Status == AssemblyForgeStatus.Success) Console.WriteLine(result.OutputDllPath);
```

## Executable (.exe)

```csharp
workspace = workspace with { AssemblyName = "MyGeneratedApp" };

var exeSession = client.CreateSession(new AssemblyForgeSessionOptions
{
    ArtifactKind = AssemblyForgeArtifactKind.Executable,
    UiFramework = AssemblyForgeUiFramework.Wpf,
    ExecutableMainTypeName = "Neo.Dynamic.DynamicProgram",
});

var exeResult = await exeSession.RunAsync("Create a tiny calculator app.", CancellationToken.None);
if (exeResult.Status == AssemblyForgeStatus.Success) Console.WriteLine(exeResult.OutputExePath);
```

## Notes

- DLL output: `result.OutputDllPath`
- EXE output: `result.OutputExePath` (output folder is `result.OutputExeDirectory`)
- Dependency DLLs: `result.NuGetDllPaths` + `result.AdditionalDllPaths`
- `AssemblyForgeSession` keeps state (history/current code/NuGet list) so you can run multiple prompts incrementally.
