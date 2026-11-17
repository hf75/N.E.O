# Claude Agent — a live Claude Code session for .NET

`Neo.ClaudeAgent` keeps one Claude Code running and talks to it turn by turn. Anthropic's Claude Agent SDK does this for TypeScript and Python; there is no official SDK for .NET, so this library is the part N.E.O. needs. Neo.Stage, the harness for speaking and pointing that is being built, uses it as its brain.

## Using it

```csharp
using Neo.ClaudeAgent;

await using var session = await ClaudeAgentSession.StartAsync(new ClaudeAgentOptions
{
    Model = "claude-haiku-5-5",
    Effort = "low",
    SystemPrompt = "Antworte knapp auf Deutsch …",   // replaces Claude Code's own prompt
    Restricted = true,                               // no tools that run commands, no settings files
    ToolSearch = false,                              // every tool's description up front
    WorkingDirectory = workFolder,
    McpServers = new Dictionary<string, ClaudeAgentMcpServer>
    {
        ["neo-preview"] = new ClaudeAgentStdioServer(@"…\Neo.McpServer.exe"),
    },
    AllowedTools = ["mcp__neo-preview"],
    Tools = [new ClaudeAgentTool("pointer", "Where the person points.", schema, (input, cancel) => …)],
    PermissionHandler = async (request, cancel) =>
        await AskThePersonAsync(request) ? ClaudeAgentPermission.Allow() : ClaudeAgentPermission.Deny("Nein."),
});

session.EventReceived += e => …;   // text as it is written, tool steps, permission prompts, the end of a turn
var result = await session.SendAsync("Bau mir eine Uhr und stell sie an x=1300, y=160.", images);
await session.InterruptAsync();     // stops the running turn
```

- **Events** (raised on the reading thread; a handler must return quickly):
  - `ClaudeAgentReady`: model, tools and the state of the MCP servers.
  - `ClaudeAgentTextDelta` and `ClaudeAgentText`: words as they come, and finished text blocks.
  - `ClaudeAgentToolStarted` and `ClaudeAgentToolFinished`: each tool step and how long it took.
  - `ClaudeAgentPermissionAsked`
  - `ClaudeAgentTurnEnded`
  - `ClaudeAgentExited`
- **A turn's result:** whether it succeeded, the text, duration, API time, rounds, cost of the turn and of the session, tokens (new, cache written, cache read, output), and the tools that were refused.
- **One turn at a time:** a second `SendAsync` while one runs is refused. Cancelling `SendAsync` interrupts the turn in Claude Code, waits until it has stopped, then throws.
- **Tools of the host:** they run in the calling process. Claude sees them as `mcp__<ToolServerName>__<name>` and may use them without asking. A tool can return a picture.
- **Permission prompts:** without a `PermissionHandler`, anything that would ask is refused (headless Claude Code does that by itself).

## How it talks to Claude Code

As the official SDKs do (checked against the type definitions of the TypeScript SDK 0.3.232):

- **Command line:** `claude -p --input-format stream-json --output-format stream-json --verbose`, plus the options above.
- **Messages:** user messages go in as JSON lines; events come out as JSON lines.
- **Control requests** in both directions, each answered by a `control_response` with the same `request_id`:
  - `initialize`: sent first. It names the in-process MCP server.
  - `interrupt`
  - `can_use_tool`: a permission prompt, answered by the handler (`--permission-prompt-tool stdio`).
  - `mcp_message`: the JSON-RPC messages for the host's own tools. They travel on the same pipe, with no port and no second program. `SdkMcpServer` answers `initialize`, `tools/list`, `tools/call` and `ping`.
- **Cost and API time:** Claude Code counts them for the whole session. A turn gets the difference.

## Which Claude Code

`ClaudeCli.Find()` takes the newest Claude Code it finds:

- the `claude` on the PATH: npm's launcher is followed to `bin\claude.exe`, or to `node cli.js` in older releases;
- the copies the Claude desktop app keeps (`%LOCALAPPDATA%\Claude-Data\<app>\claude-code\<version>\<build>\claude.exe`).

The version comes from npm's `package.json` or from the folder name. The one on the PATH wins a tie, and wins when its version cannot be told.

Old builds fail with the current models: 2.1.111 sends a thinking setting that the 5.5 models refuse (400). 2.1.325 and 2.1.326 work.

The [[Claude Code]] agent finds Claude Code the same way.

A session runs on an Anthropic API key (`ANTHROPIC_API_KEY`, also read from the user's variables). Products other than Anthropic's may not use a claude.ai login without Anthropic's approval, so `RequireApiKey` is on. When N.E.O. runs inside a Claude Code session, that session's variables are not passed on.

## Measured

2026-11-17: Claude Code 2.1.326, Haiku 5.5 at effort low, own short prompt, restricted, no tool search, neo-preview attached.

| | |
|---|---|
| Start until Claude Code answered initialize | 0.9 s |
| A question answered with a tool of the host | 2.1 s |
| A clock built with neo-preview and placed at x/y | 11.5 s (Haiku needed two compiles) |
| A picture in the message described | 0.9 s |
| A file written after the permission prompt | 2.6 s |
| A running turn interrupted | ended after 0.5 s |
| The whole session | 1.2 US cents |

In the spike before, the same tasks took about twice as long when run through Claude Code's own system prompt and its tool search. Sonnet 5.5 was about 1.5 times slower and cost about 15 times as much.

## Tests

`Neo.ClaudeAgent.Tests` uses a PowerShell script that plays Claude Code: stream-json and the control protocol, permission prompts, MCP messages, interrupts and a crash. They cost nothing.

## Limits

- Tested on Windows. The desktop app's copies are looked for on Windows only.
- Hooks, elicitations and user dialogs are not answered; Claude Code gets an error for them.
- What Claude does is up to the model. The host sets the tools, the permissions and the prompt.
