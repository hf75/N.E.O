# Web App

Neo.WebApp is the **browser-hosted** variant of Neo. The exact same flow — prompt → C# → compiled DLL → live UI — runs entirely inside the browser's WebAssembly runtime. A small local ASP.NET Core helper proxies AI requests (so API keys never reach the browser) and resolves NuGet packages.

```
Browser tab                                     Neo.Backend (your machine)
+------------------------------------+          +------------------------------+
|  Neo.App.WebApp.Browser (WASM)     |          |  ASP.NET Core                |
|  • Avalonia UI + chat              | <------> |  • POST /api/ai/*/stream     |
|  • Roslyn compiler                 | SSE+ZIP  |    (Claude/OpenAI/Gemini/…)  |
|  • Collectible ALC (plugin host)   |          |  • POST /api/nuget/resolve   |
|  • IndexedDB sessions              |          |  • Static: wwwroot/ bundle   |
+------------------------------------+          +------------------------------+
```

**What's different from the desktop host:** no child process, no Named Pipes. The compiled plugin DLL is loaded into a collectible `AssemblyLoadContext` in the same WASM page as the host app, and runs inside the browser's sandbox. Everything else (structured-response contract, patch workflow, session format) is identical.

**When to prefer it**

- You don't want to install a .NET runtime on the target machine — a browser is enough (the SDK is still needed to build / run the backend locally).
- You like the browser's natural sandbox for experimenting with generated code.
- You want to iterate from a laptop without the full Neo desktop environment.

**When not to prefer it**

- You need WPF-specific features (designer mode, AppContainer).
- You need P/Invoke, `System.IO.File`, or any API the WASM runtime doesn't expose. These are blocked by a Roslyn-based security analyzer before compilation (see the *Security* section below for the blocked API list).

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- `wasm-tools` workload (`dotnet workload install wasm-tools-net9` if missing)
- A modern browser (Chromium, Firefox, Safari — all tested)
- One AI provider configured (see [[Settings and Configuration]])

## Setup

### 1. Publish the browser client once

```bash
cd N.E.O
dotnet publish Neo.App.WebApp/Neo.App.WebApp.Browser/Neo.App.WebApp.Browser.csproj \
    -c Release \
    -o Neo.App.WebApp/Neo.App.WebApp.Browser/bin/Release/net9.0-browser/publish
```

This produces the full WASM bundle (Avalonia + Roslyn + Neo services) in the publish folder. ~50 MB uncompressed, ~27 MB brotli, ~12 MB gzip.

> The backend's build target auto-copies the published `wwwroot` into its own `bin/Release/net9.0/wwwroot`. You only need to run `publish` again when you change client code.

### 2. Run the backend

```bash
dotnet run --project Neo.Backend -c Release --urls=http://localhost:5099
```

The backend prints which providers it sees:

```
Neo.Backend — provider status:
  claude     available
  openai     available
  gemini     missing env var
  ollama     missing env var
  lmstudio   missing env var
```

Open <http://localhost:5099>. First load takes a few seconds while the browser streams and caches the WASM bundle.

## Using it

The left pane is the chat + prompt input. The right pane has two tabs:

- **Preview** — the compiled plugin renders here, fully interactive.
- **Code** — the raw C# the AI produced; editable. Click **Recompile** to apply your edits to the live preview.
- **Monaco** — the same code inside an overlaid Monaco editor (CDN-loaded, ~5 MB on first open). Click **Recompile (Monaco)** to apply.

Submit with **Ctrl+Enter** or the *Generate & Run* button. The assistant reply streams into the chat bubble live; once the JSON is complete, the status line shows compile/load timings:

```
NuGet resolved (3 DLLs in 1120 ms).
Compiling generated code…
Loading 48824 B + 3 dep(s) into plugin host…
Done in 4321 ms. Compile: 980 ms, DLL: 48824 B.
```

### Iterating on a generated app

Just follow up in the chat — the AI sees the previous turn's code (not just the summary) and produces a full updated source that preserves everything you didn't ask to change.

### NuGet packages

The AI can request extra packages by adding a `"nuget"` field to its response:

```json
{
  "code": "…",
  "nuget": [{"id": "NodaTime", "version": "3.1.11"}]
}
```

The backend runs the exact same `NuGetPackageService` that Neo.App.Core and Neo.McpServer use, which does full transitive-dep resolution. The resolved DLLs ship to the browser as a ZIP and are fed to Roslyn for compile and to the plugin ALC for runtime resolution.

Cache lives at `%LocalAppData%/Neo.Backend/nuget-cache/<tfm>/` and persists across backend restarts. First resolve of a package: 5–15 s. Subsequent resolves with the same packages: ~1 s or instant (client cache).

### Sessions

| Button | Action |
|---|---|
| **New** | Unload the current plugin and clear chat |
| **Load…** | Dialog listing sessions stored in IndexedDB — load or delete |
| **Save .neo** | Write to IndexedDB **and** download as a JSON file you can share |

`.neo` files are compatible with the desktop host and MCP server (same wire format).

### Providers

The dropdown in the header lists every provider the backend could see. Entries without a configured env var are still shown but disabled. Selection applies to the next prompt only.

## Features in the Web App

| Feature | Status |
|---|---|
| Prompt → compile → live UI | Yes |
| Iterative edits with history | Yes |
| NuGet packages (transitive) | Yes — via backend proxy |
| Session save/load | Yes — IndexedDB + `.neo` download |
| Live AI streaming into chat | Yes |
| Monaco editor | Yes — HTML overlay over the Avalonia canvas |
| Unified-diff patches | Not yet — full-code round-trips only |
| Unloadable plugin (collectible ALC) | Yes — heap stays flat over long sessions |
| Forbidden-API analyzer | Yes — blocks `File`, `Process`, `P/Invoke`, `unsafe`, `System.Windows.*` |
| Export as standalone `.exe` | Not yet — planned |
| Visual designer | Not yet — possibly WPF-only forever |
| Channel back-reporting (`Ai.Trigger`) | Not yet — MCP-only |

## Bundle size

After `dotnet publish -c Release`:

| Variant | Total |
|---|---|
| Uncompressed | ~56 MB |
| Brotli (what browsers fetch) | ~27 MB |
| Gzip (fallback) | ~36 MB |

The bundle is currently shipped untrimmed (`<PublishTrimmed>false</PublishTrimmed>`) because `System.Text.Json` needs reflection for DTO deserialization. Follow-up work — introduce `JsonSerializerContext` for every wire-format type — will enable trimming and bring the bundle back toward ~9 MB brotli. See [[Troubleshooting]] if you want to trim anyway.

## Security

Generated code is screened by `SecurityAnalyzer` before Roslyn ever sees it. See [[Features Overview]] for the blocked APIs. The browser sandbox blocks real filesystem / network / process access at the runtime layer anyway — the analyzer just gives a fast, clear error message.

Disabling: `WasmCompiler.EnforceSecurityAnalyzer = false` (tests only).

## Related pages

- [[Backend API]] — the endpoints the Web App talks to (useful if you want to build another client)
- [[Architecture]] — where the Web App projects fit into Neo overall
- [[Settings and Configuration]] — env vars common to all surfaces
- [[Troubleshooting]] — Web App-specific issues are in their own section there
