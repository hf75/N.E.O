# Hot Reload

When you refine a running app with the next prompt, N.E.O. patches the new code into the **running** app instead of restarting it. The app instance survives — and with it everything you did: the Excel file you loaded, the rows you imported, the text you typed, the tab you had open, the ticked boxes, the scroll position. The new feature appears in the app you were already using.

This works in the desktop hosts (WPF and Avalonia, every way a new build reaches the preview: prompts, undo/redo, history jumps, designer edits) and in the MCP server (`update_preview`, `patch_preview`). `compile_and_preview` always starts the app fresh. The Web App reloads in the browser as before.

## What you see

- Desktop: an info line in the chat — `Hot reload (generation 3): the running app was patched in place, its state is preserved. …` — or, when a change cannot be patched, `Hot reload not possible — <reason>. Reloading the app.` followed by the old behaviour (a fresh start).
- MCP: the same line at the end of the `update_preview` / `patch_preview` result, so Claude knows whether the state is still there.

## How it works

```
 host (Neo.App / Neo.App.Avalonia / Neo.McpServer)                 plugin window (child process)
 ─────────────────────────────────────────────────                 ─────────────────────────────
 compile  →  debug DLL (PDB embedded) + <dll>.hotreload.json
 first load: unload/load as always, then HotReloadSession.StartAsync   (loaded from bytes, running)
 next change:
   HotReloadSession.CreateUpdateAsync(new sources)
     1. StateMigrationAnalyzer: what do the living objects miss?
     2. inject small helper methods into the new source
     3. Roslyn hot reload analysis → metadata/IL/PDB deltas   ── HotReload ──►  MetadataUpdater.ApplyUpdate
                                                                              run the state steps
                                                                              snapshot UI state
                                                                              call BuildUi() again
                                                                              put UI state back
                                            ◄── HotReloadResult ──  "UI rebuilt (5 of 6 values carried over)"
   failure anywhere → unload + load fresh (the old way)
```

1. **Deltas come from Roslyn's own hot reload engine** — `WatchHotReloadService`, the analysis behind `dotnet watch` and Visual Studio's Hot Reload (`Neo.HotReload/RoslynHotReloadService.cs`). It decides what can be patched, maps lambdas of the old code onto the new code (so a timer tick or `CollectionChanged` handler that is already subscribed runs the new body), and says precisely why an edit cannot be applied ("rude edit", e.g. `ENC0014` for a changed base class). The type is internal to Roslyn, so it is reached through reflection and the package version is pinned — `HotReloadSessionTests` fail loudly when a Roslyn update changes that API.
2. **The runtime applies them.** Plugin windows are started with `DOTNET_MODIFIABLE_ASSEMBLIES=debug` (the hosts set it), report their edit-and-continue capabilities in the Hello message, and call `MetadataUpdater.ApplyUpdate` on the plugin assembly (`Neo.Shared/HotReloadRuntime.cs`).
3. **Constructors and initializers never run again** for objects that already exist. `StateMigrationAnalyzer` (`Neo.HotReload`) makes up for that:
   - **new fields with initializers** (`List<string> _tags = new()`) are initialised on every live object of that type — otherwise the first use is a `NullReferenceException`;
   - **changed configuration** (a `readonly` string/colour/number, anything `static readonly`) is applied again — "make the accent blue" shows; mutable fields are your data and keep their value;
   - **statements added to the constructor** ("start the clock") run once on the live control;
   - **UI code**: when something that runs while the UI is built changed, the app's UI method (`BuildUi()`, `InitializeComponents()`, …) is called again. Before that, the window takes a snapshot of the visual tree (text, selection, checked state, expanded, scroll offset, focus — matched by control name and by position, so controls the designer renamed are found again) and puts it back into the new tree (`UiStateSnapshot.cs` per framework). Timers the UI method replaced are stopped instead of ticking twice.
4. **Anything that cannot be carried over safely ends in a full reload**, never in a half-updated app: rude edits, a constructor change that cannot be replayed, UI changes in an app without a re-runnable UI method, a changed set of references (a new NuGet package), a failure while applying.

## Writing apps that survive changes

The system prompts of the desktop hosts and the MCP server teach these rules (`Neo.AssemblyForge.Core/HotReloadGuidance.cs`):

- Build the complete UI in one parameterless method (`BuildUi()`, or `InitializeComponents()` in the desktop templates) that the constructor calls. It must be re-runnable: create all controls in it, never clear or reload data in it.
- Keep everything the user did or loaded in **fields** (loaded data, the path of an opened file, lists, the selection) — fields survive, controls are rebuilt.
- One-time set-up — timers, subscriptions to objects held in fields, background work — belongs in the constructor. Keep timers in fields.
- Keep names and types of existing fields and methods stable; add instead of renaming.
- Optional: `void OnHotReloaded()` is called after every live update.

## Limits

- Things Roslyn cannot patch into a running app (new base class/interface, changed method signatures in some cases, generic type changes, …) fall back to a fresh start. The message says which edit it was.
- A download or connection that is running while the code changes keeps running with the new code for everything it calls afterwards; code already on the stack finishes as the old version.
- Plugins are loaded into a **non-collectible** load context in processes that can hot reload: the runtime crashes when an update adds a static field to an assembly in a collectible context. Most changes now patch the running app; each *full* reload keeps the replaced assembly in memory until the preview window is restarted.
- Generated code is compiled without optimizations (edit-and-continue needs it). For UI apps the difference is not measurable.
- `capture_screenshot` shows the state after the update as usual; a native web view appears empty there (see [[MCP Server]]).

## Code map

| Piece | Where |
|---|---|
| Session per running app, update computation | `Neo.HotReload/HotReloadSession.cs`, `HotReloadCoordinator.cs` |
| What living objects miss, helper injection | `Neo.HotReload/StateMigrationAnalyzer.cs` |
| Roslyn's engine (reflection wrapper) | `Neo.HotReload/RoslynHotReloadService.cs` |
| Baseline next to every DLL | `Neo.Agents.CSharpDllCompile/HotReloadBaselineFile.cs` (`<dll>.hotreload.json`); the PDB is embedded in the DLL — Roslyn opens a separate PDB at the path recorded in the DLL and keeps it locked, which blocked the next compile to that path |
| Applying in the child | `Neo.Shared/HotReloadRuntime.cs`, `MainWindow.HandleHotReload` + `UiStateSnapshot` in both `PluginWindow*.Core` |
| IPC | `HotReload` / `HotReloadResult`, `HelloMessage.HotReloadCapabilities` in `Neo.IPC/IPC.cs` |
| Desktop wiring | `AppController.DisplayCompiledDllAsync` |
| MCP wiring | `PreviewSessionManager.UpdateAsync` |
| Tests | `Neo.HotReload.Tests` (engine), `Neo.PluginWindow.SmokeTests/DesktopHotReloadSmokeTests` (desktop heads), `Neo.McpServer.Tests/HotReloadProcessTests` (server end to end) |
