# N.E.O. — Native Executable Orchestrator

**AI-powered tool builder** — describe what you need in natural language, and N.E.O. generates, compiles, and runs it in real time.

Think of it as Claude Artifacts or ChatGPT Canvas — but for native .NET desktop apps with a real GUI (WPF or Avalonia), running locally on your machine.

> **Status:** Pre-alpha (v0.000000000001) — hobby project, born out of curiosity about what happens when you combine LLMs with runtime compilation.

## Try this — a single prompt, a native app

> *"Build an app that captures the system's audio output in real time via WASAPI — e.g. while a YouTube video is playing — and generates a spectacular, visually stunning live animation for a techno club. The animation should react fluidly to every aspect of the audio signal (frequency, volume, bass, beats) and create maximum visual overload. The animation must respond to the music in real time."*

## What is N.E.O.?

N.E.O. is a .NET desktop application that lets you create small, ready-to-use utility programs and tools through natural language prompts. Need a quick file renamer, a JSON viewer, a color picker, or a custom calculator? Just describe it — N.E.O. uses AI (Claude, ChatGPT, Gemini, or local models like Ollama and LM Studio) to generate C# code, compiles it at runtime using Roslyn, and displays the result in a child process.

**Key features:**
- Generate WPF, Avalonia, or React (WebView2) apps from text descriptions
- Live compilation and hot-reload of generated code
- Incremental updates through unified diff patches
- Export WPF apps as native Windows executables
- Export Avalonia apps for Windows, Linux, and macOS
- **MCP Server for Claude Cowork/Code** — Claude generates and previews live Avalonia apps on your desktop
- Branching undo/redo history
- Visual designer mode (click-to-edit)
- Optional sandboxed execution via Windows AppContainer
- AI image generation, image analysis, speech-to-text, and text-to-speech as built-in capabilities
- Optional Python integration via embedded Python 3.11

## Cross-Platform

N.E.O. ships with two host applications:

| Host | Project | Platform | Solution |
|------|---------|----------|----------|
| **WPF host** | `Neo.App` | Windows only | `neo.sln` |
| **Avalonia host** | `Neo.App.Avalonia` | Windows, Linux, macOS | `neo-avalonia.sln` |

Both hosts share the same core logic (`Neo.App.Core`), AI agents, compilation pipeline, and IPC layer. The difference is the host UI framework:

- **WPF host** — The original host. Side-by-side layout with an embedded Live Preview panel. Includes the visual designer, sandbox security (AppContainer), and full code editor with syntax highlighting (AvalonEdit).
- **Avalonia host** — Cross-platform port. Single-column layout with chat and prompt area. The Live Preview runs in a separate window with magnetic docking (snaps to the edge of the main window). Code editor is available but without syntax highlighting.

Choose the host that matches your platform. On Windows, either host works.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or later (change `NeoNetMajor` in `Directory.Build.props` to match your version)
- Windows 10/11 required only for the WPF host (`Neo.App`); the Avalonia host (`Neo.App.Avalonia`) runs on Windows, Linux, and macOS
- At least one AI provider (see API Keys below)

## API Keys

N.E.O. reads API keys from **user environment variables**. Set at least one:

| Provider | Environment Variable | Get your key |
|----------|---------------------|--------------|
| Anthropic Claude (recommended) | `ANTHROPIC_API_KEY` | [console.anthropic.com](https://console.anthropic.com/settings/keys) |
| Google Gemini (recommended) | `GEMINI_API_KEY` | [aistudio.google.com](https://aistudio.google.com/apikey) |
| OpenAI / ChatGPT | `OPENAI_API_KEY` | [platform.openai.com](https://platform.openai.com/api-keys) |

**Local models** (no key required): [Ollama](https://ollama.com) or [LM Studio](https://lmstudio.ai) — just start the server.

Example (Windows PowerShell):
```powershell
[Environment]::SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-...", "User")
```

Example (Linux / macOS — add to `~/.bashrc` or `~/.zshrc`):
```bash
export ANTHROPIC_API_KEY="sk-ant-..."
```

On first launch, N.E.O. will prompt you to set up your keys if none are found. Use **Ctrl+1** to cycle through available AI providers at any time.

## Building

### Windows (full solution, includes WPF host)

```bash
git clone https://github.com/hf75/N.E.O.git
cd N.E.O
dotnet restore neo.sln
dotnet build neo.sln
```

### Cross-platform (Avalonia host only)

```bash
git clone https://github.com/hf75/N.E.O.git
cd N.E.O
dotnet restore neo-avalonia.sln
dotnet build neo-avalonia.sln
```

The solution is fully self-contained — all dependencies (including the agent libraries) are included in the repository.

## Running

```bash
# WPF host (Windows only)
dotnet run --project Neo.App

# Avalonia host (Windows, Linux, macOS)
dotnet run --project Neo.App.Avalonia
```

## Architecture

```
Neo.App (WPF Host, Windows)  ─┐
                               ├── Neo.App.Core (shared logic)
Neo.App.Avalonia (Cross-plat) ─┘       |
  |-- Neo.Agents.Claude / OpenAI / Gemini / Ollama / LmStudio (AI code generation)
  |-- Neo.Agents.GeminiImageGen (AI image generation)
  |-- Neo.Agents.OpenAIWhisper (Speech-to-Text)
  |-- Neo.Agents.OpenAITTS (Text-to-Speech)
  |-- Neo.Agents.GeminiImageAnalysis (AI image analysis / OCR)
  |-- Neo.AssemblyForge (Roslyn compilation pipeline)
  |-- Neo.IPC (Named Pipes with framed protocol)
  |-- Neo.PluginWindowWPF (WPF child process)
  |-- Neo.PluginWindowAvalonia (Avalonia child process)
  |-- Neo.McpServer (MCP Server for Claude Cowork / Claude Code)
```

The host application sends prompts to an AI agent, receives structured responses (code or patches), compiles them into DLLs via Roslyn, and streams the binaries over named pipes to a child process for display.

## MCP Server (Claude Cowork / Claude Code)

N.E.O. includes an [MCP (Model Context Protocol)](https://modelcontextprotocol.io) server that lets **Claude Cowork** or **Claude Code** compile and display live Avalonia apps on your desktop — without running the full N.E.O. host application.

**How it works:** You describe an app in Claude Cowork, Claude generates C# code and calls `compile_and_preview`, and a real Avalonia window appears on your desktop — compiled at runtime via Roslyn, streamed over Named Pipes.

### Quick Setup

1. Build the MCP server and the Avalonia preview window:
   ```bash
   dotnet build Neo.McpServer -c Release
   dotnet build Neo.PluginWindowAvalonia -c Release
   ```

2. Add to your Claude settings (Claude Code: `.claude/settings.json`, Claude Desktop: MCP config):
   ```json
   {
     "mcpServers": {
       "neo-preview": {
         "command": "dotnet",
         "args": ["/path/to/Neo.McpServer/bin/Release/net9.0/Neo.McpServer.dll"],
         "env": {
           "NEO_PLUGIN_PATH": "/path/to/Neo.PluginWindowAvalonia/bin/Release/net9.0"
         }
       }
     }
   }
   ```

   > Both `Debug` and `Release` builds work. Use `Release` for better performance; use `Debug` if you want to attach a debugger. Just make sure both paths use the same configuration.

3. In Claude Cowork, say: *"Create a calculator app with dark theme"* — the app appears live on your desktop.

### MCP Tools

| Tool | Description |
|------|-------------|
| `compile_and_preview` | Compile C# code and show it in a live preview window |
| `update_preview` | Hot-reload modified code in the existing preview window |
| `close_preview` | Close the preview window |
| `get_preview_status` | Check if a preview window is running |

See the [Wiki: MCP Server](https://github.com/hf75/N.E.O/wiki/MCP-Server) for full documentation.

## Documentation

See the [Wiki](https://github.com/hf75/N.E.O/wiki) for the full user guide.

## License

This project is licensed under the [MIT License](LICENSE).
