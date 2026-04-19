# MCP Server (Claude Cowork / Claude Code)

N.E.O. includes an MCP (Model Context Protocol) server that lets **Claude Cowork** or **Claude Code** compile and display live **Avalonia** or **WPF** desktop apps on your screen — without running the full N.E.O. host application.

## What This Enables

You type a prompt in Claude Cowork — *"Build me a calculator with dark theme"* — and a real, native desktop window appears on your desktop. Changes are hot-reloaded in place.

With **[channel back-reporting](#channel-back-reporting-bidirectional-claude--app)** (Claude Code only, research preview) the generated app can also push events back to Claude — button clicks, timers, runtime errors — so Claude reacts without waiting for a user prompt. Jump to that section for details, or keep reading for the one-way basics.

Supports two UI frameworks:
- **Avalonia** (default) — cross-platform (Windows, Linux, macOS)
- **WPF** — Windows-only, for apps that need WPF-specific features

```
Claude Cowork/Code                  Neo.McpServer
+------------------+   JSON-RPC    +-----------------------------+
| "Create a        | ------------> | 1. Roslyn compiles C#       |
|  calculator"     |               | 2. Starts PluginWindow      |
|                  | <------------ | 3. Streams DLL over pipe    |
| "SUCCESS"        |               | 4. Live UI appears          |
+------------------+               +-----------------------------+
                                              |
                                   +----------v----------+
                                   | Neo.PluginWindow    |
                                   | Avalonia or WPF     |
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
dotnet build Neo.PluginWindowWPF.MCP -c Release      # optional, Windows-only, for WPF support
```

Both `Debug` and `Release` builds work. Use `Release` for better performance.

> **Note:** The `.MCP` variants include all MCP-specific IPC handlers plus the Smart Edit feature (Ctrl+K). The normal `Neo.PluginWindowAvalonia` / `Neo.PluginWindowWPF` are slim variants for the standalone host apps without MCP features.

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
        "ANTHROPIC_API_KEY": "sk-ant-your-key-here"
      }
    }
  }
}
```

> `ANTHROPIC_API_KEY` is needed for the Smart Edit feature (Ctrl+K). API keys are inherited by child processes — add any keys you need here.

**Optional environment variables:**

| Variable | Purpose | Required? |
|----------|---------|-----------|
| `NEO_PLUGIN_PATH` | Override Avalonia plugin window path | No — auto-discovered from dev-time paths |
| `NEO_PLUGIN_PATH_WPF` | Override WPF plugin window path | No — auto-discovered from dev-time paths |
| `NEO_SKILLS_PATH` | Directory for the App Skills Registry | No — skills feature inactive without it |

The MCP server automatically discovers plugin windows relative to its own location. Environment variables are only needed for non-standard deployments (e.g., self-contained publish to a different directory).

**Claude Desktop / Cowork** (MCP settings):
Same JSON structure — add via Settings > Extensions > Advanced, or edit the MCP config file directly.

> **Important:** Use absolute paths. Replace `/full/path/to/` with the actual path to your N.E.O. clone.

### 3. Verify

In Claude Code, you can verify with:
```
claude mcp list
```

The `neo-preview` server should appear with 25 tools.

## Available Tools (25)

> All tools accept an optional `windowId` parameter for multi-window mode. Use different IDs to create multiple windows. Omit for single-window (backward compatible).

### `compile_and_preview`

Compiles C# UserControl code and shows it in a live preview window. Supports both Avalonia and WPF.

**Parameters:**
- `sourceCode` (string[], required) — Complete C# source files. The main file must contain a class `DynamicUserControl : UserControl`.
- `nugetPackages` (string, optional) — NuGet packages as JSON object string, e.g. `'{"Humanizer": "default", "Bogus": "35.6.1"}'`. Use `"default"` for latest stable version. Avalonia packages are included automatically when using the Avalonia framework.
- `framework` (string, optional) — `"avalonia"` (default, cross-platform) or `"wpf"` (Windows-only). WPF apps use `System.Windows.Controls.UserControl` and don't need NuGet packages for the UI framework.

**Example prompts in Claude Cowork:**
> "Create a calculator app with dark theme." → Avalonia (default)
> "Create a WPF calculator app with dark theme." → Uses `framework: "wpf"`

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

### `patch_preview`

Applies a unified diff patch to the last compiled source code and hot-reloads. Much more efficient than `update_preview` — Claude sends only the changed lines instead of the full code. Uses fuzzy context matching. NuGet packages carry over automatically from the previous compilation.

**Parameters:**
- `patch` (string, required) — Unified diff text with `@@ hunk` headers targeting `./currentcode.cs`.
- `nugetPackages` (string, optional) — Only needed if adding new packages.

### `inspect_visual_tree`

Returns the complete Avalonia visual tree as structured JSON — all controls, types, names, key properties (text, colors, fonts, enabled state, item counts), pixel bounds, and nested hierarchy. Much more precise than a screenshot.

No parameters.

### `inject_data`

Pushes live data into controls without recompilation. Three modes:

**Parameters:**
- `target` (string, required) — For replace/append: the items control (`"myListBox"`, `"ListBox:0"`). For fill: `"root"` or a container name.
- `mode` (string, required) — `"replace"` (set new items), `"append"` (add to existing), or `"fill"` (set form control values).
- `dataJson` (string, required) — For replace/append: JSON array of objects. For fill: JSON object mapping control names to values.
- `autoTemplate` (bool, optional) — Auto-generate ItemTemplate if the control has none. Default: true.
- `focusFields` (string, optional) — Comma-separated field names to show in auto-template.

### `read_data`

Reads current data from controls. Auto-detects scope based on control type.

**Parameters:**
- `target` (string, required) — Control name, type, or `"root"`.
- `scope` (string, optional) — `"items"` (ItemsSource data), `"form"` (all named children values), or `"value"` (single control). Auto-detected if omitted.

### `extract_code`

Reverse-engineers the running app back to clean, compilable C# source code. Captures the current state — including all changes made via `set_property`, `inject_data`, and Smart Edit (Ctrl+K). Supports Grid, Border, StackPanel, WrapPanel, DockPanel, Canvas, ScrollViewer, and all common controls with their properties and attached properties.

No parameters.

### `run_test`

Runs UI test assertions against the visual tree without modifying anything. Returns pass/fail summary.

**Parameters:**
- `assertions` (string, required) — JSON array of assertions:
  ```json
  [
    {"target": "title", "property": "Text", "expected": "Hello"},
    {"target": "slider", "property": "Value", "operator": ">", "expected": "50"},
    {"target": "submitBtn", "operator": "exists", "property": "", "expected": ""}
  ]
  ```
  Operators: `=`, `!=`, `>`, `<`, `>=`, `<=`, `contains`, `exists`.

### `save_session`

Saves the current app session to a `.neo` JSON file. Includes source code, NuGet packages, and WebBridge HTML (if active).

**Parameters:**
- `name` (string, required) — Session name (used as filename).
- `directory` (string, required) — Absolute path to the save directory.

### `load_session`

Loads a `.neo` session file and auto-compiles the app. The preview window appears instantly. If the session included a WebBridge, it is restarted automatically.

**Parameters:**
- `path` (string, required) — Absolute path to the `.neo` file.

### `register_skill`

Registers a saved session as a reusable "skill" with keywords. Skills are remembered across conversations — Claude sees them in the system prompt and can load matching apps automatically.

**Parameters:**
- `name` (string, required) — Display name (e.g. "Email Reader").
- `description` (string, required) — Short description of what the app does.
- `keywords` (string, required) — Comma-separated keywords for auto-matching.
- `sessionPath` (string, required) — Absolute path to the `.neo` file.

Requires `NEO_SKILLS_PATH` environment variable.

### `unregister_skill`

Removes a skill from the registry. The `.neo` file is not deleted.

**Parameters:**
- `name` (string, required) — Name of the skill to remove.

### `start_web_bridge`

Starts an HTTP + WebSocket server inside the preview process. Claude creates both a native desktop app AND a web page — they communicate bidirectionally in real-time. Uses `{{WS_URL}}` placeholder in HTML for automatic WebSocket URL injection.

**Parameters:**
- `htmlContent` (string, required) — Complete HTML page with JavaScript WebSocket client code.
- `port` (int, optional) — Port number. Default: auto-detect.

### `send_to_web`

Sends a JSON message to all browsers connected to the web bridge via WebSocket.

**Parameters:**
- `message` (string, required) — JSON message to broadcast.

### `stop_web_bridge`

Stops the HTTP + WebSocket server. No parameters.

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

### `create_wpf_app`

A prompt template for WPF UserControls (Windows-only):

- Class must be named `DynamicUserControl`
- No XAML — everything in C# code-behind
- WPF on .NET 9 (no extra NuGet packages needed)
- Uses `System.Windows.*` namespaces
- Includes WPF-specific guidance (no `Spacing` on StackPanel, `Visibility.Collapsed`, `FontWeights.Bold`, etc.)

Claude uses these prompts automatically when the MCP server is connected.

## How It Works Internally

### Compilation (no SDK required)

The MCP server uses **Roslyn as an in-process library** (not the `dotnet` CLI). Reference assemblies are discovered from two sources:

1. **.NET runtime DLLs** — found via `dotnet --list-runtimes` (same approach as the N.E.O. host app). On Windows, the Windows Desktop runtime (WPF/WinForms) is also discovered automatically.
2. **Framework DLLs** — taken directly from the plugin window build output:
   - **Avalonia**: DLLs from `Neo.PluginWindowAvalonia.MCP` (auto-discovered or via `NEO_PLUGIN_PATH`)
   - **WPF**: DLLs from `Neo.PluginWindowWPF.MCP` (auto-discovered or via `NEO_PLUGIN_PATH_WPF`)

This means **only the .NET runtime is needed**, not the full SDK. Compilation typically completes in under 2 seconds.

### Preview Window

The compiled DLL is sent to the appropriate plugin window child process:
- **Avalonia**: `Neo.PluginWindowAvalonia.MCP.exe` — cross-platform
- **WPF**: `Neo.PluginWindowWPF.MCP.exe` — Windows-only

Both run in **standalone mode** (`--standalone`), showing a decorated window. The `framework` parameter on `compile_and_preview` determines which child process is launched.

Communication uses the same **Named Pipes IPC protocol** (framed binary with blob streaming) as the main app. The DLL bytes are streamed directly into a `SandboxPluginLoadContext` — no files written to disk for the main assembly.

### NuGet Packages

If the generated code needs additional NuGet packages (beyond Avalonia), the MCP server resolves them using N.E.O.'s built-in `NuGetPackageLoaderAgent` — a custom NuGet resolver that works without the SDK.

Framework packages are **never downloaded** — Avalonia DLLs come from the Avalonia plugin window output, and WPF assemblies ship with the .NET Windows Desktop runtime.

### Screenshot Capture

When `capture_screenshot` is called, the MCP server sends a `CaptureScreenshot` command over the Named Pipe. The PluginWindow renders its content to a `RenderTargetBitmap` (Avalonia or WPF variant), encodes it as PNG, and sends the bytes back. The MCP server returns it as an `ImageContentBlock` that Claude can analyze visually.

### Live Property Editing

The `set_property` tool sends a `SetProperty` command over the Named Pipe. The PluginWindow traverses the visual tree to find the target control (by name, type, or index), looks up the property (via `AvaloniaPropertyRegistry` for Avalonia or `DependencyProperty` reflection for WPF), parses the value string into the correct type (brushes, colors, thickness, enums, primitives), and sets it. No assembly reload occurs — all app state is preserved.

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
    CompilationPipeline.cs          — Roslyn + NuGet wrapper + export pipeline (Avalonia + WPF)
    PreviewSessionManager.cs        — PluginWindow lifecycle + Named Pipe IPC (framework-aware)
    SkillsRegistry.cs               — App skills registry (skills.json read/write)
  Tools/
    PreviewTools.cs                 — MCP tool definitions (25 tools, framework parameter)
    AvaloniaPrompt.cs               — MCP prompts (create_avalonia_app + create_wpf_app)

Neo.PluginWindowAvalonia.MCP/       — Avalonia preview child process (cross-platform)
Neo.PluginWindowWPF.MCP/            — WPF preview child process (Windows-only)
```

## Multi-Window Mode

All tools accept an optional `windowId` parameter. Use different IDs to create multiple preview windows simultaneously.

### `list_windows`

Lists all running preview windows with their IDs and status. No parameters.

### `close_all_windows`

Closes all running preview windows at once. No parameters.

### `position_window`

Sets a window's position and size on screen using absolute coordinates.

**Parameters:**
- `windowId` (string, required) — Which window to position.
- `x` (double, required) — X coordinate in pixels from left edge.
- `y` (double, required) — Y coordinate in pixels from top edge.
- `width` (double, required) — Window width in pixels.
- `height` (double, required) — Window height in pixels.

### `layout_windows`

Arranges all running windows in a predefined layout.

**Parameters:**
- `layout` (string, required) — Layout preset:
  - `"side_by_side"` — Windows split horizontally (equal width)
  - `"top_bottom"` — Windows stacked vertically (equal height)
  - `"left_half_right_stack"` — First window takes left half, rest stack on right
  - `"grid"` — Equal grid arrangement (auto-calculates rows/columns)

### Example: Multi-Window Data Analysis

```
Prompt 1: "Create a data table with windowId 'table'"
Prompt 2: "Create a chart with windowId 'chart'"
Prompt 3: "Arrange them side by side"
  → layout_windows(layout: "side_by_side")
Prompt 4: "Fill the table with data and update the chart"
  → inject_data(windowId: "table", ...)
  → inject_data(windowId: "chart", ...)
```

Windows persist across prompts within the same Cowork session. Claude can target specific windows for any operation (set_property, inject_data, capture_screenshot, etc.) using the windowId parameter.

## Troubleshooting

**Preview window doesn't appear:**
- Ensure the plugin window projects are built: `dotnet build Neo.PluginWindowAvalonia.MCP` (and `Neo.PluginWindowWPF.MCP` for WPF)
- The MCP server auto-discovers plugin windows relative to its own location. If using a non-standard layout, set `NEO_PLUGIN_PATH` / `NEO_PLUGIN_PATH_WPF`

**Compilation fails with "No .NET reference assemblies found":**
- The .NET 9 runtime must be installed and `dotnet` must be on your PATH
- Run `dotnet --list-runtimes` to verify

**Compilation fails with Avalonia type errors:**
- Rebuild: `dotnet build Neo.PluginWindowAvalonia.MCP`

**WPF preview doesn't work:**
- WPF is Windows-only — it won't work on Linux/macOS
- Ensure `Neo.PluginWindowWPF.MCP` is built: `dotnet build Neo.PluginWindowWPF.MCP`
- Check that Claude passes `framework: "wpf"` in the `compile_and_preview` call

**NuGet resolution hangs:**
- Only non-Avalonia NuGet packages are downloaded. If it hangs, check your network connection.
- The first NuGet resolution may take longer as packages are cached locally.

**Smart Edit (Ctrl+K) shows "ANTHROPIC_API_KEY not set":**
- Add `ANTHROPIC_API_KEY` to the `env` section of your MCP server config. Cowork may not inherit User environment variables due to sandboxing.

**Build fails with locked DLL:**
- Close the preview window (or stop the MCP server in Cowork) before rebuilding. The running PluginWindow process locks its DLL.

## Smart Edit (Ctrl+K)

Both MCP variants of the preview window (`Neo.PluginWindowAvalonia.MCP` and `Neo.PluginWindowWPF.MCP`) include an embedded Claude chat overlay for modifying apps directly — without going through the MCP server or Cowork.

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

## Channel back-reporting (bidirectional Claude ↔ app)

> **Research preview.** Claude Code CLI only (v2.1.80+), started with the development flag below.

Everything above describes a **one-way** flow: Claude pushes, the app displays. Channel back-reporting adds the reverse direction — the running preview app can push events **back** into the same Claude Code session, and Claude automatically starts a new turn in response. No user prompt needed in between.

This turns Neo into something qualitatively different from a normal previewer. Generated apps can carry almost no business logic of their own: Claude generates UI plus trigger calls, and then becomes the runtime that handles every interaction at request time.

### Two event types

- **`runtime_error`** (always on). Unhandled exceptions in the generated app auto-push to Claude — he typically reads the stack trace and fixes the bug via `patch_preview`.
- **`user_trigger`** (opt-in, via `Neo.Trigger(prompt)` in the generated code). The payload is a complete natural-language instruction; Claude executes it as if the user had typed it.

### Example

You say:

> *"Build an app with a combobox of vacation destinations and a 'Research' button. When I click the button, research the selected country and write the result into a TextBlock named `resultText`."*

Claude generates the UI **and** the trigger call:

```csharp
using Neo.App;

private void OnResearchClick(object sender, RoutedEventArgs e)
{
    var country = CountryCombo.SelectedItem?.ToString() ?? "(unknown)";
    Neo.Trigger(
        $"Research vacation destination {country}: best time to visit, " +
        $"top 3 sights, typical prices. Write the result with set_property " +
        $"into the TextBlock named 'resultText'.");
}
```

You click the button. `Neo.Trigger(...)` sends the prompt over the channel. Claude starts a new turn, does the research, calls `set_property` on `resultText`. The answer appears in the same window — no user input between the click and the result.

### How to run it

Channels aren't on Claude Code's approved allowlist yet. Start Claude Code with the development flag:

```bash
claude --dangerously-load-development-channels server:neo-preview
```

The `server:neo-preview` part matches the server's name in your `.claude/settings.json`. Then just describe an app **and** the event behaviour you want ("when I click X, do Y", "after 10 seconds, do Z") — Claude will include `Neo.Trigger(...)` calls automatically.

### Full details

Timer-based triggers, cost and latency considerations, prompt-injection risks, and why this pattern works only in the MCP variant (not desktop host or Web App): [[Channels]].

## Related pages

- [[Architecture]] — where Neo.McpServer fits into the overall project map
- [[Excel MCP]] — a second MCP server for live Excel access
- [[Web App]] — browser-hosted Neo variant (complementary, not MCP-based)
