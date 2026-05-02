# Live-MCP — every generated app is an MCP server

> Generated apps don't just run in N.E.O. — they can be MCP servers themselves. Claude Code can call into a running app's methods, read its state, even subscribe to property changes. Combined with [[Channels]] (`Ai.Trigger`) the loop is fully bidirectional: Claude drives the app, the app drives Claude.

## The shift

Until Live-MCP, a generated N.E.O. app was a **passive surface**: Claude wrote the code, the user clicked things, that was the conversation. With Live-MCP, the running app exposes its own methods and properties as first-class MCP tools and resources. Claude doesn't have to take a screenshot and guess — it can call `add_item("Buy milk")` directly, then read `ItemCount` to verify.

Three layers, all opt-in:

| Layer | Direction | How |
|---|---|---|
| **Call into the app** | Claude → app | `[McpCallable]` on methods → become MCP tools |
| **Read the app's state** | Claude → app | `[McpObservable]` on properties → readable, optionally subscribable |
| **Trigger Claude from the app** | app → Claude | `Ai.Trigger(prompt)` via [[Channels]] |

The first two are Live-MCP. The third is Channels. Together they close the loop.

## Two modes

### Dev-Mode — apps live inside the N.E.O. MCP server

You compile and preview an app via the existing `compile_and_preview` tool. The running app's `[McpCallable]` methods become MCP tools (`invoke_method`, plus dynamic per-method tools), `[McpObservable]` properties are readable via `read_observable`, watchable ones can be subscribed via `watch_observable`. No extra setup — register `neo-preview` once, every app you generate gets these capabilities for free.

### Frozen-Mode — apps live on their own

Export the same app with `mcpMode=true` and you get a single executable that **simultaneously is a GUI app and an MCP server**. Anyone can register it with `claude mcp add my-todo-app "C:/path/my-todo-app.exe" -- --mcp` — no N.E.O. installation needed on the consumer's machine. The app's methods, observables, and trigger calls all behave identically to Dev-Mode.

Full Frozen-Mode walkthrough: [[Frozen-Mode]].

## What it looks like in code

```csharp
using Neo.App;

public class DynamicUserControl : UserControl, INotifyPropertyChanged
{
    public ObservableCollection<TodoItem> Items { get; } = new();

    // Claude can read this
    [McpObservable("Total number of TODO items currently in the list.")]
    public int ItemCount => Items.Count;

    // Claude can read AND subscribe — gets pushed an update on every change
    [McpObservable("Number of completed TODO items.", Watchable = true)]
    public int CompletedCount => Items.Count(i => i.IsDone);

    // Claude can call this
    [McpCallable("Adds a new TODO item. Returns the new item count.")]
    public int AddItem(string title)
    {
        Items.Add(new TodoItem(title));
        return Items.Count;
    }

    [McpCallable("Marks the TODO item at the given index as completed.")]
    public void CompleteItem(int index) => Items[index].IsDone = true;

    public event PropertyChangedEventHandler? PropertyChanged;
}
```

That's the whole API surface. Three attributes plus normal C#. No protocol code, no wiring, no manifest. The MCP server (Dev-Mode or Frozen-Mode) reads the attributes via reflection and exposes everything Claude needs.

## What Claude does with it

A typical conversation:

> *"Add five sample tasks and complete the first two."*

Claude calls `inspect_app_api` once, sees `AddItem`, `CompleteItem`, `ItemCount`, `CompletedCount`. Then five `invoke_method("AddItem", "[\"task n\"]")` calls, two `invoke_method("CompleteItem", "[0]")`, and a `read_observable("CompletedCount")` to verify it's 2. No screenshot needed for verification, no fuzzy matching. The API is exact.

For *"watch the completion count and tell me when it hits 10"*, Claude opens a `watch_observable("CompletedCount")` subscription. The MCP server pushes a notification on every change, Claude wakes up exactly when 10 is hit, no polling.

## Combining with Channels — the full loop

When the user enables [[Channels]] (`claude --dangerously-load-development-channels server:neo-preview`), the third direction lights up: the running app can push prompts back to Claude via `Ai.Trigger(...)`.

A complete example:

```csharp
[McpCallable("Refresh data from the API and trigger Claude to summarise the diff.")]
public async Task RefreshAndSummarise()
{
    var oldSnapshot = CurrentItems.ToList();
    await ReloadFromApi();
    var newSnapshot = CurrentItems.ToList();

    // Claude wrote this code. Now Claude executes the next step too.
    Ai.Trigger(
        $"The data was just refreshed. Old count: {oldSnapshot.Count}, new count: {newSnapshot.Count}. " +
        $"Summarise the changes in a single sentence and write it into the StatusBlock via set_property.");
}
```

Claude can call `RefreshAndSummarise` (Live-MCP). Inside that method, the app calls back to Claude (Channels). Claude resolves the trigger by calling `set_property` (Live-MCP again). Same Claude, same session, three protocol hops.

## What this means for the N.E.O. story

N.E.O. used to be a tool that built apps. With Live-MCP it's a factory for MCP tools — every prompt produces something the entire MCP ecosystem can consume. Generate a small data manipulator, freeze it, share the EXE. Anyone with Claude Code can now use it the same way they use Slack, GitHub, or Excel-MCP.

The app you describe in a sentence is no longer a one-off demo. It's a tool you can ship.

## Status

- **Layer 1 (call into app):** complete in Dev-Mode and Frozen-Mode.
- **Layer 2 (observe state):** complete, including `Watchable` subscriptions.
- **Layer 3 (trigger Claude):** complete via [[Channels]] research preview.
- **Frozen-Mode export:** complete; adds ~5 MB to the export package.

All of the above is hobby-stage software. Test it, break it, file an issue if something blows up.

## Related pages

- [[Channels]] — how `Ai.Trigger` works under the hood
- [[Frozen-Mode]] — exporting a Live-MCP app as a standalone MCP server
- [[MCP Server]] — the host that drives Dev-Mode
- [[Architecture]] — where Live-MCP sits in the project tree
