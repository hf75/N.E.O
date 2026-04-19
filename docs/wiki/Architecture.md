# Architecture

This page is for contributors and anyone who wants to understand how the pieces fit together. For user-facing docs see [[Home]].

## The three surfaces

Neo has one brain and three bodies:

```
     +-------------------------------------------------------------+
     |                     Neo.App.Core                            |
     |   AppController, structured-response contract, chat state,  |
     |   patch workflow, session format, IAgent registry           |
     +-------------------------------------------------------------+
                    ^             ^             ^
                    |             |             |
        Desktop host|  MCP server |  Web app    |
                    |             |             |
     +--------------+----+ +------+--------+ +--+---------------+
     | Neo.App (WPF)     | | Neo.McpServer | | Neo.App.WebApp   |
     | Neo.App.Avalonia  | |               | | (Avalonia WASM)  |
     +---+---------------+ +-+-------------+ +--+---------------+
         |                   |                  |
         | Named Pipes       | Named Pipes      | In-process ALC
         v                   v                  v
     Neo.PluginWindow*   Neo.PluginWindow*  (same page)
     (child processes)   (.MCP variants)
                                              +------------------+
                                              | Neo.Backend      |
                                              | AI+NuGet proxy   |
                                              +------------------+
```

**Shared core (`Neo.App.Core`, ~37 source files)**

- `AppController` — central orchestrator, 2 000+ LOC, state machine around the prompt → compile → load loop. Used by Neo.App (WPF) and Neo.App.Avalonia via source-linking.
- `StructuredResponse` — the JSON shape the AI returns (`code`, `patch`, `explanation`, `chat`, `nuget`, `powershell`, `consoleApp`). Every surface parses this the same way.
- `UnifiedDiffPatcher` — fuzzy-hunk patch application. Only used by desktop and MCP; the Web App does full-code round-trips for now.
- `NuGetPackageService` — wrapper over `NuGet.Protocol` 7.0.0 with transitive resolution. Used by Neo.App, Neo.McpServer, and Neo.Backend.
- `CompilationService` — wraps Roslyn behind the `CSharpCompileAgent` / `CSharpDllCompileAgent`.

**Desktop path**

1. `AppController.ExecutePromptAsync` → AI agent → `StructuredResponse`.
2. `CompilationService` produces a `.dll` (optionally with NuGet DLL paths).
3. `ChildProcessService` spawns `Neo.PluginWindowWPF` or `Neo.PluginWindowAvalonia`.
4. `FramedPipeMessenger` streams the DLL bytes over a Named Pipe.
5. Child process loads the DLL into a `SandboxPluginLoadContext` (collectible ALC) and mounts the UserControl.

**MCP path**

Same as desktop, but driven by tool calls (`compile_and_preview`, `update_preview`, `patch_preview`, …) from Claude Code / Cowork. The MCP server (`Neo.McpServer`) wraps `CompilationService` + `NuGetPackageService` via [`CompilationPipeline`](../Neo.McpServer/Services/CompilationPipeline.cs) and spawns `Neo.PluginWindowAvalonia.MCP` / `Neo.PluginWindowWPF.MCP` (the `.MCP` variants include the Smart-Edit overlay and channel support). See [[MCP Server]].

**Web App path**

1. Browser (`Neo.App.WebApp.Browser`) posts prompt + history to `/api/ai/{provider}/stream` on `Neo.Backend`.
2. Backend proxies to the provider's API and relays SSE deltas.
3. Client parses StructuredResponse, sends `"nuget"` list (if any) to `/api/nuget/resolve`, receives a ZIP of DLLs.
4. Roslyn, running inside the browser's WASM runtime, compiles the plugin source with the NuGet refs.
5. `InProcessPluginHost` loads the plugin DLL into a collectible ALC in the same page; dep DLLs are resolved on demand by the ALC's `Load` override.

No child process, no pipes. The browser sandbox replaces the AppContainer.

## Project map

Flat layout at the repo root — 27 projects across three solutions.

```
Neo.App                       WPF host (Windows). ~68 .cs files.
Neo.App.Avalonia              Avalonia host (Win/Linux/macOS). Shares Core by source-linking.
Neo.App.Core                  Shared business logic for WPF + Avalonia.
Neo.App.Core.Tests            201 tests.
Neo.App.Api                   Public surface generated apps see (Ai.Trigger, …).

Neo.App.WebApp/               Container directory (sub-solution neo-webapp-like).
  Neo.App.WebApp/             Shared Avalonia library for WASM.
  Neo.App.WebApp.Browser/     WASM head (net9.0-browser). The real target.
  Neo.App.WebApp.Desktop/     Dev-iteration head; not shipped.
Neo.App.WebApp.Tests          22 tests (parser, analyzer, session store, ChatEntry INPC).

Neo.Backend                   ASP.NET Core helper — AI+NuGet proxy + static server.
Neo.Backend.Tests             12 tests (endpoints, provider registry).

Neo.McpServer                 MCP server over stdio. 25 tools.

Neo.ExcelMcp/                 Container directory.
  Neo.ExcelMcp.AddIn          Excel-DNA .xll that runs inside Excel.exe.
  Neo.ExcelMcp.Bridge         stdio↔named-pipe translator for Claude.

Neo.PluginWindowWPF           WPF child process for the desktop host.
Neo.PluginWindowAvalonia      Avalonia child process for the desktop host.
Neo.PluginWindowWPF.MCP       WPF child used by MCP (adds Smart Edit, channels).
Neo.PluginWindowAvalonia.MCP  Avalonia child used by MCP (adds Smart Edit, channels, WebBridge).

Neo.AssemblyForge             Prompt→DLL pipeline (NuGetPackageService, CompilationService). Used by Core, MCP, Backend.
Neo.AssemblyForge.Tests       160 tests.
Neo.IPC                       Named-pipe framed protocol.
Neo.Shared                    PipeClient + PeUtils.

Neo.Agents/                   One project per AI provider / capability:
  Neo.Agents.Core             IAgent / AgentBase.
  Neo.Agents.Claude           Anthropic SDK.
  Neo.Agents.OpenAI           OpenAI SDK (also used by Ollama, LM Studio).
  Neo.Agents.Gemini           Google Gemini SDK.
  Neo.Agents.Ollama           OpenAI-compatible local endpoint.
  Neo.Agents.LmStudio         OpenAI-compatible local endpoint.
  Neo.Agents.GeminiImageGen   Image generation.
  Neo.Agents.GeminiImageAnalysis  Image analysis / OCR.
  Neo.Agents.OpenAIWhisper    Speech-to-text.
  Neo.Agents.OpenAITTS        Text-to-speech.
  Neo.Agents.CSharpCompile    Roslyn → EXE via AppHost template.
  Neo.Agents.CSharpDllCompile Roslyn → DLL only.
  Neo.Agents.NugetLoader      NuGet.Protocol 7.0.0 driver.
  Neo.Agents.PowerShell       PowerShell script execution.

Neo.DynamicSlot.Avalonia      Dynamic UserControl host plumbing.
Neo.DynamicSlot.Wpf           Same, WPF variant.
```

## Solutions

| Solution | Contents |
|---|---|
| `neo.sln` | Full Windows solution — 22 projects including the WPF host and both Excel projects |
| `neo-avalonia.sln` | Cross-platform slice — omits WPF-only projects, 17 projects |

Both are fully self-contained: all agent implementations live in the repo, no private packages required.

## Key patterns

### `IAgent` interface

Every AI call, every Roslyn compile, every PowerShell run is an `IAgent` — `SetOption`/`GetOption`, `SetInput`/`GetInput`, `ExecuteAsync`, `GetOutput`, `GetJsonSchema`. This keeps provider implementations interchangeable and makes unit-testing trivial.

### Collectible AssemblyLoadContext

Both the desktop plugin child and the Web App's in-process host use a **collectible** `AssemblyLoadContext`. Unloading and creating a new one on each recompile keeps the heap stable — validated at −0.7 % growth over 50 iterations in the Web App POC.

In Mono WASM the ALC wrapper object itself lingers after `Unload` (the weak reference still reports `IsAlive = true`) but the assembly payload is freed. Don't trust `IsAlive` as a leak detector; measure total heap growth.

### Structured response

Every agent response flows through `StructuredResponseParser`. The AI is instructed to respond with a single JSON object; the parser accepts plain JSON, JSON inside a ```json fence, or JSON embedded in prose. See `Neo.App.Core/StructuredResponse.cs` (and the Web App's own copy at `Neo.App.WebApp/Services/Ai/StructuredResponse.cs`).

### Named-pipe framed protocol (desktop + MCP)

32-byte header + payload. Frame types: `ControlJson`, `BlobStart`, `BlobChunk`, `BlobEnd`. Blob streaming lets the parent send the whole plugin DLL (megabytes) without buffering in memory. Correlation IDs turn the pipe into a request/response channel when needed.

### Channels (MCP only)

The generated app links against `Neo.App.Api` and calls `Ai.Trigger(prompt)` — the trigger text travels out via the pipe → MCP server → Claude Code channel → a new Claude turn. See [[Channels]].

## Where to start contributing

| You want to | Look at |
|---|---|
| Add a new AI provider | `Neo.Agents/Neo.Agents.Core` and existing providers like `Neo.Agents.Claude` |
| Change the prompt → code → UI flow | `Neo.App.Core/AppController.cs` (desktop), `Neo.App.WebApp/.../AppOrchestrator.cs` (Web) |
| Add a new MCP tool | `Neo.McpServer/Tools/PreviewTools.cs` |
| Tweak generated-code safety rules | `Neo.App.WebApp/.../SecurityAnalyzer.cs` (Web); there's no equivalent on desktop (AppContainer covers it) |
| Explore the Web App stack | [[Web App]] + [[Backend API]] |
