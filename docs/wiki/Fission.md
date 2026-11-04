# Fission — tear a part out of a running program

Hold Alt and point at the chart of your expense list: a purple frame shows the part. Drag it out of the window with Alt still held and let go. A moment later the chart is gone from the list, and a window of its own stands where you let go: a program that is just the chart. Add an expense in the list, and the chart in the other window follows.

[[Fusion]] makes one program of two. Fission makes two of one: the program you tore the part out of keeps running without it and hands it its data over the [[App Network]], and the part becomes a program of its own, a [[Living Apps|living app]] that you can change with Ctrl+Shift+N, export, or close.

Windows only, and only in app windows of the Avalonia host and its exports, like Fusion.

## What you see

1. Hold Alt over the app. The part under the pointer stands out in purple: *Drag it out with Alt: a program of its own*. A part is a piece of the layout that lies beside others, a chart, a list, a group of fields, not a single letter.
2. Press, drag it out of the window, let go. A picture of the part goes along with the pointer. Let go inside the window, and nothing happens.
3. *Tearing it out: it becomes a program of its own…* The AI takes the part out of this program and lets the program offer on the app network what the part showed. That change is patched into the running app ([[Hot Reload]]): its data stays.
4. *Writing "Expense chart" as a program of its own…* The AI writes the new program in a session of its own, from the part and the source of the program it came from.
5. The new program opens where you let go, about as big as the part was. It gets its values from the program it came from while that runs, and says so when it does not.

The chat names the file the new program is saved in: beside the project of a living app, else in `%LOCALAPPDATA%\Neo\Fission`.

## How it works

- **The gesture** (`TearGesture`): a handler in front of the app's own ones, so that the press that takes a part is no click on it. The part is the nearest control under the pointer that lies in a panel beside others and is at least 40 × 24 pixels. Its picture is drawn into a bitmap for the drag.
- **What the model sees**: the part's controls by their addresses in the app, with what they show, a text that is not in the source only as its shape, as for an [[App Link]]; the source of the app. Nothing the person entered goes out as it is.
- **Two steps**: first this program gives the part away (an ordinary prompt with the part described after it, so the change is in its history and can be undone), then the new program is written in a session of its own, with the list of running apps in its prompt: by then the first program offers what the part needs, under the names the new one is to use.
- **A program of its own**: the new project is started with `--play`, the way a living app runs: its window is the program, its N.E.O. hides behind it. Its first session starts with the packages of the program it came from, so a chart torn out of it has its chart library. Once its window shows, the N.E.O. it came from moves it to where the part was let go, about as big as the part was.

## Limits

- A part that is tightly woven into the rest (its code reaches into everything) comes out as well as the model can untangle it. Read what the chat says.
- The new program gets its data over the app network, so both run on the same PC. Closed, the program it came from cannot give it anything; the new one says so and goes on when it is back.
- Two prompts: it takes as long as two changes.
- The new program is saved as a project; nothing else of it leaves the PC.

## Code map

| Piece | Where |
|-------|-------|
| The gesture, the frame, the picture | `Neo.PluginWindowAvalonia.Core/TearGesture.cs`, `MainWindow.ShowTearTarget` |
| The part for the model | `LinkControls.DescribePart`, `LinkControls.AddressOf` |
| The two steps, the new program | `Neo.App.Core/AppController.Fission.cs` |
| Prompt | `Neo.AssemblyForge.Core/FissionGuidance.cs` |
| Tests | `Neo.AssemblyForge.Tests/FissionGuidanceTests` (names, what the model is told), `Neo.PluginWindow.SmokeTests/FissionSmokeTests` (real host, stand-in model, the real mouse with Alt: the part leaves the app, the program of its own opens where it was let go and follows the first one's data) |
