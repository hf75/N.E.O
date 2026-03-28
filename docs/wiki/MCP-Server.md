# MCP Server (Claude Cowork / Claude Code)

N.E.O. includes an MCP (Model Context Protocol) server that lets **Claude Cowork** or **Claude Code** compile and display live Avalonia desktop apps on your screen — without running the full N.E.O. host application.

## What This Enables

You type a prompt in Claude Cowork — *"Build me a calculator with dark theme"* — and a real, native Avalonia window appears on your desktop. Changes are hot-reloaded in place.

```
Claude Cowork/Code                  Neo.McpServer
+------------------+   JSON-RPC    +-----------------------------+
| "Create a        | ------------> | 1. Roslyn compiles C#       |
|  calculator"     |               | 2. Starts PluginWindow      |
|                  | <------------ | 3. Streams DLL over pipe    |
| "SUCCESS"        |               | 4. Live Avalonia UI appears |
+------------------+               +-----------------------------+
                                              |
                                   +----------v----------+
                                   | Neo.PluginWindow    |
                                   | Avalonia            |
                                   | (Desktop Window)    |
                                   +---------------------+
```

## Prerequisites

- .NET 9 runtime (not the SDK — the runtime is sufficient)
- The N.E.O. repository cloned and built

## Setup

### 1. Build the required projects

```bash
cd N.E.O
dotnet build Neo.McpServer -c Release
dotnet build Neo.PluginWindowAvalonia -c Release
```

Both `Debug` and `Release` builds work. Use `Release` for better performance. Just make sure both paths below use the same configuration you built with.

### 2. Configure Claude

Add the MCP server to your Claude settings.

**Claude Code** (`.claude/settings.json` or via `claude mcp add`):
```json
{
  "mcpServers": {
    "neo-preview": {
      "command": "dotnet",
      "args": ["/full/path/to/Neo.McpServer/bin/Release/net9.0/Neo.McpServer.dll"],
      "env": {
        "NEO_PLUGIN_PATH": "/full/path/to/Neo.PluginWindowAvalonia/bin/Release/net9.0"
      }
    }
  }
}
```

**Claude Desktop / Cowork** (MCP settings):
Same JSON structure — add via Settings > Extensions > Advanced, or edit the MCP config file directly.

> **Important:** Use absolute paths. Replace `/full/path/to/` with the actual path to your N.E.O. clone.

### 3. Verify

In Claude Code, you can verify with:
```
claude mcp list
```

The `neo-preview` server should appear with 4 tools.

## Available Tools

### `compile_and_preview`

Compiles C# Avalonia UserControl code and shows it in a live preview window.

**Parameters:**
- `sourceCode` (string[], required) — Complete C# source files. The main file must contain a class `DynamicUserControl : UserControl`.
- `nugetPackages` (object, optional) — Additional NuGet packages as `{ "PackageName": "version" }`. Avalonia packages are included automatically.

**Example call from Claude:**
```
compile_and_preview({
  sourceCode: ["using Avalonia.Controls;\nnamespace D;\npublic class DynamicUserControl : UserControl { ... }"],
  nugetPackages: { "LiveChartsCore.SkiaSharpView.Avalonia": "2.0.0-rc3.3" }
})
```

### `update_preview`

Hot-reloads modified code in the existing preview window. Same parameters as `compile_and_preview`. The preview window stays open — the user sees changes in place.

### `close_preview`

Closes the preview window. No parameters.

### `get_preview_status`

Returns the current state: whether a preview window is running, recent child process logs, and the Avalonia version.

## Available Prompts

### `create_avalonia_app`

A prompt template that teaches Claude the coding conventions for N.E.O. Avalonia UserControls:

- Class must be named `DynamicUserControl`
- No XAML — everything in C# code-behind
- Avalonia 11.3.12 with Fluent theme
- Thread-safe UI access via `Avalonia.Threading.Dispatcher.UIThread`
- Fully qualified dispatcher types to avoid naming conflicts

Claude uses this prompt automatically when the MCP server is connected.

## How It Works Internally

### Compilation (no SDK required)

The MCP server uses **Roslyn as an in-process library** (not the `dotnet` CLI). Reference assemblies are discovered from two sources:

1. **.NET runtime DLLs** — found via `dotnet --list-runtimes` (same approach as the N.E.O. host app)
2. **Avalonia DLLs** — taken directly from the `Neo.PluginWindowAvalonia` build output (set via `NEO_PLUGIN_PATH`)

This means **only the .NET runtime is needed**, not the full SDK. Compilation typically completes in under 2 seconds.

### Preview Window

The compiled DLL is sent to `Neo.PluginWindowAvalonia.exe` — the same child process used by the main N.E.O. application. It runs in **standalone mode** (`--standalone`), showing a decorated window titled "N.E.O. — Live Preview".

Communication uses the same **Named Pipes IPC protocol** (framed binary with blob streaming) as the main app. The DLL bytes are streamed directly into a `SandboxPluginLoadContext` — no files written to disk for the main assembly.

### NuGet Packages

If the generated code needs additional NuGet packages (beyond Avalonia), the MCP server resolves them using N.E.O.'s built-in `NuGetPackageLoaderAgent` — a custom NuGet resolver that works without the SDK.

Avalonia packages are **never downloaded** — they come from the local `NEO_PLUGIN_PATH` directory, which already contains all Avalonia assemblies.

## Project Structure

```
Neo.McpServer/
  Program.cs                        — MCP host with STDIO transport
  Services/
    CompilationPipeline.cs          — Roslyn + NuGet wrapper (runtime-only)
    PreviewSessionManager.cs        — PluginWindow lifecycle + Named Pipe IPC
  Tools/
    PreviewTools.cs                 — MCP tool definitions (4 tools)
    AvaloniaPrompt.cs               — MCP prompt for Avalonia coding conventions
```

## Troubleshooting

**Preview window doesn't appear:**
- Check that `NEO_PLUGIN_PATH` points to a directory containing `Neo.PluginWindowAvalonia.exe` (or `.dll` on Linux/macOS)
- Ensure `Neo.PluginWindowAvalonia` is built: `dotnet build Neo.PluginWindowAvalonia`

**Compilation fails with "No .NET reference assemblies found":**
- The .NET 9 runtime must be installed and `dotnet` must be on your PATH
- Run `dotnet --list-runtimes` to verify

**Compilation fails with Avalonia type errors:**
- Ensure `NEO_PLUGIN_PATH` contains Avalonia DLLs (e.g., `Avalonia.Base.dll`)
- Rebuild: `dotnet build Neo.PluginWindowAvalonia`

**NuGet resolution hangs:**
- Only non-Avalonia NuGet packages are downloaded. If it hangs, check your network connection.
- The first NuGet resolution may take longer as packages are cached locally.
