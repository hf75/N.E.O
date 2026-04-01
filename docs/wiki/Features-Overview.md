# Features Overview

## The Interface

N.E.O. has two host applications with different layouts:

### WPF Host (Windows)

```
+---------------------+----------------------+
|                     |                      |
|   Chat History      |   Live Preview       |
|                     |   (Generated App)    |
|                     |                      |
+---------------------+                      |
|   [Toolbar Buttons] |                      |
+---------------------+                      |
|   Prompt Input      |                      |
|                     |                      |
+---------------------+----------------------+
```

The WPF host uses a side-by-side layout with the Live Preview embedded in the right half of the main window.

### Avalonia Host (Cross-Platform)

```
+---------------------+     +------------------+
|                     |     |                  |
|   Chat History      |     |  Live Preview    |
|                     |     |  (Separate       |
+---------------------+     |   Window)        |
|   [Toolbar Buttons] |     |                  |
+---------------------+     |                  |
|   Prompt Input      |     |                  |
|                     |     |                  |
+---------------------+     +------------------+
      Main Window             Preview Window
```

The Avalonia host uses a single-column layout. The Live Preview runs in a **separate window** with magnetic docking — it automatically snaps to the edge of the main window when dragged nearby.

### Common Elements

- **Chat History**: Shows your prompts, AI responses, and system messages
- **Toolbar**: Action buttons for all features
- **Prompt Input**: Type your prompts here
- **Live Preview**: Your generated application, running in real time

## Toolbar Buttons

From left to right:

| Button | Name | Description |
|--------|------|-------------|
| Import | Import | Load a previously saved `.resx` project file |
| Export | Export | Export your app as a standalone executable |
| Sandbox | Sandbox Controls | Toggle security sandbox options |
| Settings | Settings | Open the settings window |
| Designer | Designer Mode | Click-to-edit visual editing |
| Code | Code Editor | Open the built-in code editor |
| History | History | View branching undo/redo tree |
| Clear | Clear | Reset the session |

## AI Code Generation

### How It Works

1. You type a natural language prompt
2. The AI generates C# code (or a patch for existing code)
3. Required NuGet packages are automatically downloaded
4. Roslyn compiles the code to a DLL
5. The DLL is streamed to the preview process
6. Your app appears instantly

### Prompting Tips

- **Be specific**: "Create a file explorer with a tree view on the left and file list on the right" works better than "make a file manager"
- **Iterate**: Start simple, then add features in follow-up prompts
- **Reference elements**: "Make the submit button blue" — the AI understands context from previous code
- **Request NuGet packages**: "Create a chart using LiveCharts2" — the AI will include the right packages

### Response Types

The AI can respond in several ways:

- **Code**: Full C# source (used for first generation)
- **Patch**: Unified diff for incremental changes (preferred for updates)
- **Chat**: Text-only response for questions or clarifications
- **PowerShell**: System commands when needed
- **Console App**: Standalone executable for data processing tasks

## Code Editor

Toggle with the **Code Editor** button or **Ctrl+Shift+C**.

| Feature | WPF Host | Avalonia Host |
|---------|----------|---------------|
| C# syntax highlighting | Yes (AvalonEdit) | No |
| Line numbers | Yes | Yes |
| Direct editing | Yes | Yes |
| Apply / Revert | Yes | Yes |

## Undo/Redo History

N.E.O. uses a **branching history tree**, not a linear undo stack.

- **Ctrl+Shift+Z**: Undo — go back one step
- **Ctrl+Shift+Y**: Redo — go forward (opens History Rails if multiple branches exist)
- **History button**: Opens the visual history graph

When you undo and then make a new change, it creates a **branch** — your previous future states are preserved and you can return to them at any time.

## View Modes

### WPF Host

Cycle through view modes with **Ctrl+2**:

1. **Default**: All panels visible
2. **Prompt-Only**: Maximized prompt area, no preview
3. **Content-Only**: Full-screen preview (also via **Ctrl+Shift+F**)

### Avalonia Host

Cycle through view modes with **Ctrl+2**:

1. **Default**: All panels visible
2. **Prompt-Only**: Maximized prompt area

Use **F11** to toggle fullscreen mode.

## Sandbox Security

> **Note:** Sandbox security is only available on the **WPF host** (Windows). The Avalonia host does not include AppContainer sandboxing.

N.E.O. can run generated code in a **Windows AppContainer sandbox**:

1. Click the **Sandbox Hub** button to reveal security controls
2. Toggle **Secure Mode** to enable the sandbox
3. Optionally grant:
   - **Internet Access**: Allow network requests
   - **Folder Access**: Grant access to specific directories

When sandboxed, the generated app cannot access your files, registry, or network unless explicitly permitted.

## AI Provider Switching

Press **Ctrl+1** to cycle through available AI providers. The current provider is shown in the window title bar:

```
N.E.O. [...] [Claude]
```

Provider priority (default selection): Claude > Gemini > OpenAI > Ollama > LM Studio

## Python Integration

Enable Python in **Settings** to use Python code within your generated apps:

- Embedded Python 3.11 runtime (auto-downloaded on first use)
- `pythonnet` for seamless C#/Python interop
- Auto-install Python packages via pip at runtime
- Use `PythonHost` and `PythonModuleLoader` helper classes

## Framework Modes

Switch between frameworks in **Settings**:

| Mode | Setting | Best For |
|------|---------|----------|
| WPF (default) | — | Native Windows desktop apps |
| Avalonia | Enable Avalonia | Cross-platform apps (Win/Linux/macOS) |
| React | Enable REACT-UI | Web-based UIs, JavaScript ecosystem |

## MCP Server (Claude Cowork / Claude Code)

N.E.O. includes an MCP server (`Neo.McpServer`) with 21 tools that lets Claude Cowork or Claude Code compile and display live Avalonia apps directly on your desktop — without running the full N.E.O. host application.

**Core workflow:**
- Claude generates C# code and calls `compile_and_preview` — app appears in ~1 second
- `update_preview` hot-reloads, `patch_preview` applies minimal diffs
- `capture_screenshot` lets Claude see the app, `inspect_visual_tree` gives structural JSON
- `set_property` changes colors/fonts/text live without recompile
- `inject_data` / `read_data` push and pull data at runtime
- `extract_code` reverse-engineers the current visual state back to clean C#
- `run_test` checks UI assertions (pass/fail)
- `export_app` exports as standalone executable (Windows/Linux/macOS)

**Sessions and Skills:**
- `save_session` / `load_session` persist apps as `.neo` files
- `register_skill` / `unregister_skill` create a personal app ecosystem — Claude recognizes saved apps by keywords and loads them automatically in future conversations

**Web Bridge:**
- `start_web_bridge` serves an HTML page + WebSocket from the Avalonia process
- Browser and desktop app communicate bidirectionally in real-time
- No Node.js — pure .NET BCL

**Smart Edit (Ctrl+K):**
- The MCP preview window (`Neo.PluginWindowAvalonia.MCP`) includes an embedded Claude chat overlay
- Press **Ctrl+K** → type a change → the app modifies itself in real-time
- Uses embedded Roslyn for compilation and Claude API for code generation
- No MCP server or Cowork needed — the app is its own AI client

**Multi-Window:**
- All tools accept optional `windowId` for creating multiple windows
- `layout_windows` arranges them: side_by_side, top_bottom, left_half_right_stack, grid
- Windows persist across prompts — build a data table in one prompt, add a chart in the next
- Claude can target specific windows for inject_data, set_property, capture_screenshot, etc.

No SDK required — only the .NET 9 runtime. See [[MCP Server]] for full setup and documentation.
