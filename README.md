# N.E.O. — Native Executable Orchestrator

**AI-powered tool builder** — describe what you need in natural language, and N.E.O. generates, compiles, and runs it in real time.

Think Claude Artifacts or ChatGPT Canvas — but for **native .NET apps** on your own machine, with a real GUI (WPF or Avalonia) or a browser tab.

> **Status:** Pre-alpha (v0.000000000001). Hobby project, MIT-licensed.

## Try it with one prompt

> *"Build an app that captures the system's audio output in real time via WASAPI and generates a techno-club visualizer reacting to frequency, volume, bass and beats."*

---

## Three ways to run Neo

| Surface | Who drives | Where it runs | Setup |
|---|---|---|---|
| **Desktop host** | You type prompts into Neo.App | Native window (WPF or Avalonia) | [Getting Started](docs/wiki/Getting-Started.md) |
| **MCP server** | Claude Code / Cowork drives | Neo controls a child window | [MCP Server](docs/wiki/MCP-Server.md) |
| **Web app** | You type prompts into a browser | Avalonia-in-WASM + local ASP.NET backend | [Web App](docs/wiki/Web-App.md) |

All three share the same `Neo.App.Core` (compile pipeline, agents, structured-response contract). Pick whichever fits your workflow — they're not mutually exclusive.

## Quickstart

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). .NET 10 (LTS) is the minimum; every executable rolls forward to a newer major when .NET 10 itself is not installed, so a machine that only has .NET 11 works too. The version lives in one place: `NeoNetMajor` in `Directory.Build.props`.
- One AI provider env var, or a local model (Ollama, LM Studio). Neo reads keys from user env vars — nothing is stored in the repo.

| Provider | Env var |
|---|---|
| Anthropic Claude | `ANTHROPIC_API_KEY` |
| Google Gemini | `GEMINI_API_KEY` |
| OpenAI | `OPENAI_API_KEY` |
| Ollama | `OLLAMA_HOST` (e.g. `http://localhost:11434`) |
| LM Studio | `LMSTUDIO_HOST` (e.g. `http://localhost:1234`) |

### Desktop host (WPF or Avalonia)

```bash
git clone https://github.com/hf75/N.E.O.git
cd N.E.O
# Windows — includes the WPF host:
dotnet run --project Neo.App
# Cross-platform — Avalonia:
dotnet run --project Neo.App.Avalonia
```

### MCP server (Claude Code / Cowork)

```bash
dotnet build Neo.McpServer -c Release
dotnet build Neo.PluginWindowAvalonia.MCP -c Release
```

Then register the server in `.claude/settings.json` — full details in [docs/wiki/MCP-Server.md](docs/wiki/MCP-Server.md).

### Web app (browser)

```bash
# 1. Publish the Avalonia.Browser client once
dotnet publish Neo.App.WebApp/Neo.App.WebApp.Browser/Neo.App.WebApp.Browser.csproj \
    -c Release -o Neo.App.WebApp/Neo.App.WebApp.Browser/bin/Release/net10.0-browser/publish

# 2. Run the backend (serves the WASM bundle + proxies AI + NuGet)
dotnet run --project Neo.Backend -c Release --urls=http://localhost:5099
```

Open <http://localhost:5099>. Full guide: [docs/wiki/Web-App.md](docs/wiki/Web-App.md).

---

## What it does

- **Prompt → code → live UI in seconds.** Claude/GPT/Gemini writes C#; Roslyn compiles; a collectible `AssemblyLoadContext` loads it into a plugin window (desktop) or the same WASM runtime (web).
- **Iterate.** Follow-up prompts see the previous code and produce full updates (desktop/MCP also support unified-diff patches).
- **[Hot reload](docs/wiki/Hot-Reload.md)** — the next prompt patches the *running* app (desktop and MCP): the file you loaded, the text you typed, the selected row are still there when the new feature appears.
- **[Heal in place](docs/wiki/Hot-Reload.md#heal-in-place)** — an app that throws while you use it is not restarted: it stays up with your data, the AI fixes the code, the fix is patched into the running app and the failed click is done again.
- **[Shared sessions](docs/wiki/Shared-Sessions.md)** — build one app together over the internet: an invitation code and a reply code connect two N.E.O.s directly (WebRTC, no server). One person changes the app at a time; every change is hot-reloaded into everybody's running app, and each person keeps their own data.
- **[App network](docs/wiki/App-Network.md)** — apps that use each other: every running N.E.O. app (preview, living app, MCP preview) offers its `[McpCallable]` methods and `[McpObservable]` values to the others; "show the total from the Sales app and keep it up to date" gives you a dashboard that follows the other window. The AI is told which apps are running.
- **[Living apps](docs/wiki/Living-Apps.md)** — export an app that keeps being built by the person using it: a normal desktop app until they press Ctrl+Shift+N, then N.E.O. slides in beside it, patches the running app, saves the change, and can reset to the version they received.
- **[Web search](docs/wiki/Web-Search.md)** — generated apps answer from the current web with their sources, as text or as typed data for tables, where no free API exists (Anthropic key). If you switch it on, Claude also looks things up while it builds.
- **Branching undo/redo**, **export to standalone `.exe`** (Win/Linux/macOS), **AppContainer sandbox** (WPF).
- **Session save/load** as `.neo` files — same format across desktop, MCP, and Web App.
- **Built-in AI capabilities**: image generation (Gemini), image analysis (Gemini), speech-to-text (Whisper), text-to-speech (OpenAI TTS).
- **Optional Python** integration via embedded Python 3.11 (desktop only).
- **[Live-MCP](docs/wiki/Live-MCP.md)** — every generated app is itself an MCP server. Annotate methods with `[McpCallable]` and properties with `[McpObservable]` and Claude Code can call them directly, read state, subscribe to changes. Works in two modes: live inside N.E.O. (Dev-Mode) or as a standalone exported `.exe` (Frozen-Mode) that anyone can register with `claude mcp add`.
- **[Channel back-reporting](docs/wiki/Channels.md)** — generated apps can push prompts back into Claude Code to drive business logic without pre-coding. Combined with Live-MCP, this closes the loop: Claude drives the app, the app drives Claude.
- **[Excel MCP](docs/wiki/Excel-MCP.md)** — a second MCP server that gives Claude read/write access to the active Excel workbook via an in-process COM add-in.

## Architecture at a glance

```
           Desktop host         MCP server          Web app (browser)
       +----------------+   +----------------+   +------------------+
       |  Neo.App       |   |  Neo.McpServer |   |  Neo.App.WebApp  |
       |  Neo.App.Avalonia|  (driven by Claude)  |  (Avalonia WASM) |
       +--------+---------+---------+----------+---------+---------+
                           |                             |
                +----------+----------+       +----------+-----------+
                |   Neo.App.Core      |       |  Neo.Backend         |
                |   (prompt→DLL path) |       |  (AI+NuGet proxy,    |
                |                     |       |   serves wwwroot)    |
                +----------+----------+       +----------------------+
                           |
          +----------------+-----------------+
          | Neo.AssemblyForge (compile pipe) |
          | Neo.Agents.* (AI providers)      |
          | Neo.IPC (Named Pipes; desktop)   |
          +----------------------------------+
```

Full developer-facing map: [docs/wiki/Architecture.md](docs/wiki/Architecture.md).

## Documentation

Everything lives in [docs/wiki/](docs/wiki/Home.md). Start with [Home](docs/wiki/Home.md) or jump straight to the surface you care about:

- [Getting Started](docs/wiki/Getting-Started.md) — install, first prompt (desktop host path)
- [Web App](docs/wiki/Web-App.md) — the browser variant
- [MCP Server](docs/wiki/MCP-Server.md) — Claude-driven workflow (30 tools)
- [Live-MCP](docs/wiki/Live-MCP.md) — generated apps as MCP servers (call into them, read state, subscribe)
- [Frozen-Mode](docs/wiki/Frozen-Mode.md) — exporting Live-MCP apps as standalone executables
- [Excel MCP](docs/wiki/Excel-MCP.md) — live Excel integration
- [Channels](docs/wiki/Channels.md) — generated apps that push prompts back to Claude
- [Architecture](docs/wiki/Architecture.md) — how the projects fit together
- [Features Overview](docs/wiki/Features-Overview.md) · [Hot Reload](docs/wiki/Hot-Reload.md) · [Living Apps](docs/wiki/Living-Apps.md) · [App Network](docs/wiki/App-Network.md) · [Shared Sessions](docs/wiki/Shared-Sessions.md) · [Web Search](docs/wiki/Web-Search.md) · [Export and Import](docs/wiki/Export-and-Import.md) · [Settings](docs/wiki/Settings-and-Configuration.md) · [Shortcuts](docs/wiki/Keyboard-Shortcuts.md) · [Troubleshooting](docs/wiki/Troubleshooting.md)

## License

MIT — see [LICENSE](LICENSE).
