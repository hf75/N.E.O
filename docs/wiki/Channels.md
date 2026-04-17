# Channels — generated apps that push prompts back

> **Research preview.** Available in the **MCP server** only, requires Claude Code CLI v2.1.80+ with a development flag. API is likely to change.

Neo's MCP server supports [Claude Code Channels](https://code.claude.com/docs/en/channels-reference) — a protocol that lets a running preview app push events back into the Claude Code session **without waiting for user input**. Claude automatically starts a new turn in response.

## Two event types

- **`runtime_error`** (system, always active) — Unhandled exceptions in the generated app are automatically pushed so Claude can react (usually: fix the bug and hot-reload).
- **`user_trigger`** (user-defined via `Neo.Trigger(prompt)`) — The generated code decides when to push. The payload is a complete natural-language instruction; Claude executes it as if the user had typed it.

## The `Neo.Trigger` API

The `Neo.App.Api` assembly is auto-referenced by every generated app. From inside a generated handler:

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

Click the button → `Neo.Trigger(...)` sends the prompt via MCP channel → Claude starts a new turn → executes the research using its tools → writes the result back into the running app via `set_property`. **No user input between the click and the answer.**

`Neo.ScheduleTrigger(TimeSpan delay, string prompt)` does the same thing after a delay — handy for timer-based behaviours.

## Example end-to-end

You say (in Claude Code):

> *"Build an app with a combobox of vacation destinations and a 'Research' button. When I click the button, research the selected country and write the result into a TextBlock named 'resultText'."*

Claude calls `compile_and_preview` with code that includes the `Neo.Trigger` call above. The window appears. You click the button. Claude gets a `user_trigger` event, executes the research, calls `set_property` on `resultText`, and the text appears in the same window — still alive, still responsive.

## What this enables

- **Self-healing apps.** Runtime errors turn into fix-it prompts without you noticing.
- **AI as runtime.** The generated UI is the thin shell; the behaviour is Claude.
- **Business logic as prompts.** "If the user selects X, fetch Y from the web and render Z." No pre-coding required.
- **Scheduled behaviours.** Chains of triggers can model polling loops.

## Costs and risks

- **Every trigger costs an API call.** Don't wire it to `MouseMoved`, `TextChanged`-on-every-keystroke, or anything high-frequency. User-initiated actions only.
- **Latency.** Trigger → new turn → tool calls → UI update is seconds, not milliseconds. Fine for "do the research" — terrible for "bounce a ball".
- **Prompt injection.** Any text the running app passes into a trigger ends up in Claude's context. Don't run untrusted generated code with channels enabled.
- **Single-window for now.** Multi-window routing of channel events is a future exercise.
- **Claude Code only (for now).** Claude Desktop / Cowork don't support channels yet.

## Enabling it

Channels aren't on the approved allowlist yet. Start Claude Code with:

```bash
claude --dangerously-load-development-channels server:neo-preview
```

The `server:neo-preview` part matches the MCP server's `name` in your Claude config. If you registered the MCP server under a different name, change it accordingly.

Once running, describe an app **and** the event behaviour you want ("when I click X, do Y"). Claude will include `Neo.Trigger(...)` calls automatically.

## Protocol references

- [Claude Code channels reference](https://code.claude.com/docs/en/channels-reference) — protocol details and limitations
- [[MCP Server]] — the surface that implements the MCP-side of the channel
- `Neo.App.Api` source in this repo — the `Neo.Trigger` and `Neo.ScheduleTrigger` implementations

---

**Why this isn't in the desktop host or the Web App:** both rely on Named Pipes or WASM runtime respectively, neither of which has a natural "push prompt back to Claude" path. The MCP variant is the only one that sits inside Claude Code's process boundary via JSON-RPC. Moving channels to the Web App would require a persistent websocket from the backend into the Claude Code CLI, which is not trivial and not currently on the roadmap.
