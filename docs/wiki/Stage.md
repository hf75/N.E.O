# N.E.O. Stage — talk to your computer

`Neo.Stage` is an experimental harness for Claude Code that you use by voice. Hold the right Ctrl key, speak, let go: the stage listens (ElevenLabs speech recognition), Claude Code answers (through [[Claude Agent]]) and speaks (ElevenLabs text to speech), and its hands act on the computer. A transparent layer over the screen shows what happens: a ring low in the middle that breathes with your voice, your words as they are heard, the answer as it is written, and every window it works on, outlined.

The microphone is open only while the key is held. A shortcut with the right Ctrl (Ctrl+C) never starts it. Pressing the key while the stage answers silences it and listens again.

> Experimental and Windows only. It started as a modern "Put That There" (speaking while pointing); pointing turned out to be more cumbersome than people expect a computer to be, so the voice comes first and pointing waits behind a switch.

## What it can do

- **Windows by name.** With every question Claude gets the open windows (title, program, how each lies). It arranges them (minimize, maximize, restore, bring to front, left or right half, all of them in a grid), highlights them while it talks about them, and looks at one (a picture of that window) only when the question needs it.
- **Opening.** Programs as the Start menu names them ("Word", "Rechner"), known folders (Downloads, Documents…), websites and files, and files found in the Windows Search index by name and content. What the stage starts comes to the front when its window appears.
- **Show me.** "How do I set the margins in Word?" lights up what to click, step by step, in the program itself: "1 · Layout" glows at once, "2 · Margins" as soon as the tab is open. When a step opens something new (a drop-down, a dialog), Claude sees what came and shows the next step, numbered on ("3 · Custom Margins…"), or says you are there. The stage never clicks: you do, and learn where it is. It finds things by the names the program gives them for UI Automation, in drop-downs and dialogs too, and stops when you speak again.
- **Everything nobody foresaw.** Claude writes a small Windows PowerShell script and says what it changes:
  - *nothing* (it only reads) and *undoable* (closing a window: the program asks itself about unsaved work) run at once;
  - *lasting* (deleting, overwriting, sending, installing) runs only after the stage has asked aloud and you held the key and said yes. The stage checks the script too and asks even when Claude did not say so;
  - running as administrator and changing security or system settings are refused, whatever is said.
  A script that worked can be kept as a skill and used again by name.
- **The web:** questions to the internet go to web search.
- **Apps by voice.** "Build me a timer…" has [[N.E.O.|Home]] build the app in a hidden N.E.O. (only the app window is seen, Ctrl+Shift+N shows N.E.O. beside it). "Make it blue" changes it while it runs. Every app the stage builds offers its settings and its state on the [[App Network]], so Claude can then simply call it: "make Earth go twice as fast" sets a value, "how many laps has Earth done?" reads one, instantly and exactly, without looking at the screen.
- **Tell me when…** "Tell me when a file called Zeugnis shows up in my downloads" sets a watcher: a small script that only looks (or a value of a running N.E.O. app), asked every few seconds, until the moment comes; then the stage says so in its own voice ("The file Zeugnis-Entwurf.txt is in your downloads now"). Watchers that would change something are refused, one that keeps failing says so and stops, "what are you watching?" lists them, "stop watching the battery" ends one. They come back after the stage restarts (`%LOCALAPPDATA%\Neo\Stage\watchers.json`).
- **Apps that speak up.** The stage is on the [[App Network]] as *N.E.O. Stage*: any N.E.O. app can say something aloud through it (`Say`) or ask the person and get the spoken answer back (`Ask`). "Build me an egg timer that tells me when it is done" gives an app that says "the eggs are done" in the stage's voice when the time is up; a quiz can ask its questions aloud and hear the answers. The stage names the app and outlines its window, waits until neither you nor another answer is speaking, and tells Claude what the app said and what you answered, with your next question.
- **Speaking while writing:** the first sentence of an answer is spoken while the rest is still being written; when something takes a while ("N.E.O. is building…"), the stage says so instead of keeping silent.

## What you need

- Windows 10 or 11 and .NET 10.
- The Claude Code CLI (npm package `@anthropic-ai/claude-code`, 2.1.325 or newer for the 5.5 models; the stage finds the newest one on the machine) and `ANTHROPIC_API_KEY`. The stage does not use a claude.ai login.
- `ELEVENLABS_API_KEY` with speech to text (realtime) and text to speech.
- A microphone and a loudspeaker.
- For building apps: a build of `Neo.App.Avalonia` copied to `%LOCALAPPDATA%\Neo\Stage\neo`, or `NEO_STAGE_NEO` naming its `Neo.App.Avalonia.exe`.

## Starting it

Build `Neo.Stage` and start `Neo.Stage.exe`. Run it from a copy of the output folder rather than from `bin`, since it stays running and would lock the next build. A tray icon appears; hold the right Ctrl key and speak. A newer stage ends one that is already running, so two never answer at once.

| Variable | What it does |
|----------|--------------|
| `NEO_STAGE_LANGUAGE` | `de` (default) or `en`: what the ears expect and the stage speaks |
| `NEO_STAGE_MODEL` | The Claude model of the brain (default `claude-haiku-5-5`, low effort) |
| `NEO_STAGE_VOICE` | An ElevenLabs voice ID (default: a stock voice) |
| `NEO_STAGE_NEO` | Where `Neo.App.Avalonia.exe` is, if not beside the stage |
| `NEO_STAGE_POINTING` | `1` turns pointing back on (Put That There) |
| `NEO_STAGE_LOG` | A file for what the stage heard, did and measured |

## What goes where

- Your voice goes to ElevenLabs while the key is held, and the answers go there to be spoken.
- What you said, the titles of the open windows, pictures of windows when Claude looks at one, and the names of what can be clicked in a program it shows you around go to Anthropic through Claude Code.
- Scripts run on your computer under your account; the ones that last only after your yes.
- What an app says through the stage goes to ElevenLabs to be spoken; your answer to an app's question goes to that app, and both to Claude with your next question.

## Checks and demos

None of these opens the microphone or plays a sound unless it says so:

| Argument | What it does |
|----------|--------------|
| `--brain-check "sentence" [--then file] [--windows word] [--for-real] [--yes]` | Asks the brain directly; its hands only rehearse unless `--for-real`, and `--windows` lets it see only some windows |
| `--live-check seconds [--sound pcm] [--reply pcm] [--brain]` | The whole stage as if the key were held, hearing recorded sentences |
| `--mouth-check`, `--open-check`, `--find-check` | The voice without a loudspeaker; what "open" would do; the file search |
| `--uia-find word --name name` | What the eyes find by that name in a window whose title holds the word, what can be clicked there, and how long a look takes |
| `--frames folder` | Pictures of the stage's states over a drawn desktop |
| `--demo script.json` | A demo nobody operates: lines spoken by a synthetic voice, the stage answering live, the screen recorded with ffmpeg and both voices kept with their times |

`Neo.App.Avalonia --stage <pipe> [--title name]` is the hidden mode in which N.E.O. builds apps for the stage.

## Limits

- The brain is one Claude Code session that stays open; follow-ups know what came before.
- Apps built before the stage restarted keep running and can still be called, but "change it" reaches only apps of the running stage.
- N.E.O. sometimes replaces an app on a change instead of patching it in place; most state carries over, not always.
- Show me needs a program that names its controls for UI Automation (Office, Windows' own programs, most WinForms, WPF and WinUI apps). A program that draws everything itself offers nothing to find.
- German and English only.
