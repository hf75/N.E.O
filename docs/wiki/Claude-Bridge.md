# Claude Bridge — channels for the Claude desktop app

In the terminal, an app that calls `Ai.Trigger("…")` starts a new turn in Claude Code ([[Channels]]). The Claude desktop app does not take channels, so there the prompt would be lost. The Claude bridge closes that gap from the outside: a small N.E.O. panel docked to the Claude window ([[Exoskeleton]]) types each prompt into the session's prompt box and sends it, as a person would.

Windows only. Built against the Claude desktop app 4.4212 (English).

## What you see

1. In a Claude Code session of the desktop app you build an app that uses `Ai.Trigger`. The server's instructions tell Claude to start the bridge then (`start_claude_bridge`) and to say so.
2. A small panel sits at the lower right of the Claude window: *N.E.O. bridge*, the session it serves, what it is doing, what waits.
3. The app calls `Ai.Trigger("Research Portugal …")`. In the session a message appears, and Claude starts working on it:

   ```
   From the N.E.O. app "default":
   Research Portugal …
   ```

   `default` is the window id of the app that sent it; Claude treats what follows as a `user_trigger` event.

## When it types

| The bridge types a prompt in only when | Otherwise |
|----------------------------------------|-----------|
| its session is the one in front | it waits, says which session is in front, and offers **Open session** |
| Claude is not working (no Stop button) | it waits until Claude is done |
| the prompt box is empty | your draft stays yours, the prompt waits until the box is empty |

Prompts wait in the panel, oldest first; one goes per turn. At most 20 wait: an app that fires faster loses its oldest prompts, and the panel counts them. When the box changes while the bridge writes into it (you typed in the same moment), the bridge pauses and leaves the box as it is. **Pause** stops it by hand, **Use the session in front** binds it to the session you see, for instance after a rename.

## Bringing the session to the front

With the switch **Bring the session to the front** in the panel, or `bringToFront: true` when Claude starts it, the bridge does not wait for you to switch. When a prompt waits and another session is in front, it opens its session's `claude://` link, and the Claude app shows the session. It does that only

- after nobody has used mouse or keyboard for 20 seconds, so that nothing you type ends up in Claude, and
- while the session in front has nothing in its prompt box.

When the link brings the session under another title (it was renamed), the bridge goes on with that title. A link that brings nothing is tried again after 30 seconds. The switch is off by default: it takes the screen from you, and it is meant for prompts that come while you are away, such as those of `Ai.ScheduleTrigger`.

## How it works

- The neo-preview server hands every `Ai.Trigger` of its apps to the bridge. It still sends the channel notification too, for the terminal.
- The bridge is an ordinary N.E.O. app built on `ForeignApp` ([[Exoskeleton]]). The server compiles it together with the helper's source and shows it in a window of its own, `claude-bridge`, which `close_all_windows` and `layout_windows` leave alone.
- It reads the Claude window through the Windows accessibility interface, as a screen reader does: the header button *"title, rename session"* says which session is in front, a *Stop* button says Claude is working, the field *Prompt* is the prompt box. It writes the field's value and presses *Send*. No mouse, no keyboard, no screenshots.
- It knows its session by the title and the `claude://` link that Claude passes from `get_session` with `self`. The link is the only thing **Open session** opens.
- The server calls the bridge directly, outside Live-MCP's loop protection, and offers Claude no tools of it.

```text
start_claude_bridge(sessionTitle: "N.E.O.", sessionLink: "claude://claude.ai/epitaxy/local_…", bringToFront: false)
close_preview(windowId: "claude-bridge")
```

## Limits

- It depends on how the Claude app names its controls (*Prompt*, *Send*, *Stop*, *…, rename session*). Another version or language of the app may name them differently; the panel then says what it waits for.
- One bridge serves one session: the session of the server that started it. It types only into that session while it is in front (or brings it there, see above), and with two sessions side by side it waits.
- While prompts wait, it looks at the Claude window once a second. Chromium keeps its accessibility tree switched on while something reads it.
- Runtime errors are not typed in, only `Ai.Trigger`.
- What it reads is the names of controls and whether the prompt box is empty, not the conversation. The risks are those of channels: whatever an app puts into a prompt ends up in Claude's context, and every prompt costs a turn.

## Code map

| What | Where |
|------|-------|
| The panel | `Neo.McpServer/Bridge/ClaudeBridge.cs` (compiled at run time, never into the server) |
| Starting and binding it | `Neo.McpServer/Tools/PreviewTools.ClaudeBridge.cs` |
| Handing prompts over | `PreviewSessionManager.HandToClaudeBridgeAsync` |
| Tests | `Neo.McpServer.Tests/ClaudeBridgeProcessTests.cs` (a stand-in Claude window that names its controls like the real one), `ClaudeBridgeTests.cs` |
