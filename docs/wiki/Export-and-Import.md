# Export and Import

## Exporting Your App

Click the **Export** button in the toolbar to open the Export Dialog.

### Export Options

- **Project Name**: Name for your exported application
- **Output Directory**: Where to save the exported files
- **Custom Icon**: Optional `.ico` file for the executable
- **Shortcuts**: Create Windows Start Menu and/or Desktop shortcuts (WPF host only)

### What Gets Exported

- Standalone `.exe` file (no .NET SDK required — only the .NET runtime on the target machine)
- All NuGet dependency DLLs
- Python runtime (if Python mode was enabled)
- Project metadata (`.resx` file for re-importing)

### Cross-Platform Export (Avalonia Only)

When using Avalonia mode, you can export for multiple platforms:

| Target | Output |
|--------|--------|
| Windows (win-x64) | `.exe` |
| Linux (linux-x64) | Binary executable |
| macOS (osx-arm64) | Binary executable |

Each platform gets its own AppHost template bundled with the project. The export dialog automatically selects the current platform as the default target.

### Running Exported Apps

**Windows:**
Double-click the `.exe` file, or run from a terminal:
```bash
.\MyApp.exe
```

**Linux:**
```bash
chmod +x ./MyApp
./MyApp
```
Or alternatively: `dotnet ./MyApp.dll`

**macOS:**
```bash
chmod +x ./MyApp
./MyApp
```

All exported apps require the [.NET Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (not the SDK) installed on the target machine.

### Export Size

The AI agent DLL filtering system automatically excludes AI provider libraries (Claude, OpenAI, Gemini, etc.) from the export unless your generated code explicitly references them. This keeps exports lean.

## Importing a Project

Click the **Import** button to load a previously exported `.resx` file.

### What Gets Restored

- Full C# source code
- NuGet package list (packages are re-downloaded)
- The app is recompiled and displayed immediately

### File Format

Projects are saved as `.resx` files containing JSON-encoded data:

```
MyApp.resx
├── Source code (all files)
├── NuGet package references
└── Project metadata
```

## Tips

- Export early and often — the `.resx` file is your project save file
- Use **Import** to share projects with others or move between machines
- The exported `.exe` only requires the .NET runtime — copy it to any machine with the runtime installed and run it
