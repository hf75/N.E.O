# N.E.O. — Native Executable Orchestrator

**AI-powered tool builder** — describe what you need in natural language, and N.E.O. generates, compiles, and runs it in real time.

Think of it as Claude Artifacts or ChatGPT Canvas — but for native .NET desktop apps with a real GUI (WPF or Avalonia), running locally on your machine.

> **Status:** Pre-alpha (v0.000000000001) — hobby project, born out of curiosity about what happens when you combine LLMs with runtime compilation.

## What is N.E.O.?

N.E.O. is a .NET desktop application that lets you create small, ready-to-use utility programs and tools through natural language prompts. Need a quick file renamer, a JSON viewer, a color picker, or a custom calculator? Just describe it — N.E.O. uses AI (Claude, ChatGPT, Gemini, or local models like Ollama and LM Studio) to generate C# code, compiles it at runtime using Roslyn, and displays the result in a child process — all in seconds.

**Key features:**
- Generate WPF, Avalonia, or React (WebView2) apps from text descriptions
- Live compilation and hot-reload of generated code
- Incremental updates through unified diff patches
- Export WPF apps as native Windows executables
- Export Avalonia apps for Windows, Linux, and macOS
- Branching undo/redo history
- Visual designer mode (click-to-edit)
- Optional sandboxed execution via Windows AppContainer
- Optional Python integration via embedded Python 3.11

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or later (change `NeoNetMajor` in `Directory.Build.props` to match your version)
- Windows 10/11 (the host application is WPF-based)
- At least one AI provider:
  - **Cloud:** [Anthropic Claude](https://console.anthropic.com/settings/keys), [OpenAI](https://platform.openai.com/api-keys), or [Google Gemini](https://aistudio.google.com/apikey) (API key required)
  - **Local:** [Ollama](https://ollama.com) or [LM Studio](https://lmstudio.ai) (no key required)

## Building

```bash
git clone https://github.com/hf75/N.E.O.git
cd N.E.O
dotnet build neo.sln
```

The solution is fully self-contained — all dependencies (including the agent libraries) are included in the repository.

## Architecture

```
Neo.App (WPF Host)
  |-- Neo.Agents.Claude / OpenAI / Gemini / Ollama / LmStudio (AI code generation)
  |-- Neo.Agents.GeminiImageGen (AI image generation)
  |-- Neo.Agents.OpenAIWhisper (Speech-to-Text)
  |-- Neo.Agents.OpenAITTS (Text-to-Speech)
  |-- Neo.Agents.GeminiImageAnalysis (AI image analysis / OCR)
  |-- Neo.AssemblyForge (Roslyn compilation pipeline)
  |-- Neo.IPC (Named Pipes with framed protocol)
  |-- Neo.PluginWindowWPF (WPF child process)
  |-- Neo.PluginWindowAvalonia (Avalonia child process)
```

The host application sends prompts to an AI agent, receives structured responses (code or patches), compiles them into DLLs via Roslyn, and streams the binaries over named pipes to a child process for display.

## Documentation

See the [Wiki](https://github.com/hf75/N.E.O/wiki) for the full user guide.

## License

This project is licensed under the [MIT License](LICENSE).
