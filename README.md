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
- **MCP Server for Claude Cowork/Code** — Claude generates and previews live Avalonia or WPF apps on your desktop
- **Excel MCP Server (Neo for Excel)** — In-process Excel COM add-in that gives Claude live read/write access to the active workbook
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
  |-- Neo.PluginWindowAvalonia.MCP (Avalonia child + Smart Edit + embedded Claude + Roslyn)
  |-- Neo.McpServer (MCP Server for Claude Cowork / Claude Code)
  |-- Neo.ExcelMcp (Excel MCP — in-process COM add-in + STDIO bridge)
```

The host application sends prompts to an AI agent, receives structured responses (code or patches), compiles them into DLLs via Roslyn, and streams the binaries over named pipes to a child process for display.

### Smart Edit (Ctrl+K)

The MCP variant of the preview window (`Neo.PluginWindowAvalonia.MCP`) includes an embedded Claude chat overlay. Press **Ctrl+K** while viewing any generated app — a chat panel appears where you can type modification requests directly. The app modifies itself in real-time: Claude generates code, embedded Roslyn compiles it, and the control hot-reloads — all without leaving the app. No MCP server or Cowork needed for this workflow.

## MCP Server (Claude Cowork / Claude Code)

N.E.O. includes an [MCP (Model Context Protocol)](https://modelcontextprotocol.io) server that lets **Claude Cowork** or **Claude Code** compile and display live Avalonia apps on your desktop — without running the full N.E.O. host application.

**How it works:** You describe an app in Claude Cowork, Claude writes C# code, the MCP server compiles it via Roslyn in ~1 second, and a real Avalonia window appears on your desktop — streamed over Named Pipes. Claude can then see what it built, tweak the UI live, inject data, run tests, export as standalone executable, and save apps as reusable "skills" that work across conversations.

### Quick Setup

1. Build the MCP server and the MCP preview window:
   ```bash
   dotnet build Neo.McpServer -c Release
   dotnet build Neo.PluginWindowAvalonia.MCP -c Release
   ```

2. Add to your Claude settings (Claude Code: `.claude/settings.json`, Claude Desktop: MCP config):
   ```json
   {
     "mcpServers": {
       "neo-preview": {
         "command": "dotnet",
         "args": ["/path/to/Neo.McpServer/bin/Release/net9.0/Neo.McpServer.dll"],
         "env": {
           "NEO_PLUGIN_PATH": "/path/to/Neo.PluginWindowAvalonia.MCP/bin/Release/net9.0",
           "NEO_SKILLS_PATH": "/path/to/your/neo-apps",
           "ANTHROPIC_API_KEY": "sk-ant-your-key-here"
         }
       }
     }
   }
   ```

   > Both `Debug` and `Release` builds work. Use `Release` for better performance; use `Debug` if you want to attach a debugger. Just make sure both paths use the same configuration.
   >
   > `NEO_SKILLS_PATH` is optional — enables the App Skills Registry for saving and auto-loading apps across conversations.
   >
   > `ANTHROPIC_API_KEY` is needed for the Smart Edit feature (Ctrl+K in the preview window). Also add `OPENAI_API_KEY`, `GEMINI_API_KEY` etc. if you want those providers available.

3. In Claude Cowork, say: *"Create a calculator app with dark theme"* — the app appears live on your desktop.

### MCP Tools (25)

All tools accept an optional `windowId` parameter for multi-window mode. Omit it for single-window (backward compatible).

| Tool | Description |
|------|-------------|
| `compile_and_preview` | Compile C# code and show it in a live preview window |
| `update_preview` | Hot-reload modified code in the existing preview window |
| `patch_preview` | Apply a unified diff patch — send only changed lines instead of full code |
| `capture_screenshot` | Take a screenshot — Claude can **see** the running app |
| `inspect_visual_tree` | Get the full UI control hierarchy as structured JSON |
| `set_property` | Change colors, fonts, text **live** without recompiling |
| `inject_data` | Push JSON data into ListBox/ItemsControl/forms at runtime |
| `read_data` | Read current data back from controls |
| `extract_code` | Reverse-engineer the running app back to clean C# source code |
| `run_test` | Run UI assertions against the visual tree (pass/fail) |
| `export_app` | Export as a **standalone executable** (Windows/Linux/macOS) |
| `save_session` | Save app session to a .neo file (code, NuGet packages, WebBridge) |
| `load_session` | Load a .neo session — auto-compiles and shows the app instantly |
| `register_skill` | Register a session as a reusable skill with keywords |
| `unregister_skill` | Remove a skill from the registry |
| `start_web_bridge` | Start an HTTP + WebSocket server — Browser ↔ Avalonia real-time communication |
| `send_to_web` | Push messages to connected browsers via WebSocket |
| `stop_web_bridge` | Stop the web bridge server |
| `list_windows` | List all running preview windows with IDs and status |
| `close_all_windows` | Close all preview windows at once |
| `position_window` | Set a window's position and size on screen |
| `layout_windows` | Arrange windows in presets: side_by_side, top_bottom, left_half_right_stack, grid |
| `get_runtime_errors` | Read runtime exceptions from the running app |
| `get_preview_status` | Check preview status, logs, and error summary |
| `close_preview` | Close a specific preview window |

See the [Wiki: MCP Server](https://github.com/hf75/N.E.O/wiki/MCP-Server) for full documentation.

## Excel MCP Server (Neo for Excel)

A second MCP server that gives Claude **live access to your Excel workbook** — not by reading files, but by running as an in-process COM add-in inside `Excel.exe`. Sub-50ms response times, sees your active selection, reads and writes cells directly.

```bash
dotnet build Neo.ExcelMcp/Neo.ExcelMcp.AddIn -c Debug
dotnet build Neo.ExcelMcp/Neo.ExcelMcp.Bridge -c Debug
```

Load the `.xll` in Excel, add the bridge to your Claude config, and ask Claude: *"What's in my spreadsheet?"*

### Excel MCP Tools (8)

| Tool | Description |
|------|-------------|
| `excel_context` | Workbook name, active sheet, selection, sheet list |
| `excel_read` | Read cell values (range or current selection) |
| `excel_write` | Write values to cells |
| `excel_write_formula` | Write formulas (SUM, AVERAGE, VLOOKUP...) |
| `excel_tables` | List all Excel Tables with headers |
| `excel_read_table` | Read a Table as array of records |
| `excel_format` | Bold, colors, number format, borders, alignment |
| `excel_create_sheet` | Create a new worksheet |

When both MCP servers are connected, Claude can read Excel data, build a native Avalonia UI, and write results back — all from a single conversation.

See the [Wiki: Excel MCP](https://github.com/hf75/N.E.O/wiki/Excel-MCP) for full documentation.

## Documentation

See the [Wiki](https://github.com/hf75/N.E.O/wiki) for the full user guide.

## License

This project is licensed under the [MIT License](LICENSE).
