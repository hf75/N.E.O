# N.E.O. — Native Executable Orchestrator

**AI-powered desktop application builder** — describe what you want in natural language, and N.E.O. generates, compiles, and runs it in real time.

> **Status:** Pre-alpha research preview (V0.000006)

## What is N.E.O.?

N.E.O. is a .NET desktop application that lets you create desktop UIs through natural language prompts. It uses AI (Claude, ChatGPT, or Gemini) to generate C# code, compiles it at runtime using Roslyn, and displays the result in a sandboxed child process.

**Key features:**
- Generate WPF, Avalonia, or React (WebView2) user interfaces from text descriptions
- Live compilation and hot-reload of generated code
- Sandboxed execution via Windows AppContainer
- Incremental updates through unified diff patches
- Export to standalone executables (Windows, Linux, macOS)
- Branching undo/redo history
- Visual designer mode (click-to-edit)
- Optional Python integration via embedded Python 3.11

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or later (change `NeoNetMajor` in `Directory.Build.props` to match your version)
- Windows 10/11 (the host application is WPF-based)
- An API key for at least one AI provider:
  - [Anthropic Claude](https://www.anthropic.com/)
  - [OpenAI ChatGPT](https://openai.com/)
  - [Google Gemini](https://ai.google.dev/)

## Building

```bash
git clone https://github.com/<your-username>/neo.git
cd neo
dotnet build neo.sln
```

The solution is fully self-contained — all dependencies (including the agent libraries) are included in the repository.

## Architecture

```
Neo.App (WPF Host)
  |-- Neo.Agents (Claude / ChatGPT / Gemini)
  |-- Neo.AssemblyForge (Roslyn compilation pipeline)
  |-- Neo.IPC (Named Pipes with framed protocol)
  |-- Neo.PluginWindowWPF (WPF child process)
  |-- Neo.PluginWindowAvalonia (Avalonia child process)
```

The host application sends prompts to an AI agent, receives structured responses (code or patches), compiles them into DLLs via Roslyn, and streams the binaries over named pipes to an isolated child process for display.

## License

This project is licensed under the [MIT License](LICENSE).
