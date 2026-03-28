# N.E.O. — Native Executable Orchestrator

**AI-powered desktop application builder** — describe what you want in natural language, and N.E.O. generates, compiles, and runs it in real time.

N.E.O. runs on **Windows, Linux, and macOS** via its Avalonia-based host, or on Windows with its original WPF-based host.

## What is N.E.O.?

N.E.O. is a .NET desktop application that lets you create desktop apps through natural language prompts. It uses AI (Claude, ChatGPT, Gemini, or local models) to generate C# code, compiles it at runtime using Roslyn, and displays the result in a child process.

## Quick Links

- [[Getting Started]] — Install, configure, run
- [[Features Overview]] — All features at a glance
- [[MCP Server]] — Use N.E.O. with Claude Cowork / Claude Code
- [[Designer Mode]] — Click-to-edit visual editing
- [[Export and Import]] — Save and share your creations
- [[Settings and Configuration]] — AI providers, frameworks, options
- [[Keyboard Shortcuts]] — All shortcuts in one place
- [[Troubleshooting]] — Common issues and solutions

## How It Works

```
You type a prompt
    -> AI generates C# code
        -> Roslyn compiles it to a DLL
            -> DLL is streamed to a child process via named pipes
                -> Your app appears in real time
```

Each iteration builds on the previous one. Ask for changes, and the AI sends a patch — no full rewrite needed.

## Two Host Applications

| Host | Project | Platform | Solution |
|------|---------|----------|----------|
| **WPF host** | `Neo.App` | Windows | `neo.sln` |
| **Avalonia host** | `Neo.App.Avalonia` | Windows, Linux, macOS | `neo-avalonia.sln` |

Both hosts share the same core engine (`Neo.App.Core`), AI agents, compilation pipeline, and IPC layer. Choose the one that matches your platform.

## Supported UI Frameworks

| Framework | Platform | Use Case |
|-----------|----------|----------|
| **WPF** (default) | Windows | Native Windows desktop UIs |
| **Avalonia** | Windows, Linux, macOS | Cross-platform desktop UIs |
| **React** (WebView2) | Windows | Web-based UIs with full React ecosystem |

## Supported AI Providers

| Provider | Requires | Key Variable |
|----------|----------|--------------|
| Anthropic Claude | API key | `ANTHROPIC_API_KEY` |
| OpenAI | API key | `OPENAI_API_KEY` |
| Google Gemini | API key | `GEMINI_API_KEY` |
| Ollama | Local server | — |
| LM Studio | Local server | — |
