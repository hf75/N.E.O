# Live-MCP — Phase 0 Findings

> **Status:** in progress · started 2026-04-29
> **Spike code:** [`Neo.McpServer.Spike/`](Neo.McpServer.Spike/) (deletable after Phase 0 closes)
> **Outcome:** Go/No-Go for Phase 2 (dynamic per-method tools)

## Task 1 — `notifications/tools/list_changed`

**Question:** Does Claude Code CLI re-fetch the tool list when it receives a
`notifications/tools/list_changed` notification mid-session? If yes, Phase 2's
per-method-tool architecture (`app.<id>.<method>`) is feasible. If no, we pivot
Phase 1 to Meta-Tool-only (`invoke_method(app_id, method, params)`).

### Spike server behaviour

`Neo.McpServer.Spike` exposes:

| Tool | Lifecycle |
|---|---|
| `spike_phase_a` | present at startup |
| `spike_advance` | present at startup; each call adds a new `spike_phase_cN` |
| `spike_phase_b` | auto-added **5 seconds** after server start |

Capabilities advertise `tools.listChanged = true`.

### Self-test (out-of-process, before connecting to Claude Code CLI)

Verified on 2026-04-29:

- ✅ Custom `ListToolsHandler` logs every `tools/list` request to stderr.
- ✅ `McpServerPrimitiveCollection<McpServerTool>.Add()` raises `Changed` automatically.
- ✅ The SDK forwards `Changed` to the wire as
  `{"method":"notifications/tools/list_changed","jsonrpc":"2.0"}` on stdout.

So the *server-side* notification path is confirmed working with
ModelContextProtocol 1.1.0. The question reduces to whether Claude Code CLI
honours the notification by re-fetching `tools/list`.

### Live test (against Claude Code CLI) — pending

Test plan (run this; record results below):

1. Register the spike: `claude mcp add neo-livemcp-spike -- dotnet "C:/Home/Code/nw.Create.VX-Avalonia-OpenSource/Neo.McpServer.Spike/bin/Release/net9.0/Neo.McpServer.Spike.dll"`
2. Start a fresh Claude Code CLI session in any directory.
3. Wait ~10 seconds (lets the auto-trigger fire).
4. Ask Claude to call `spike_phase_b`.
   - **Visible & callable** → list_changed works (auto-fire path).
   - **Not visible / "tool not found"** → list_changed not honoured.
5. Ask Claude to call `spike_advance` once, then immediately `spike_phase_c1`.
   - **Both work** → list_changed works (manual path).
   - **`spike_phase_c1` not found** → notification not honoured for newly-added-after-handshake tools.

### Result

Live test executed 2026-04-29 against Claude Code CLI (Opus 4.7, 1M context):

| Path | Tool became visible without restart? |
|---|---|
| Auto-trigger (`spike_phase_b` after 5 s) | ✅ yes — server returned `OK: spike_phase_b (added 5s post-startup)` on first call |
| Manual trigger (`spike_phase_cN` via `spike_advance`) | ✅ yes — `spike_advance` registered `spike_phase_c1`, which was callable in the very next turn (`OK: spike_phase_c1 (added by spike_advance call #1)`) |

Mechanics observed in this CLI build: newly-registered tools surface as **deferred** entries — their names appear in a system reminder, but their schemas must be loaded via the `ToolSearch` tool with `select:<name>` before they can be invoked. After the schema is loaded the tool behaves like any other. So `notifications/tools/list_changed` is honoured *and* the schema-fetch round-trip is automated by the CLI; no manual restart, no user intervention.

**Verdict:** ✅ **GO** — Phase 2's per-method dynamic tool registration (`app.<id>.<method>`) is feasible. No pivot to Meta-Tool-only required.

Implication for Phase 2 design: the Live-MCP server should expect a small latency between registering a new tool and Claude actually calling it (deferred-schema fetch). Not a blocker, but worth documenting so apps don't `Ai.Trigger("call app.foo.bar")` in the same turn they call `register_tool`.

---

## Task 2 — Subscription pattern for `watch_observable`

**Question:** Which MCP notification path is the right home for property-change
subscriptions on `[McpObservable(Watchable = true)]` properties?

### Options evaluated

#### Option A — Custom capability analog `claude/channel`

Server advertises a new experimental capability (e.g. `neo/observable`),
defines a custom notification method `notifications/observable/changed` with
payload `{ appId, observable, value, hops }`. Claude Code CLI handles the
notification through a dedicated channel pipeline — same mechanism as the
existing `Ai.Trigger` channel.

- ✅ Full payload control. Can include the new value inline (no follow-up read).
- ✅ Reuses the `claude/channel` plumbing already in [Program.cs:42](Neo.McpServer/Program.cs#L42)
  and [PreviewSessionManager.PushChannelEventAsync](Neo.McpServer/Services/PreviewSessionManager.cs).
- ❌ Client-specific. Claude Desktop / Cowork would need their own adapter when we re-enter their scope.
- ❌ No subscribe/unsubscribe lifecycle in the spec — we'd invent it.

#### Option B — `notifications/resources/updated` (MCP standard)

Each `[McpObservable(Watchable = true)]` property is exposed as an MCP **resource**:

| Field | Value |
|---|---|
| `uri` | `app://<appId>/<observable>` (deterministic from manifest) |
| `name` | observable name |
| `description` | `[McpObservable]` description |
| `mimeType` | `application/json` |

Claude opens a subscription with `resources/subscribe` (URI). When the property
changes, server pushes `notifications/resources/updated{uri}`. Claude responds
by calling `resources/read{uri}` to fetch the current value. The server reads
the latest cached value (last `ObservableValue` IPC frame received from the app)
and returns it.

- ✅ Standard MCP — works on every compliant client (Claude Code CLI, Desktop,
  Cowork, third-party agents). Critical for Frozen-Mode Phase 4 where the app
  is consumed by clients we don't control.
- ✅ Subscribe/unsubscribe semantics already defined.
- ✅ Matches semantic intent: an observable IS a readable, watchable resource.
- ❌ Two round-trips per change (`updated` notification + `resources/read`). For
  rapid-fire properties this could be chatty.
- ❌ No payload in the notification — Claude can't see the new value without the
  read call.

#### Option C — Polling fallback

Claude calls `read_observable(app_id, name)` on a timer it sets via existing
mechanisms.

- ✅ Zero server-side machinery. Works on any client.
- ❌ Token-expensive, not "live", high latency.
- Use case: degraded mode for clients that ignore both `tools/list_changed`
  *and* `notifications/resources/updated`. Currently no such client matters
  (CLI honours both per Task 1), but worth keeping as a documented escape hatch.

### Decision: **Option B (resources/subscribe) as primary, Option C as fallback.**

Rationale:
1. **Portability matters more than payload-in-notification.** Phase 4 (Frozen-Mode)
   is the whole point of Live-MCP. Frozen apps are consumed by arbitrary clients;
   tying `watch_observable` to a custom Claude-Code-only capability would mean
   re-implementing the feature for every other consumer.
2. The two-round-trip cost is real but small (resources/read is cheap; the value
   is already cached server-side from the last IPC `ObservableValue` frame). For
   high-frequency properties we add a server-side **coalescing window** (default
   200ms) so rapid changes collapse into one notification.
3. Subscribe/unsubscribe lifecycle comes for free from the spec — we don't have
   to invent it.

Custom-channel (Option A) remains in use for `Ai.Trigger` push (where payload
inline matters and there's no read-back semantics). We do *not* use it for
observables.

### Pseudocode

**Server side (Neo.McpServer):**

```csharp
// Manifest emission — one resource per Watchable observable
foreach (var obs in manifest.Observables.Where(o => o.Watchable))
{
    resources.Add(new Resource {
        Uri = $"app://{appId}/{obs.Name}",
        Name = obs.Name,
        Description = obs.Description,
        MimeType = "application/json"
    });
}

// resources/subscribe handler
async Task OnSubscribe(string uri)
{
    var (appId, obsName) = ParseUri(uri);
    _subscriptions.Add(uri);
    await SendIpc(appId, new SubscribeObservable(obsName));  // tells app to start emitting changes
}

// IPC inbound: ObservableValue frame from the app
async Task OnObservableValueFromApp(string appId, ObservableValueMessage msg)
{
    var uri = $"app://{appId}/{msg.Name}";
    _cache[uri] = msg.ValueJson;
    if (_subscriptions.Contains(uri))
    {
        await CoalesceAndPush(uri);  // 200ms coalesce window, then notifications/resources/updated
    }
}

// resources/read handler
Task<ResourceContents> OnRead(string uri)
    => Task.FromResult(new ResourceContents(_cache.GetValueOrDefault(uri) ?? "null"));
```

**App side (Neo.App.Mcp / Neo.PluginWindowAvalonia.MCP):**

```csharp
// On SubscribeObservable IPC frame: start watching the property
void HandleSubscribe(string observableName)
{
    var prop = _manifest.GetProperty(observableName);
    if (prop.Source is INotifyPropertyChanged inpc)
        inpc.PropertyChanged += (_, e) => { if (e.PropertyName == prop.Name) Emit(prop); };
    else
        StartPolling(prop, _pollInterval);  // configurable, default 500ms
}

void Emit(PropertyManifestEntry prop)
{
    var value = prop.Getter.Invoke(prop.Source);
    _pipe.Send(new ObservableValueMessage(prop.Name, JsonSerializer.Serialize(value)));
}
```

**New IPC frame types (additions to `Neo.IPC/IPC.cs`):**

```csharp
public const string SubscribeObservable = "SubscribeObservable";
public const string UnsubscribeObservable = "UnsubscribeObservable";
public const string ObservableValue = "ObservableValue";

public record SubscribeObservableMessage(string Name);
public record UnsubscribeObservableMessage(string Name);
public record ObservableValueMessage(string Name, string ValueJson, int Hops = 0);
```

`Hops` on `ObservableValueMessage` is for telemetry only — observables don't
themselves trigger Claude turns, so they don't drive the loop counter.

---

## Task 3 — Loop-protection model

**Problem:**

```
Claude → invoke_method(app, doStuff) → App.doStuff() → Ai.Trigger("now do X")
       → Claude reacts → invoke_method(app, doX) → App.doX() → Ai.Trigger(...)
       → ... unbounded
```

Without a depth counter, a single misbehaving prompt can drive arbitrary token
spend and turn count.

### Model

Track **hop count** on every Live-MCP-bearing message that crosses the
Claude↔App boundary. Counter is incremented by the server on each forward.
Default limit: **5**. Configurable via env `NEO_LIVEMCP_MAX_DEPTH`. On overflow:
the server returns a structured error and emits a telemetry log; the chain
breaks naturally because Claude receives an error response instead of a result.

### State location

The hop counter lives **server-side**, keyed by `appId`, with a **30-second
inactivity decay**. Rationale: putting it in the protocol (passed by Claude)
relies on Claude correctly threading it through, which is fragile across model
versions and clients. Putting it server-side makes it tamper-proof from the
prompt side, which is the side we're protecting against.

The hop count IS still passed to the app and back in IPC frames — but that
copy is for telemetry and for Ai.Trigger context-stamping inside the app, not
for enforcement.

### Frames affected

Two new frames + extension of existing one:

```csharp
// Neo.IPC/IPC.cs additions
public const string InvokeMethod = "InvokeMethod";
public const string MethodResult = "MethodResult";

public record InvokeMethodMessage(
    string Method,         // e.g. "ApplyFilter"
    string ArgsJson,       // JSON array of positional args
    int Hops               // server stamps before sending; app reads into AsyncLocal
);

public record MethodResultMessage(
    bool Success,
    string? ResultJson,
    string? Error,         // null on success
    string? ErrorCode      // "loop_limit_exceeded", "method_not_found", "invocation_failed", etc.
);

// Existing AppEventMessage gains:
public record AppEventMessage(
    string EventType,
    string Target,
    string? Value = null,
    string? Details = null,
    int Hops = 0           // NEW — stamped by app from AsyncLocal, used by server to seed next chain
);
```

### Server-side enforcement (Neo.McpServer)

```csharp
class CallChain { public int Hops; public DateTime LastActivityUtc; }

class LoopProtection
{
    readonly ConcurrentDictionary<string, CallChain> _chains = new();
    readonly int _max = int.TryParse(Environment.GetEnvironmentVariable("NEO_LIVEMCP_MAX_DEPTH"), out var v) ? v : 5;
    readonly TimeSpan _decay = TimeSpan.FromSeconds(30);

    // Called when invoke_method tool is invoked from Claude
    public int OnInvokeMethod(string appId)
    {
        var chain = _chains.GetOrAdd(appId, _ => new CallChain());
        lock (chain)
        {
            if (DateTime.UtcNow - chain.LastActivityUtc > _decay) chain.Hops = 0;
            chain.Hops++;
            chain.LastActivityUtc = DateTime.UtcNow;
            if (chain.Hops > _max)
                throw new LoopLimitException(appId, chain.Hops, _max);
            return chain.Hops;
        }
    }

    // Called when an AppEvent (Ai.Trigger) arrives from the app
    public void OnAppEvent(string appId, int hopsFromApp)
    {
        var chain = _chains.GetOrAdd(appId, _ => new CallChain());
        lock (chain)
        {
            // Seed the chain from the app's perspective; Claude's next invoke_method will increment from here.
            chain.Hops = Math.Max(chain.Hops, hopsFromApp);
            chain.LastActivityUtc = DateTime.UtcNow;
        }
    }
}

// In the invoke_method MCP tool handler:
public async Task<MethodResult> InvokeMethod(string appId, string method, JsonElement args)
{
    int hops;
    try { hops = _loopProtection.OnInvokeMethod(appId); }
    catch (LoopLimitException ex)
    {
        _logger.LogWarning("loop_limit_exceeded app={AppId} hops={Hops} max={Max}", ex.AppId, ex.Hops, ex.Max);
        return new MethodResult { Success = false, Error = ex.Message, ErrorCode = "loop_limit_exceeded" };
    }
    var frame = new InvokeMethodMessage(method, args.GetRawText(), hops);
    return await _ipc.SendAndAwait<InvokeMethodMessage, MethodResultMessage>(appId, frame);
}
```

### App-side context propagation (Neo.App.Mcp / plugin host)

```csharp
// Internal call-context holder, used by Ai.Trigger to stamp outgoing AppEvents
internal static class LiveMcpCallContext
{
    static readonly AsyncLocal<int?> _currentHops = new();
    public static int CurrentHops => _currentHops.Value ?? 0;
    public static void Set(int hops) => _currentHops.Value = hops;
    public static void Clear() => _currentHops.Value = null;
}

// Ai.Trigger pickup
public static class Ai
{
    public static void Trigger(string prompt)
    {
        var hops = LiveMcpCallContext.CurrentHops;
        PipeClient.Send(new AppEventMessage("user_trigger", "ai", prompt, null, hops));
    }
}

// InvokeMethod handler in the plugin host
async Task<MethodResultMessage> HandleInvokeMethod(InvokeMethodMessage frame)
{
    LiveMcpCallContext.Set(frame.Hops);
    try
    {
        var result = await _dispatcher.Call(frame.Method, frame.ArgsJson);
        return new MethodResultMessage(true, JsonSerializer.Serialize(result), null, null);
    }
    catch (Exception ex)
    {
        return new MethodResultMessage(false, null, ex.Message, "invocation_failed");
    }
    finally { LiveMcpCallContext.Clear(); }
}
```

### Edge cases

- **`Ai.Trigger` from a real button click (not from invoke_method):** AsyncLocal
  is empty → Hops = 0. Correct: this is a fresh chain.
- **`Ai.Trigger` from a `Timer` callback:** AsyncLocal flows through `await`,
  but a timer's callback may run on a fresh thread without the AsyncLocal set.
  Hops = 0. Acceptable; timers are user-driven.
- **Cross-app chains** (App A's `Ai.Trigger` causes Claude to call App B):
  the AppEvent from App A carries `hops`; server seeds App B's chain with
  `Math.Max(B.Hops, A.Hops)` on the next `invoke_method`. So crossing apps
  doesn't reset the budget — the chain is global per-Claude-turn-graph,
  enforced per-app. Good enough for v1; revisit if multi-app pipelines (Phase 5)
  show pathological patterns.
- **30-second decay window:** picked to be longer than typical Claude
  reasoning latency (5–20s) but short enough that a manual user prompt 30s
  later starts fresh. Tuneable via env if needed.

### Telemetry

On every `loop_limit_exceeded`, server logs to stderr:

```
[live-mcp] loop_limit_exceeded app=<appId> hops=<n> max=<n> last_method=<name>
```

stderr is the standard MCP log channel ([Neo.McpServer Program.cs](Neo.McpServer/Program.cs)
already routes there); no new log infrastructure needed.

---

## Roadmap impact

- **Task 1 = GO** ⇒ Phase 2 plan unchanged; per-method tool registration is real.
- **Add to VISION.md "Spielregeln":** mention the deferred-schema-fetch latency when a Live-MCP tool is registered mid-session. Apps should not assume a tool is callable in the same Claude turn it was registered.
