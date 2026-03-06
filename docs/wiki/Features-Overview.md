# Features Overview

## The Interface

N.E.O.'s main window is divided into three areas:

```
┌─────────────────────┬──────────────────────┐
│                     │                      │
│   Chat History      │   Live Preview       │
│                     │   (Generated App)    │
│                     │                      │
├─────────────────────┤                      │
│   [Toolbar Buttons] │                      │
├─────────────────────┤                      │
│   Prompt Input      │                      │
│                     │                      │
└─────────────────────┴──────────────────────┘
```

- **Chat History** (top-left): Shows your prompts, AI responses, and system messages
- **Toolbar** (center): Action buttons for all features
- **Prompt Input** (bottom-left): Type your prompts here
- **Live Preview** (right): Your generated application, running in real time

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

- Full C# syntax highlighting (AvalonEdit)
- Line numbers
- Direct editing of generated code
- **Apply**: Compile and apply your changes
- **Revert**: Discard changes

## Undo/Redo History

N.E.O. uses a **branching history tree**, not a linear undo stack.

- **Ctrl+Shift+Z**: Undo — go back one step
- **Ctrl+Shift+Y**: Redo — go forward (opens History Rails if multiple branches exist)
- **History button**: Opens the visual history graph

When you undo and then make a new change, it creates a **branch** — your previous future states are preserved and you can return to them at any time.

## View Modes

Cycle through view modes with **Ctrl+2**:

1. **Default**: All panels visible
2. **Prompt-Only**: Maximized prompt area, no preview
3. **Content-Only**: Full-screen preview (also via **Ctrl+Shift+F**)

## Sandbox Security

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
