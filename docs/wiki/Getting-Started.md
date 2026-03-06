# Getting Started

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or later
- Windows 10/11 (the host application is WPF-based)
- An API key for at least one AI provider, **or** a local model server (Ollama / LM Studio)

> **Tip:** To use a different .NET version (e.g. .NET 10), change `NeoNetMajor` in `Directory.Build.props` at the solution root.

## 1. Clone and Build

```bash
git clone https://github.com/<your-username>/neo.git
cd neo
dotnet build neo.sln
```

The solution is fully self-contained — all dependencies are included in the repository.

## 2. Configure an AI Provider

N.E.O. reads API keys from **Windows user environment variables**. Set at least one:

```powershell
# PowerShell (pick one or more)
[Environment]::SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-...", "User")
[Environment]::SetEnvironmentVariable("OPENAI_API_KEY", "sk-...", "User")
[Environment]::SetEnvironmentVariable("GEMINI_API_KEY", "AI...", "User")
```

Or use the **built-in setup wizard** — if no keys are detected on first launch, N.E.O. will open a setup window where you can enter your keys directly.

### Using Local Models (No API Key Required)

If you prefer to run models locally:

1. Install [Ollama](https://ollama.com) or [LM Studio](https://lmstudio.ai)
2. Start the server (default ports: Ollama `11434`, LM Studio `1234`)
3. Launch N.E.O. — it will auto-detect the running server

## 3. Run

```bash
dotnet run --project Neo.App
```

Or open `neo.sln` in Visual Studio / Rider and press F5.

## 4. Your First Prompt

Type a prompt in the text box at the bottom and press **Enter**:

```
Create a calculator with large colorful buttons and a display at the top
```

N.E.O. will:
1. Send your prompt to the AI
2. Receive generated C# code
3. Download any required NuGet packages
4. Compile the code with Roslyn
5. Display the result in the preview panel

The entire process takes a few seconds.

## 5. Iterate

Now refine your creation with follow-up prompts:

```
Make the buttons round and add a history panel on the right side
```

The AI will send a **patch** (unified diff) instead of rewriting everything, preserving your existing code.

## Next Steps

- [[Features Overview]] — Learn about all available features
- [[Designer Mode]] — Click on UI elements to edit them visually
- [[Export and Import]] — Save your creation as a standalone executable
- [[Keyboard Shortcuts]] — Speed up your workflow
