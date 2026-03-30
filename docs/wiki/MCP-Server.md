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
dotnet build Neo.PluginWindowAvalonia.MCP -c Release
```

Both `Debug` and `Release` builds work. Use `Release` for better performance. Just make sure both paths below use the same configuration you built with.

> **Note:** `Neo.PluginWindowAvalonia.MCP` is the MCP variant with all MCP-specific IPC handlers plus the Smart Edit feature (Ctrl+K). The normal `Neo.PluginWindowAvalonia` is the slim variant for the standalone host app (Neo.App / Neo.App.Avalonia) without MCP features.

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
        "NEO_PLUGIN_PATH": "/full/path/to/Neo.PluginWindowAvalonia.MCP/bin/Release/net9.0",
        "NEO_SKILLS_PATH": "/full/path/to/your/neo-apps",
        "ANTHROPIC_API_KEY": "sk-ant-your-key-here"
      }
    }
  }
}
```

> `NEO_SKILLS_PATH` is optional — enables the App Skills Registry.
> `ANTHROPIC_API_KEY` is needed for the Smart Edit feature (Ctrl+K). API keys are inherited by child processes — add any keys you need here.

**Claude Desktop / Cowork** (MCP settings):
Same JSON structure — add via Settings > Extensions > Advanced, or edit the MCP config file directly.

> **Important:** Use absolute paths. Replace `/full/path/to/` with the actual path to your N.E.O. clone.

### 3. Verify

In Claude Code, you can verify with:
```
claude mcp list
```

The `neo-preview` server should appear with 21 tools.

## Available Tools (21)

### `compile_and_preview`

Compiles C# Avalonia UserControl code and shows it in a live preview window.

**Parameters:**
- `sourceCode` (string[], required) — Complete C# source files. The main file must contain a class `DynamicUserControl : UserControl`.
- `nugetPackages` (string, optional) — NuGet packages as JSON object string, e.g. `'{"Humanizer": "default", "Bogus": "35.6.1"}'`. Use `"default"` for latest stable version. Avalonia packages are included automatically.

**Example prompt in Claude Cowork:**
> "Create a calculator app with dark theme using the DynamicUserControl class."

### `update_preview`

Hot-reloads modified code in the existing preview window. Same parameters as `compile_and_preview`. The preview window stays open — the user sees changes in place. App state (timers, scroll positions) is reset with the new code.

### `capture_screenshot`

Takes a screenshot of the running preview window and returns it as a PNG image. Claude can **see** what the app looks like and suggest visual improvements. This creates a feedback loop: generate → compile → display → observe → refine.

No parameters. The preview must be running.

### `set_property`

Changes a single property on a running control **without recompilation**. The change is instant and preserves all app state — scroll positions, user input, timer state, everything.

**Parameters:**
- `target` (string, required) — Control to modify. Can be a Name (`"myButton"`), a type (`"TextBlock"` for first match), or type:index (`"TextBlock:2"` for third TextBlock).
- `propertyName` (string, required) — Property to change, e.g. `"Foreground"`, `"FontSize"`, `"Text"`, `"IsVisible"`, `"Opacity"`, `"Background"`, `"Margin"`, `"FontWeight"`.
- `value` (string, required) — New value. Examples: `"Red"`, `"#FF5500"`, `"24"`, `"Hello World"`, `"true"`, `"10,5,10,5"` (for Thickness/Margin), `"Bold"` (for FontWeight).

**Example prompt in Claude Cowork:**
> "Change the header text color to red and increase the font size to 48."

Claude will call `set_property` twice — one for `Foreground`, one for `FontSize`. Both changes are instant.

### `get_runtime_errors`

Returns runtime exceptions thrown by the generated app since the last `compile_and_preview`. Includes exception type, message, and stack trace. Claude can use this information to fix the code and call `update_preview` to hot-reload.

> **Note:** MCP is a request-response protocol — Claude cannot be notified automatically when an error occurs. The user must ask Claude to check for errors (e.g. "are there any runtime errors?").

No parameters.

### `export_app`

Exports the generated app as a **standalone executable** that runs without N.E.O., without the MCP server, and without the .NET SDK (only the .NET runtime is needed on the target machine).

**Parameters:**
- `sourceCode` (string[], required) — Same source code as `compile_and_preview`.
- `appName` (string, required) — Name for the exported app (used as folder name and window title).
- `exportPath` (string, required) — **Absolute path** to the export directory, e.g. `"C:/Users/heiko/Desktop"` or `"C:/tmp"`. A subfolder with the app name will be created.
- `platform` (string, optional) — Target platform: `"windows"` (default), `"linux"`, or `"osx"`. Cross-compilation is supported (e.g. build a Linux app on Windows).
- `nugetPackages` (string, optional) — Same format as `compile_and_preview`.

**Example prompt in Claude Cowork:**
> "Export this app as MyCalculator to C:/tmp for Windows."

The exported directory (~27 MB) contains the executable, all Avalonia DLLs, NuGet dependencies, and native libraries (SkiaSharp, HarfBuzz). Copy the folder to any machine with the .NET 9 runtime and it runs.

### `get_preview_status`

Returns the current state of the preview system: whether a window is running, recent IPC logs, runtime error summary, and the Avalonia version.

### `close_preview`

Closes the preview window and cleans up. No parameters.

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

### Screenshot Capture

When `capture_screenshot` is called, the MCP server sends a `CaptureScreenshot` command over the Named Pipe. The PluginWindow renders its content to an Avalonia `RenderTargetBitmap`, encodes it as PNG, and sends the bytes back. The MCP server returns it as an `ImageContentBlock` that Claude can analyze visually.

### Live Property Editing

The `set_property` tool sends a `SetProperty` command over the Named Pipe. The PluginWindow traverses the Avalonia visual tree to find the target control (by name, type, or index), looks up the property via `AvaloniaPropertyRegistry`, parses the value string into the correct type (brushes, colors, thickness, enums, primitives), and sets it. No assembly reload occurs — all app state is preserved.

### Runtime Error Collection

The PluginWindow has global exception handlers (`Dispatcher_UnhandledException`, `TaskScheduler_UnobservedTaskException`, `AppDomain_UnhandledException`) that catch all unhandled exceptions in the generated code. These are serialized and sent back over the Named Pipe as `Error` messages. The MCP server collects them in `_runtimeErrors`. Claude can call `get_runtime_errors` to read the stack traces and fix the code.

> **Note:** MCP is request-response only — Claude cannot be notified automatically when errors occur. The user must prompt Claude to check (e.g. "check for runtime errors").

### Export

The `export_app` tool compiles the user's source code together with an Avalonia app wrapper (window, FluentTheme, exception handler, assembly preloader) into a standalone executable using `CSharpCompileAgent` with embedded AppHost templates. All Avalonia DLLs, NuGet dependencies, and platform-specific native libraries (SkiaSharp, HarfBuzz) are copied to the export directory. The result is a ~27 MB folder that runs on any machine with the .NET 9 runtime.

Cross-compilation is supported: you can build a Linux or macOS app on Windows.

### Patch Preview

The `patch_preview` tool applies a unified diff to the last compiled source code and hot-reloads. Claude sends 5-10 lines of diff instead of 100+ lines of full code. Uses the existing `UnifiedDiffPatcher` from Neo.AssemblyForge with fuzzy context matching. NuGet packages carry over from the previous compilation automatically.

### Code Extraction (Reverse-Engineering)

The `extract_code` tool traverses the live Avalonia visual tree and generates clean, compilable C# source code reflecting the current state — including all changes made via `set_property` and `inject_data`. Supports Grid (RowDefinitions, ColumnDefinitions, attached properties), Border, StackPanel, WrapPanel, DockPanel, Canvas, ScrollViewer, and all common controls.

### UI Testing

The `run_test` tool checks assertions against the visual tree without modifying anything. Claude sends a JSON array of assertions with target, property, expected value, and optional operator (`=`, `!=`, `>`, `<`, `>=`, `<=`, `contains`, `exists`). Returns pass/fail summary with details.

### Web Bridge (Browser ↔ Avalonia)

The `start_web_bridge` tool starts an HTTP + WebSocket server inside the PluginWindow process using pure .NET BCL (`HttpListener` + `System.Net.WebSockets`). Claude creates both a native desktop app AND a web page — they communicate bidirectionally in real-time. Use `{{WS_URL}}` in the HTML as a placeholder for the WebSocket URL. `send_to_web` pushes messages to all connected browsers.

### Sessions and Skills

The `save_session` tool saves the current app state (source code, NuGet packages, WebBridge HTML) to a `.neo` JSON file. `load_session` restores it — auto-compiles and displays the app instantly, including WebBridge restart.

The **App Skills Registry** (`register_skill` / `unregister_skill`) enables a personal app ecosystem across conversations. Saved sessions are registered with keywords. The MCP prompt automatically includes available skills, so Claude can match user requests to existing apps and load them instantly instead of generating new code.

Requires `NEO_SKILLS_PATH` environment variable. Without it, the skills feature is inactive (no error).

## Project Structure

```
Neo.McpServer/
  Program.cs                        — MCP host with STDIO transport
  Services/
    CompilationPipeline.cs          — Roslyn + NuGet wrapper + export pipeline
    PreviewSessionManager.cs        — PluginWindow lifecycle + Named Pipe IPC
    SkillsRegistry.cs               — App skills registry (skills.json read/write)
  Tools/
    PreviewTools.cs                 — MCP tool definitions (21 tools)
    AvaloniaPrompt.cs               — MCP prompt with auto-injected skills list
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

**Smart Edit (Ctrl+K) shows "ANTHROPIC_API_KEY not set":**
- Add `ANTHROPIC_API_KEY` to the `env` section of your MCP server config. Cowork may not inherit User environment variables due to sandboxing.

**Build fails with locked DLL:**
- Close the preview window (or stop the MCP server in Cowork) before rebuilding. The running PluginWindow process locks its DLL.

## Smart Edit (Ctrl+K)

The MCP variant of the preview window (`Neo.PluginWindowAvalonia.MCP`) includes an embedded Claude chat overlay for modifying apps directly — without going through the MCP server or Cowork.

### How to Use

1. An app must be running in the preview window (via `compile_and_preview` or `load_session`)
2. Press **Ctrl+K** — the Smart Edit panel appears on the right side
3. Type a modification request: *"Make the header red and add a search bar"*
4. Press **Enter** — the app modifies itself:
   - Current code is extracted from the visual tree
   - Claude generates a patch or new code
   - Embedded Roslyn compiles it in ~1 second
   - The control hot-reloads — the change appears instantly
5. Press **Escape** to close the panel

### How It Works Internally

```
User types in ChatOverlay
  → ExtractCodeFromVisualTree (current app state)
  → ClaudeChat.SendAsync (Anthropic API with Avalonia system prompt)
  → Claude responds with patch or full code
  → UnifiedDiffPatcher.TryApply (or full replacement)
  → SmartCompiler.CompileAsync (embedded CSharpDllCompileAgent + Roslyn)
  → HandleLoadUserControlFromBytes (hot-reload)
  → App updates in real-time
```

### Requirements

- `ANTHROPIC_API_KEY` environment variable (set in MCP server config `env` section)
- .NET 9 runtime (for Roslyn compilation)

### Interaction with MCP Server

Smart Edit and the MCP server can both modify the same app. Changes made via Ctrl+K are visible to the MCP server — `extract_code` and `inspect_visual_tree` always read the current live state. The MCP prompt instructs Claude to call `extract_code` before modifying existing code, so it picks up Smart Edit changes automatically.
