# Claude Code — big tasks in the background

N.E.O. builds small tools in seconds. When a task is too big for that, such as a larger program, a whole project folder, or many steps, a tool built with N.E.O. can hand it to Claude Code, Anthropic's coding agent. Claude Code works on it in the background, and N.E.O. tells you when it is done, even when the tool that started it has been closed by then.

## Switching it on

Claude Code works with files and programs on your computer on its own, for minutes, so it is off until you switch it on:

```
setx NEO_CLAUDE_CODE 1
```

Then restart N.E.O. Any value uses the `claude` command on the PATH. The path of `claude.exe`, of the `claude.cmd` that npm installs, of Claude Code's `cli.js` or of a `.ps1` script uses that file.

Claude Code has to be installed (`npm install -g @anthropic-ai/claude-code`). It runs on your `ANTHROPIC_API_KEY`, which N.E.O. passes on, or else on Claude Code's own sign-in.

## In generated apps

The AI knows these calls and uses them for what is too big for the app itself:

```csharp
using Neo.App;

// A task, with short lines on what Claude Code does while it works
var progress = new Progress<string>(line => log.Items.Add(line));
ClaudeCodeResult built = await AIClaudeCode.RunAsync("Build a .NET console program that renames my photos by the date they were taken.", progress, cancellationToken: cancel.Token);
resultText.Text = built.Text;      // its closing words: what it did, where it is, how to start it
folderText.Text = built.Folder;    // where it worked

// A follow-up in the same conversation and folder
await AIClaudeCode.ContinueAsync(built, "Add a dry run that only lists the new names.", progress);

// The outcome as data
public record Duplicate(string Name, long Bytes, List<string> Paths);
var duplicates = await AIClaudeCode.RunAsync<List<Duplicate>>("Find duplicate files in D:\\Photos.", progress, folder: "D:\\Photos");
grid.ItemsSource = duplicates.Data;
```

- `Text` may contain Markdown. `Succeeded` is false when it stopped or was refused something it needed. Then `Text` says why.
- Cancelling the token stops Claude Code and whatever it started.
- Without a folder, it works in a new folder of its own. With one, it works there, and may change files there.
- It throws when Claude Code is switched off or not installed. Generated apps show the message.
- The model is set under **Settings → Claude Code Model**. `default` is whatever you set in Claude Code itself; the alternatives are `opus`, `sonnet` and `haiku`.

## What it may do

Nobody watches a job, so nothing can be allowed while it runs:

- It changes files in its folder without asking (Claude Code's `acceptEdits` mode).
- It may run `dotnet`, to build and test what it writes.
- Everything else is refused, and the result says so.
- It is told to work only in its folder and not to start programs that wait for a person, such as windows or servers. Starting what it built is left to you.
- A job stops when it has cost 5 US dollars (Claude Code's `--max-budget-usd`), and after 60 minutes.

## When it is done

Every job has a folder, `%LOCALAPPDATA%\Neo\ClaudeCode\Jobs\<id>`, with these files:

- the task (`prompt.txt`)
- what Claude Code reported, line by line (`events.jsonl`)
- its error output
- `work\`, where it works unless the app named a folder

Claude Code writes straight into these files. It is not tied to the app that started it, so it goes on when that app closes. N.E.O. looks into the folders every two seconds and reports in its chat when a job has ended, with Claude Code's closing words, the folder and the cost. Any other program can follow a job the same way, through `ClaudeCodeJobs` in `Neo.Agents.ClaudeCode`.

In our measurements (2026-11-10, Claude Code 2.1.111 with Haiku), Claude Code wrote, built and fixed a .NET console program in 21 seconds for 4 US cents. A question answered as data cost 3 cents.

## How it works

`Neo.Agents.ClaudeCode` starts the Claude Code CLI in print mode (`claude -p --output-format stream-json`) with these settings:

- The task arrives on its standard input.
- Its output goes into the job's files.
- A note tells it that it runs unattended.

The progress lines are read from those files, and the end from the last one. An npm install is started as `node cli.js` directly, so no command shell can misread a quote in a task.

When N.E.O. itself was started from a Claude Code session, the variables of that session are not passed on. Above all, that includes the address its API calls go to.

`ClaudeCodeAgent` is also an ordinary N.E.O. agent (`IStreamingAgent`): inputs `Prompt`, `WorkingDirectory`, `ResumeSessionId`, `JsonSchema`; it streams the progress lines and ends with the result.

## Limits

- Tested on Windows. Linux and macOS are built in, but untested.
- Each task costs money.
- The job folders stay until you delete them.
- What a job does is up to Claude Code. N.E.O. only sets the folder, the limits and the permissions.
