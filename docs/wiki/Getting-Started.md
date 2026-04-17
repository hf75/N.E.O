# Getting Started

Install, configure a provider, run your first prompt. This page covers the
**desktop host** path. If you want the browser variant, jump to [[Web App]];
for the Claude-driven MCP workflow, see [[MCP Server]].

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or later
- One AI provider configured, or a local model server (Ollama / LM Studio)

**Platform requirements**

- **WPF host** (`Neo.App`) — Windows 10/11
- **Avalonia host** (`Neo.App.Avalonia`) — Windows, Linux, or macOS

> To use a different .NET version (e.g. .NET 10), change `NeoNetMajor` in `Directory.Build.props` at the repo root.

## 1. Clone and build

Two solutions ship. Pick the one that matches your platform.

```bash
git clone https://github.com/hf75/N.E.O.git
cd N.E.O
```

**Windows** — full solution, includes the WPF host:

```bash
dotnet build neo.sln
```

**Cross-platform** — Avalonia only:

```bash
dotnet build neo-avalonia.sln
```

Every agent implementation lives in the repo — no private packages.

## 2. Configure an AI provider

Neo reads API keys from environment variables.

**Windows (PowerShell)**

```powershell
[Environment]::SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-…", "User")
[Environment]::SetEnvironmentVariable("OPENAI_API_KEY",    "sk-…",     "User")
[Environment]::SetEnvironmentVariable("GEMINI_API_KEY",    "AI…",      "User")
```

**Linux / macOS** — add to `~/.bashrc`, `~/.zshrc`, or equivalent:

```bash
export ANTHROPIC_API_KEY="sk-ant-…"
export OPENAI_API_KEY="sk-…"
export GEMINI_API_KEY="AI…"
```

Then reload your shell or open a new terminal.

If Neo can't see any keys on first launch, it opens a setup window where you
can enter them directly — no manual env editing needed.

### Local models (no API key)

1. Install [Ollama](https://ollama.com) or [LM Studio](https://lmstudio.ai)
2. Start the server (default ports: Ollama `11434`, LM Studio `1234`)
3. Launch Neo — it auto-detects the running server

## 3. Run

```bash
# WPF host (Windows only)
dotnet run --project Neo.App

# Avalonia host (Windows, Linux, macOS)
dotnet run --project Neo.App.Avalonia
```

Or open the solution in Visual Studio / Rider and press F5.

## 4. Your first prompt

Type a prompt in the input at the bottom and press **Enter**:

```
Create a calculator with large colorful buttons and a display at the top
```

Neo will:

1. Send the prompt to the AI
2. Receive generated C# code
3. Download any requested NuGet packages
4. Compile with Roslyn
5. Show the result in the Live Preview (embedded panel in WPF, separate docked window in Avalonia)

The whole round-trip takes a few seconds. First-run NuGet downloads take longer — cached from then on.

## 5. Iterate

Follow up in the chat:

```
Make the buttons round and add a history panel on the right side
```

The AI sends a unified-diff patch, not a full rewrite — your existing code is preserved.

## Where next

- [[Features Overview]] — Every feature the desktop host exposes
- [[Designer Mode]] — Click a UI element to edit its properties
- [[Export and Import]] — Save your generation as a standalone `.exe`
- [[Keyboard Shortcuts]] — Speed up your workflow
- Want the same flow without installing Neo? → [[Web App]]
- Want Claude Code / Cowork to drive the whole thing? → [[MCP Server]]
