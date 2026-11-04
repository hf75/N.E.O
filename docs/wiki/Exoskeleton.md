# Exoskeleton — new features for programs you have no source of

"Give Notepad a live word count." Ten seconds later a slim panel hangs on the side of Notepad. It counts while you type, moves with the window, disappears when you minimise it and is back when you open Notepad tomorrow. Nothing was installed into Notepad, and nobody had its source.

It works for any program that shows itself to the Windows accessibility interface, which is most of them, and it is how an old program without a vendor gets the one button it was always missing.

Windows only. On the Avalonia host the panel docks to the other program. On the WPF host the app stays inside the N.E.O. window, where it can still read and use the other program; only an exported program docks there too.

## What you see

1. Have the program open, and name it in your prompt: "Add a button to Notepad that sorts the lines."
2. The chat says what the AI gets to see: *The AI sees the window title and the structure of Notepad (the names of its buttons and menus, not what is written in it), so that the tool fits.*
3. The app window moves to the program and loses its title bar. From now on it follows that program, not N.E.O.
4. The next prompt patches the panel in place, as with every app ([[Hot Reload]]). It stays docked.

Export it, and the panel is a program of its own ([[Export and Import]]): start it and it finds Notepad by itself, also as a living app ([[Living Apps]]).

| The other program | The panel |
|-------------------|-----------|
| moves or resizes | moves with it, with the same height |
| comes to the front | comes to the front with it |
| is minimised | hides, and comes back with it |
| ends | becomes a normal window again and waits |
| starts again | docks again |

## In generated apps

The AI knows this API and uses it on its own. It is compiled into the app as source, so an exported program needs nothing from N.E.O.

```csharp
using Neo.App;

_notepad = ForeignApp.Attach("notepad");               // its process name, or a part of its window title
_notepad.TextChanged += text => count.Text = Count(text);   // on the UI thread, also once right away
_notepad.Closed += () => count.Text = "Notepad is closed";

await _notepad.ClickAsync("Save");                     // presses a button or menu item by name; false when there is none
await _notepad.SetTextAsync(sorted);                   // replaces the main text; false when the program does not allow it
var controls = await _notepad.ReadElementsAsync();     // what is in its window
var saves = await _notepad.FindAsync("Save");          // the controls whose name contains this, in the whole window
var field = await _notepad.ReadValueAsync("Search");   // the text of a named field; null when there is none

ForeignApp.Attach("excel", size: 120, side: ForeignSide.Bottom);   // a bar below the window
ForeignApp.Attach("paint", size: 220, length: 280);    // a box at the lower end of the right side
ForeignApp.Watch("notepad");                           // read and use it without docking
```

- Lines always end with `\n`, whatever the program uses. Notepad ends them with a lone `\r`, and code that splits at `\n` would see one long line.
- The panel goes to the wanted side, to the other one when there is no room, and over the window's edge when the window fills the screen. With a `length` it is shorter than the edge and sits at its lower end: over a maximised window it lies in the corner and leaves the title bar and its buttons free.

## How it works

- **Finding the program:** `ForeignApp` goes through the windows a person can see, topmost first: exact process name, then a part of the process name, then a part of the title. A store app shows inside a frame of the system (`ApplicationFrameHost`); the program is who owns what is inside it.
- **Docking:** a background thread looks 33 times a second where the window is and moves the panel there. The panel is no child or owned window of the other program, on purpose: an owned window would end with its owner and tie the two programs' input together, so one that hangs would hang the other. When the program comes to the front, the panel is raised above everything without taking the keyboard.
- **Leaving N.E.O.:** the window that runs the app normally follows N.E.O., and shows *Generating…* while N.E.O. is minimised. `ForeignApp` marks the process as docked (`AppDomain` data, because the helper may not know N.E.O. in an exported program), and the window then ignores all of that.
- **A new version of the tool:** a change that can be patched in leaves all of this running. One that cannot replaces the tool in the same process, and each version has its own copy of `ForeignApp`, so the window that runs the app tells them through `AppDomain` data that their version is over, and they stop. A new `Attach` takes the window over as it is, with what it looked like before it first docked, so the title bar still comes back when the program ends. A version that does not dock gets a normal window back. In one version, a window docks to one program: after a later `Attach`, the earlier one only reads and uses its program.
- **Reading and using:** UI Automation through its COM interface, declared in the helper, because the managed wrapper exists only for WPF programs. The text comes from the largest document or edit field in the window. A control is looked for by name in the whole window, not only among its first ones: in a browser or an Electron app the controls that matter lie deep. Such an app may build its accessibility tree only when something asks, so the first look can come back nearly empty.
- **Other apps and Claude:** the same helper makes the program itself usable for others. Its interface has methods for what a person does in it, throws instead of returning `false`, and is repaired when the program changes: [[Program Interfaces]].
- **Claude Code itself:** the [[Claude Bridge]] is a panel of this kind for the Claude desktop app. It types the prompts that N.E.O. apps send with `Ai.Trigger` into the session, which the desktop app does not take as channels.
- **What the model sees:** when a prompt names a running program, by its process or by what its title calls it (`Bericht.txt - Editor` is named by "Editor", not by "Bericht"), N.E.O. adds the structure of its window to what the model reads, and not to the history. Names only where they name a control, and the labels of fields; a control without a name gets an address (`Quantity:>Edit`, `StatusBar:1>Text`). What a text, list item or document says goes as its shape only (`Total: 250,00` as `Xxxxx: 999,99`), and stays on your PC, as does everything the finished tool reads while it runs, unless you ask the tool to send it somewhere. A minimised program is restored first, without taking the keyboard: minimised, it shows only its title bar.

## Limits

- A program that shows little to the accessibility interface gives little to read. The window can still be docked to. Excel's cells, for one, are not its main text.
- A program running with more rights than the panel (as administrator) cannot be read or used.
- `SetTextAsync` needs a text field that allows it. Notepad's does. It replaces the text as a whole, and how the program's undo treats that is up to the program.
- A patched tool keeps what its constructor started ([[Hot Reload]]): taking the docking out of the code by a patch takes effect when the tool next starts.
- Tools for other programs read what is on your screen and run with your rights, like everything N.E.O. builds.
