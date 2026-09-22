# App Network — apps that use each other

Every N.E.O. app running on a machine is on the **app network**: it can offer methods and values to the other apps, and it can use theirs. A preview in a desktop host, an exported [[Living Apps|living app]], a preview started by the [[MCP Server]] — they all take part, and the AI knows who is there when it writes the next app.

"Build a dashboard that shows the total from the Sales app and keeps it up to date." — ten seconds later a second window shows the total, and it follows every sale recorded in the first one.

## What an app offers

The same attributes that make an app drivable by Claude ([[Live-MCP]]) put it on the network:

```csharp
using Neo.App;

[AppInfo("Sales", "Records sales of the coffee bar and knows the daily total.")]
public class DynamicUserControl : UserControl
{
    [McpCallable("Records a sale of a product for an amount.")]
    public void AddSale(string product, decimal amount) { … }

    [McpCallable("The total of all sales so far.")]
    public decimal Total() => _rows.Sum(r => r.Amount);

    [McpObservable("How many sales were recorded.")]
    public int Count => _rows.Count;
}
```

- `[AppInfo(name, description)]` names the app on the network. Without it the app is known by the name its host gives it (the living app's title, else the preview window's title). The AI puts it on every app it writes.
- `[McpCallable]` methods can be called by other apps; they run on the app's UI thread (or off it with `OffUiThread = true`), with a timeout (`TimeoutSeconds`, default 30). Parameters and results are plain data — strings, numbers, lists, records — serialized as JSON.
- `[McpObservable]` properties can be read and watched.

## What an app uses

```csharp
var total = await Apps.CallAsync<decimal>("Sales", "Total");          // call, with result
await Apps.CallAsync("Inventory", "Reserve", sku, 3);                  // call, no result
var count = await Apps.ReadAsync<int>("Sales", "Count");               // read a value
_watch = Apps.Watch<int>("Sales", "Count", n => countText.Text = n.ToString()); // follow a value
if (Apps.IsRunning("Sales")) …                                         // is it there?
foreach (var app in Apps.Running()) app.Describe();                    // who is there, what do they offer
```

- Calls cross processes and are asynchronous — always `await`, never block the UI thread on them.
- `Watch` runs the callback on the thread that called it (the UI thread) with the current value right away and again on every change; the value is read twice a second. While the other app is not running nothing is reported; when it appears, watching resumes. Dispose the returned handle to stop.
- `AppNotFoundException`: no app of that name is running. `AppCallException`: the other app could not carry out the call — unknown method, wrong arguments, an exception in the method (its type and message), a timeout — the message says which.
- A chain of calls that runs in circles (A calls B calls A …) is cut off after 8 nested calls.

## What the AI knows

The desktop hosts put two things into every prompt: the rules above (`AppNetworkGuidance`, next to the hot reload rules) and a **RUNNING APPS** section listing the other apps on the machine right now — name, description, every method with its parameters and result type, every value. The AI uses exactly those names. The MCP server has the same rules in its prompts and a `list_running_apps` tool.

An app that is not running is not listed; ask for it anyway and the AI writes code that copes (a message in the UI, `Watch` that resumes by itself).

## How it works

- Each plugin window publishes the loaded app after every load and every live update (the offer may have changed), and takes it off the network when the app is unloaded or the window ends (`AppNetwork.Publish` / `Unpublish` in `Neo.App.Api`).
- A published app is a JSON file in `%LOCALAPPDATA%\Neo\AppNetwork\` (`NEO_APP_NETWORK_DIR` overrides the folder) and a named pipe `neo-app-<id>`. Listing reads the files and drops entries whose process is gone, so a crashed app leaves no ghost.
- A call is one JSON line in, one JSON line out over the pipe; the app dispatches it to its UI thread, awaits a `Task` result, and answers with the result or the reason it failed.
- Nothing goes through the host or the AI: the apps talk to each other directly, which is why exported living apps take part exactly like previews.

Limits: one machine (named pipes), no authentication between apps (everything runs as the same user), values are polled (twice a second) rather than pushed, and the web build (browser) is not on the network.

## Code map

| Piece | Where |
|---|---|
| Client API, descriptors, registry, server | `Neo.App.Api/Apps.cs`, `AppNetwork/AppDescriptor.cs`, `AppRegistry.cs`, `AppNetwork.cs` |
| Naming | `Neo.App.Api/AppInfoAttribute.cs` (`[McpCallable]`/`[McpObservable]` in `McpAttributes.cs`) |
| Publishing in the windows | `PublishOnAppNetworkAsync` in `Neo.PluginWindowAvalonia.Core/App.axaml.cs` and `Neo.PluginWindowWPF.Core/App.cs`; `--app-name` argument |
| Prompt | `Neo.AssemblyForge.Core/AppNetworkGuidance.cs`; `AppController.OtherRunningApps` (desktop, per prompt); `list_running_apps` in `Neo.McpServer/Tools/LiveMcpTools.cs` |
| Tests | `Neo.App.Api.Tests` (two apps in one process over the real pipes: calls, values, errors, timeout, recursion guard, watch, stale entries), `Neo.PluginWindow.SmokeTests/AppNetworkSmokeTests` (two real plugin windows per head: one app uses the other, live update changes the offer, unload) |
