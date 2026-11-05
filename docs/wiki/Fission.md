# Fission — tear a part out of a running program

Hold Alt and point at the chart of your expense list: a purple frame shows the part. Drag it out of the window with Alt still held and let go. A moment later the chart is gone from the list, and a window of its own stands where you let go: a program that is just the chart. Add an expense in the list, and the chart in the other window follows.

[[Fusion]] makes one program of two. Fission makes two of one: the program you tore the part out of keeps running without it and hands it its data over the [[App Network]], and the part becomes a program of its own, a [[Living Apps|living app]] that you can change with Ctrl+Shift+N, export, or close.

Windows only, and only in app windows of the Avalonia host and its exports, like Fusion. Out of another program, too, when that program has an interface: see below.

## What you see

1. Hold Alt over the app. The biggest part under the pointer stands out in purple: *Drag it out with Alt: a program of its own*. A part is a piece of the layout that lies beside others, a column, a chart, a list, a group of fields, not a single letter. Turn the mouse wheel towards you for a smaller part under the pointer, away from you for a bigger one again.
2. Press, drag it out of the window, let go. A picture of the part goes along with the pointer. Let go inside the window, and nothing happens.
3. *Tearing it out: it becomes a program of its own…* The AI takes the part out of this program and lets the program offer on the app network what the part showed. That change is patched into the running app ([[Hot Reload]]): its data stays.
4. *Writing "Expense chart" as a program of its own…* The AI writes the new program in a session of its own, from the part and the source of the program it came from.
5. The new program opens where you let go, at least as big as the part was and as big as what it shows. It gets its values from the program it came from while that runs, and says so when it does not.

The chat names the file the new program is saved in: beside the project of a living app, else in `%LOCALAPPDATA%\Neo\Fission`.

## Out of another program

The same works with a program nobody here has the source of, once it has an interface ([[Program Interfaces]]): hold Alt over its window, and the part under the pointer stands out, its booking fields, a form, a list: *Drag it out with Alt: a small program that uses Stockroom 2003*. Drag it out of the program's window and let go.

- The program keeps its part, it is not changed at all. The new program is a small window of its own that does what the part does, through the interface: its "Book out" calls the interface, and the interface books in the program the way a person does. The big program can stay in the background, or minimised.
- First the interface's N.E.O. checks that the interface offers everything the part does, and adds what it lacks (often nothing). Then the small program is written in a session of its own and opens where you let go.
- A change still waits for your yes: the first booking from the small program asks in the interface's panel, *Always for Booking* answers it for good.
- What the model sees of the part: its controls with their addresses (what they show only as its shape, as for an interface), and the picture that goes along with the pointer while you drag, as it is. What the part shows, its symbols and colors and whatever text is in it, goes to the model with that picture: you chose the part and see that picture while you drag it. The model builds the small program to look like it, every button showing what it does. Only Claude reads pictures: with another provider the model gets the list alone, and is told to make every button show what it does from its name (Word's font color button is called "Schriftfarbe Rot": a red A).

The frame is a window of N.E.O.'s own that lies over the part while Alt is held, so the press that takes the part lands there, not in the program: the program never sees it. It shows only while the pointer is over the program's window with Alt held. The Alt itself is N.E.O.'s too once the frame shows: letting it go opens neither the program's menu nor the key tips of its ribbon (a key of no meaning goes in between, the usual way to keep a lone Alt from doing that). What the frame says may reach beyond a narrow part; the box stays on the part.

## How it works

- **The gesture** (`TearGesture`): a handler in front of the app's own ones, so that the press that takes a part is no click on it. The parts are the controls under the pointer that lie in a panel beside others, are at least 40 × 24 pixels and cover at most three quarters of the app; the biggest comes first, the wheel goes through the others. They are found by where they lie, not by the hit test: a column without a background is hit nowhere in its empty space, and that is where a person points at it. Its picture is drawn into a bitmap for the drag.
- **What the model sees**: the part's controls by their addresses in the app, with what they show, a text that is not in the source only as its shape, as for an [[App Link]]; the source of the app. Nothing the person entered goes out as it is. Out of another program, which has no source to show how it looks, the picture that went along with the pointer goes out as well, as it is (see above).
- **Two steps**: first this program gives the part away (an ordinary prompt with the part described after it, so the change is in its history and can be undone), then the new program is written in a session of its own, with the list of running apps in its prompt: by then the first program offers what the part needs, under the names the new one is to use.
- **A program of its own**: the new project is started with `--play`, the way a living app runs: its window is the program, its N.E.O. hides behind it. Its first session starts with the packages of the program it came from, so a chart torn out of it has its chart library. It is started with `--place x,y,width,height` as well: its window opens where the part was let go, at least as big as the part was, and grows once to what the program in it shows, within the screen (moved there from outside after it showed, the window went back to its first size as soon as the program in it laid itself out).

## Measured

With the real model (Claude Sonnet 5.5, effort low), the real mouse and Alt, on 2026-11-04:

- **Out of an app**: an expense tracker with bars per category, three expenses entered, the bars column torn out. The first three runs found three faults: the frame did not come over a column without a background, the data went with a field the change dropped, and Roslyn's delta writer threw (see [[Hot Reload]]). In the seven runs after those fixes the new program's window stood where it was let go 28 to 48 seconds after letting go (N.E.O. said "is a program of its own now" after 22 to 43 seconds); the tracker kept its expenses every time, and a new expense showed in the new program within a second. In one of the seven the new program showed the totals without their categories: it had written its own type for them, with other names. Since then the app network names the properties of such a type, and the run after that showed them right.
- **Out of another program**: the stand-in Stockroom 2004, its interface written by N.E.O. in 13 to 16 seconds, the "Booking" group torn out. The frame stood over the group a third of a second after Alt; in five runs out of five the small program was there 22 to 33 seconds after letting go, as a copy of the group: item, look up, name, stock, amount, receive, issue. In the two runs whose driver booked with it, its "Issue" made the interface ask "Always for Booking" within half a second, and after the yes the stockroom had booked out within a second.
- **Out of Word** (Microsoft Word with a German interface, 2026-11-05): "Give Word an interface." gave an interface in 14 to 17 seconds in seven runs; Alt over the ribbon framed all of it, two turns of the wheel the font group ("Schriftart"). The small program's window stood where it was let go 34 to 51 seconds after letting go, its buttons about 4 seconds later, built by its own N.E.O.; first the interface took on what the group does, patched while it ran. The model labels the buttons its own way each time (F, Fett, A▲, a crayon). In three runs the driver found the buttons for color and highlight, in two of them the one for size too; each press changed the selected text in Word, the first one after a yes in the interface. The first runs found what is described above: Alt reached Word and opened its key tips, the new window went back to 800 × 600, the frame's label was cut off. Those small programs also showed a plain "A" and "ab" for font color and highlight, so a press that turned the text red looked like a mistake; since the model is told to make every button show what it does, six runs out of six drew them in their colors (a red A, a yellow ab or a bar under each), with the interface in 12 to 16 seconds and the small program usable 39 to 59 seconds after letting go. With the picture of the part the small program followed the font group row by row, the bars under "ab" and "A" included, and its buttons colored, highlighted and enlarged as before.
- **Out of Paint, with and without the picture** (2026-11-05): the tools group, six symbols with names ("Füllen", "Radierer"), came out recognizable either way. Paint's palette is twenty color fields whose names reach the model only as shapes: from the list alone the small program had two round buttons and one black field; with the picture three runs out of three had all twenty colors in their order, the two current colors and the color wheel. Where no symbol of the model's looks like Paint's, it sometimes still takes the nearest one (a syringe for the color picker).

## Limits

- A part that is tightly woven into the rest (its code reaches into everything) comes out as well as the model can untangle it. Read what the chat says.
- The new program gets its data over the app network, so both run on the same PC. Closed, the program it came from cannot give it anything; the new one says so and goes on when it is back.
- Two prompts: it takes as long as two changes, about half a minute.
- Out of another program only where that program has an interface, and only from the interface's own N.E.O.: the frame comes over the program the interface belongs to.
- The new program is saved as a project; nothing else of it leaves the PC.

## Code map

| Piece | Where |
|-------|-------|
| The gesture, the frame, the picture | `Neo.PluginWindowAvalonia.Core/TearGesture.cs`, `MainWindow.ShowTearTarget` |
| The gesture over another program, its frame and picture | `Neo.PluginWindowAvalonia.Core/ProgramTear.cs` (in the interface's window) |
| The part for the model | `LinkControls.DescribePart`, `LinkControls.AddressOf`; for another program `Neo.Shared/ForeignWindows.PartsAt`, `DescribePart` |
| The two steps, the new program | `Neo.App.Core/AppController.Fission.cs` (`TearOutOfProgramAsync` for another program) |
| Prompt | `Neo.AssemblyForge.Core/FissionGuidance.cs` |
| Tests | `Neo.AssemblyForge.Tests/FissionGuidanceTests` (names, what the model is told), `Neo.PluginWindow.SmokeTests/FissionSmokeTests` (real host, stand-in model, the real mouse with Alt: a column without a background stands out over its empty space, the wheel goes to the box in it, the box leaves the app, which keeps what it held, the program of its own opens where it was let go and follows the first one's data), `ProgramFissionSmokeTests` (a stockroom window with a menu and its interface: the booking group torn out with Alt, the program stays in front and its menu stays shut after the Alt, the small program books through the interface after the person's yes), `Neo.App.Core.Tests/PlayerModeTests` (`--place`) |
