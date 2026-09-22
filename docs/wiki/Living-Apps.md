# Living Apps

Every other builder ships frozen software. A living app keeps being built — by the person using it.

Export an app as a **living app** and its user gets a normal desktop app: their own window, their own title and icon, their data. Press **Ctrl+Shift+N** inside it and N.E.O. slides in beside the app. "Add a monthly chart", and ten seconds later the chart is in the window — the rows they entered are still there ([[Hot Reload]]), the change is saved, one click undoes it, and "Reset to original" brings the app back to the version that was exported.

Both desktop hosts export and play living apps. They differ in one thing: under the Avalonia host the app is its own window and N.E.O. docks *beside* it; under the WPF host the app fills N.E.O.'s window and the panel slides in *inside* it (the app is embedded there). The exported app runs on the host it was exported from, and the manifest records whether the app's code is Avalonia or WPF, so a host that can run both picks the right child.

## What the user sees

| | |
|---|---|
| Start | Only the app window, titled after the app. No splash screen, no N.E.O. |
| **Ctrl+Shift+N** | Avalonia host: N.E.O. appears docked beside the app (left, or right when there is no room) and follows the app window; closing the N.E.O. window puts it away too. WPF host: N.E.O.'s panel appears inside the window, beside the app. The same shortcut in either place puts it away again. |
| A prompt | Works exactly as in N.E.O.: the app is patched in place when possible, reloaded otherwise. Every version that reaches the window is saved to the project file, so the next start shows it. |
| Undo / History / Code editor | As in N.E.O. (`Ctrl+Shift+Z`, History, `Ctrl+Shift+C`). Import, Export and Clear are hidden — the app *is* the project. |
| ↺ Reset to original | Back to the exported version. The user's own changes are discarded (the history stays in the chat until the next start). |
| Closing the app window | Ends the app, N.E.O. included. (WPF host: Alt+F4 inside the embedded app is ignored; the window's own close button ends it.) |

The user needs the .NET 10 runtime (the app rolls forward to newer majors) and an AI: an API key, or a local model through Ollama / LM Studio. Without one, the key dialog appears at the first start — the same one N.E.O. shows.

## Exporting

**Export** → tick **Living app** → Export. The folder `<base>\<name>` then holds:

```
Notes\
  Notes.exe                 the N.E.O. host, renamed (the apphost finds Neo.App.Avalonia.dll by name)
  Notes.resx                the project the app plays and saves back to
  Notes.original.resx       what was exported — "Reset to original" copies it over Notes.resx
  Notes.ico                 window icon, when one was chosen
  neo.player.json           {"project": "Notes.resx", "original": "Notes.original.resx", "title": "Notes", "icon": "Notes.ico"}
  Neo.App.Avalonia.dll, Neo.PluginWindowAvalonia.exe, Roslyn, Avalonia, …   the host, trimmed
  runtimes\win-x64\         native libraries of this platform only
```

No separate executable is compiled for a living export; the standalone `.exe` export is the other option in the same dialog. Size is about 85 MB on Windows: the host without debug symbols (`.pdb`), documentation and the native libraries of the other operating systems, which are most of the host's 620 MB. The .NET runtime is not included.

Trying a project as its users will see it, without exporting: `Neo.App.Avalonia.exe --play C:\path\Notes.resx` or `Neo.App.exe --play …` (a `Notes.original.resx` next to it enables Reset). Without a manifest the framework is read from the project's code (`using Avalonia` / `using System.Windows`) — the two hosts share one settings file, and the last host used must not decide which child runs the code.

## How it works

The living app **is** N.E.O.: the same executable, started in **player mode**. At start-up the host looks for `neo.player.json` next to itself (or `--play` on the command line) — [`PlayerProject.Detect`](../../Neo.App.Core/PlayerMode.cs). In player mode:

- The host's main window is created but not shown, and it is not the lifetime's main window (the lifetime would show that one). Shutdown is explicit: when the app window closes, the host exits.
- The app window is started with `--title` and `--icon`, and the host tells it **not to dock** (`ParentWindowBoundsMessage.Dock = false`). The window reports its own bounds (`ChildWindowBounds`) so the host can dock beside *it*; `Ctrl+Shift+N` in the app window arrives as `ToggleHostWindow`.
- The project is loaded through the normal import; from then on `AppController.AutoSaveProjectPath` makes every successful build (`DisplayCompiledDllAsync`) write the project file.
- Builds go to `%LOCALAPPDATA%\Neo\Players\<name>\`, so a living app never compiles over a running N.E.O. or another living app.
- A closed pipe is a clean end (`EndOfStream` at a frame boundary returns `null` from the frame reader). The Avalonia host treats it as "the app was closed" in player mode (`TreatCloseAsCrash = false`) and as a crash with a recovery dialog in the normal mode, as before.
- WPF host: the window itself is the app. Player mode is the content-only view (`SetViewMode(ViewMode.ContentOnly)`) with the app's title and icon; `Ctrl+Shift+N` from the embedded app arrives as `ToggleHostWindow` on the pipe and switches the view. The WPF child ignores Alt+F4 while embedded (it used to close only the child process and leave the host with a broken pipe).

Code map: `Neo.App.Core/PlayerMode.cs` (detection, manifest, `LivingAppExporter`), `Neo.App.Avalonia/MainWindow.axaml.cs` (`RunAsPlayerAsync`, `ConfigurePlayerMode`, toggling and docking), `Neo.App.Avalonia/AvaloniaChildProcessService.cs` (`DockChildToHost`, `ChildWindowBounds`, `HostToggleRequested`, `ChildExited`), `Neo.App/MainWindow.xaml.cs` (`ConfigurePlayerWindow`, `RunAsPlayerAsync`, `TogglePlayerPanel`), `Neo.App/ChildProcessService.cs` (`HostToggleRequested`), the children's `App` (`--title`, `--icon`, bounds reports, `Dock`, `ToggleHostWindow`), `Neo.IPC/IPC.cs`.

Tests: `Neo.App.Core.Tests/PlayerModeTests` (detection, manifest, export layout, reset), `Neo.PluginWindow.SmokeTests/PluginWindowSmokeTests` (bounds reports, `Dock=false`, clean close). The end-to-end path — export through the real host, start the exported `.exe`, change it through the code editor, autosave, reset, close — was verified with UI automation against the real windows, for both hosts.

## Limits

- The user gets all of N.E.O.'s power, including the PowerShell agent and Python. The Avalonia host has no sandbox, and the WPF host's sandbox is off by default, so a living app runs its user's prompts with the user's rights. Keep this in mind before handing one to someone who does not know what they are asking for.
- Settings (model, keys, export path) are shared with a N.E.O. installation on the same machine.
- A new NuGet package the user asks for is downloaded at that moment (internet needed), as in N.E.O.
- Two living apps started at the same time each have their own build folder; the NuGet package folder is per app as well.
- Rude edits and crashes still lose the in-memory state, as described in [[Hot Reload]]; the data the app keeps in files is unaffected.
