# Frozen-Mode — N.E.O. apps as standalone MCP servers

> Phase 4 of the [Live-MCP roadmap](../../LIVE_MCP_VISION.md). Available since 2026-04-29 (lokal commitet, ungepusht).

## TL;DR

A generated N.E.O. app can be exported to a single-file Avalonia executable that *also* speaks stdio MCP. Anyone — even on a machine that has never heard of N.E.O. — can register it with `claude mcp add` and use its `[McpCallable]` methods and `[McpObservable]` properties as first-class MCP tools and resources. The app's manifest is identical to the one Dev-Mode produces, so behaviour is consistent across modes.

```
Dev-Mode (compile_and_preview)             Frozen-Mode (exported EXE)
─────────────────────────────              ────────────────────────────
       ┌─────────────────┐                       ┌──────────────────┐
       │  Claude Code    │                       │   Claude Code    │
       └────┬────────┬───┘                       └─┬────────┬───────┘
            │        │                             │ stdio  │ stdio
            ▼        ▲                             ▼        ▲
       ┌─────────────────┐                  ┌──────────────────┐
       │  Neo.McpServer  │                  │  my-todo-app.exe │
       └─┬───────┬───────┘                  │  ↳ Avalonia GUI  │
         │ pipe  │ pipe                     │  ↳ MCP server    │
         ▼       ▼                          └──────────────────┘
   ┌───────┐ ┌───────┐
   │ App A │ │ App B │     No N.E.O. needed on the consumer's machine.
   └───────┘ └───────┘
```

## Exporting an app with Frozen-Mode enabled

In Claude Code:

```
export_app(
    sourceCode=[<your DynamicUserControl source>],
    appName="my-todo-app",
    exportPath="C:/tmp",
    mcpMode=true              ← the new bit (Phase 4B)
)
```

What you get in `C:/tmp/my-todo-app/`:

- `my-todo-app.exe` — the executable.
- Avalonia runtime DLLs (already shipped by the legacy export).
- `Neo.App.Mcp.dll` plus the ModelContextProtocol + `Microsoft.Extensions.{Hosting,Logging,DependencyInjection}` closure (~5 MB extra).

Adds about **5 MB** to the export vs. `mcpMode=false`.

## CLI modes

The Frozen-Mode EXE branches on its argv:

| Invocation | Behaviour |
|---|---|
| `my-todo-app.exe` | GUI only, identical to a non-Frozen export. |
| `my-todo-app.exe --mcp` | GUI window **and** a stdio MCP server. Use this when registering with `claude mcp add`. |
| `my-todo-app.exe --mcp-help` | Dumps the manifest to stderr and exits. Cheap CI smoke test — no Avalonia window opens. |

`--mcp --headless` is reserved for a future iteration; today, `--mcp` always shows the window. If you need a true server-only run today, close the window once it appears — the embedded MCP server keeps running until stdin closes.

## Registering with Claude on another machine

Once the EXE is on the target machine:

```bash
claude mcp add my-todo-app "C:/path/to/my-todo-app.exe" -- --mcp
```

Claude Code re-fetches the tool list automatically (Phase 0 verified `tools/list_changed` works in the CLI), and the per-method tools (`add_item`, `complete_item`, …) plus `raise_event` and `simulate_input` become callable in the next turn.

For `[McpObservable(Watchable=true)]` properties, Claude opens a `resources/subscribe` against `app://<name>` and the embedded server pushes `notifications/resources/updated` on every property change (200 ms coalesce).

## What carries over from the source code

Everything the Phase 1–3 attribute set already exposes:

| Attribute | Becomes |
|---|---|
| `[McpCallable("…")]` on a method | One MCP tool named `<method_snake>` with a JSON-Schema-typed argument list. |
| `[McpObservable("…")]` | Property readable via the always-on `read_observable` tool. |
| `[McpObservable("…", Watchable = true)]` | Above, plus an MCP resource at `app://<name>` for `resources/subscribe` + push updates. |
| `[McpTriggerable("…")]` | Reserved for input-pipeline integration. Currently a manifest-only entry. |

Plus two static tools that work without any attributes:

- `raise_event(control_name, event_name)` — fires any Avalonia `RoutedEvent` (Click, KeyDown, TextInput, …) on a named control.
- `simulate_input(control_name, kind, params_json)` — kinds: `click` / `focus` / `set_text` / `key_press` / `type_text`.

## Differences vs. Dev-Mode

| Aspect | Dev-Mode | Frozen-Mode |
|---|---|---|
| Tool name prefix | `app.<windowId>.<method>` | plain `<method>` (one EXE = one app) |
| Resource URI | `app://<windowId>/<name>` | `app://<name>` |
| Hot-reload | Yes — `tools/list_changed` fires on every manifest change | No — manifest is frozen at startup |
| Loop protection | Server-side `LoopProtection` chain (max 5 hops) | Not enforced — single process, no Ai.Trigger channel |
| Multi-window | Yes (one preview window per `windowId`) | One window per process |

User-side code is identical between modes — the same `[McpCallable]`-decorated method behaves the same way whether driven by Claude through `compile_and_preview` or through a frozen EXE.

## Code signing (recommended for distribution)

A frozen EXE that the consumer registers with `claude mcp add` runs with the same trust as any local executable they launch. For distribution beyond your own machine, sign the binary so consumers can verify provenance:

**Windows (Authenticode):**

```bash
# Get a code-signing certificate (Sectigo, DigiCert, …) and import it.
signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /a my-todo-app.exe
```

**macOS / Linux:** signing is less standardised; see the Microsoft .NET single-file deploy docs for platform-specific guidance. The exported EXE is a stock .NET single-file app, so any general .NET signing flow applies.

**Recommendation:** ship a `SHA256SUMS` file alongside the EXE so consumers can verify integrity even without code-signing infrastructure.

## Limitations (Phase 4 scope, will be revisited)

- Single-file `dotnet publish --self-contained` is scaffolded in the FrozenTodoApp sample (`<NeoMcpFrozen>true</NeoMcpFrozen>`) but not yet wired into `export_app`. Today's exports require the .NET 9 runtime on the consumer machine.
- AOT publishing has not been validated; reflection-heavy paths in `AppManifestBuilder` and `InProcessDispatcher` may need `[DynamicallyAccessedMembers]` annotations before AOT works.
- `--mcp --headless` (server-only, no window) is not implemented in the wrapper. Workaround: launch with `--mcp` and minimise / close the window — the MCP server keeps running until stdin closes.
- `[McpTriggerable]` is recognised by the manifest builder but not yet used by either dispatcher.

## See also

- [Live-MCP Vision](../../LIVE_MCP_VISION.md) — the full roadmap, including Phase 5 (App-to-App Composability + Marketplace).
- [Channels](Channels.md) — the `Ai.Trigger` push channel, the foundation Frozen-Mode builds on.
- [MCP Server](MCP-Server.md) — the Dev-Mode counterpart that Frozen-Mode is structurally a peer of.
