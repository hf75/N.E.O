# N.E.O. — Native Executable Orchestrator

**AI-powered tool builder** — describe what you want in natural language, and N.E.O. generates, compiles, and runs it in real time.

N.E.O. comes in three flavours that share the same core: a **desktop host** (WPF or Avalonia), an **MCP server** driven by Claude Code / Cowork, and a **web app** running inside your browser.

## Pick your surface

| | Desktop host | MCP server | Web app |
|---|---|---|---|
| Who drives it | You, in the Neo window | Claude Code / Cowork | You, in a browser tab |
| Install footprint | The full .NET SDK + repo | The repo (no Neo app running) | The repo (plus a local backend) |
| Cross-platform | WPF → Windows · Avalonia → Win/Linux/macOS | Same | Everywhere a browser runs |
| Exports | Standalone `.exe` | Same, via `export_app` tool | `.neo` file download |
| Jump to docs | [[Getting Started]] | [[MCP Server]] | [[Web App]] |

## Guides

### Running Neo

- [[Getting Started]] — Install and run the desktop host (WPF / Avalonia)
- [[Web App]] — Browser-hosted variant; setup and features
- [[MCP Server]] — Register Neo as an MCP server for Claude
- [[Excel MCP]] — Second MCP server: live access to the active Excel workbook

### Building with Neo

- [[Features Overview]] — All features at a glance
- [[Designer Mode]] — Click-to-edit UI
- [[Export and Import]] — Save, share, and export as `.exe`
- [[Channels]] — Generated apps that push prompts back into Claude

### Reference

- [[Architecture]] — Project map and data-flow for contributors
- [[Settings and Configuration]] — AI providers, frameworks, environment variables
- [[Backend API]] — Endpoints served by Neo.Backend (used by the Web App)
- [[Keyboard Shortcuts]] — All shortcuts in one place
- [[Troubleshooting]] — Common issues and fixes

## How it works

```
You type a prompt
    → AI generates C# (code or patch)
        → Roslyn compiles it to a DLL
            → DLL runs in a collectible AssemblyLoadContext
                → Your app appears in real time
```

The context lives in the **same conversation**, so follow-up prompts see the
previous code and produce incremental updates. The loading path differs per
surface:

- **Desktop host** — DLL is streamed to a child process (`Neo.PluginWindow*`) over Named Pipes.
- **MCP server** — Same as desktop, but the preview process is spawned on demand by `compile_and_preview`.
- **Web app** — Roslyn runs in the browser WASM runtime; the DLL is loaded into a collectible ALC in the same page.

## Supported AI providers

| Provider | Requires | Key variable |
|---|---|---|
| Anthropic Claude | API key | `ANTHROPIC_API_KEY` |
| OpenAI | API key | `OPENAI_API_KEY` |
| Google Gemini | API key | `GEMINI_API_KEY` |
| Ollama (local) | Local server | `OLLAMA_HOST` (e.g. `http://localhost:11434`) |
| LM Studio (local) | Local server | `LMSTUDIO_HOST` (e.g. `http://localhost:1234`) |
