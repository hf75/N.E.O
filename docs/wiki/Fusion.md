# Fusion — drag one program onto another

You have a Mandelbrot zoom running in grey and a small tool with a colour gradient. Drag the gradient onto the picture and let go: half a minute later there is one program, the picture never stopped zooming, and it now takes its colours from your gradient. Nobody asked for that connection: putting exactly these two together was the request. The same goes for a tool that splits the costs of a trip and one that converts currencies, with everything you entered in both still in there.

Windows only, and only between app windows of the Avalonia host and its exports. The WPF host keeps its app inside the N.E.O. window, where there is nothing to drag.

## What you see

1. Drag a program by its title bar over another one. That one turns blue and says *Drop to merge Currency into this app*.
2. Let go. The dragged window is gone at once.
3. A line over the other app says *Merging Currency into this app. You can go on working.* The AI gets the source of the dropped program.
4. The new part appears beside the old one, the window grows by the width of the dropped window (as far as the screen allows), and the data of the dropped program is in it: *Currency is a part of this app now.* Where one holds something the other can use, the two are connected.

In the chat of the receiving N.E.O. it is an ordinary step (*Merge "Currency" into this app.*). The source of the other program goes to the model and stays out of the history.

| What happens to the dropped program | |
|-------------------------------------|---|
| An exported living app ([[Living Apps]]) | It has become a part of the other one and ends. Its project file stays as it was. |
| An app in N.E.O. | It stays hidden. The work is still there, and the next prompt in that N.E.O. brings it back as it was. |

## When it does not work out

The dropped window comes back with what it held and a line that says why:

- the AI could not merge the two;
- N.E.O. was busy with one of them;
- the other program was closed in the meantime (it notices within a second);
- nothing came back for five minutes.

Its data never left: it was only copied. A living app comes back where it stood before the drag, an app in N.E.O. beside its N.E.O.

Letting go over nothing, or over a program that takes no merges, does nothing. Neither does pulling an edge across another app, which is resizing, nor passing over one on the way somewhere else, nor pressing Escape during the drag.

## How it works

- **Noticing the drag:** Windows moves a window in a loop of its own and tells the window when it starts and ends. The command that starts it says whether this is moving or resizing (`WM_SYSCOMMAND`, in Avalonia through `Win32Properties.AddWndProcHookCallback`). While that lasts, a timer looks for the first window a person can see below this one under the pointer. Another window in between is no target, not even N.E.O.'s own: what you cannot see, you cannot drop on.
- **Talking:** the two windows and the two N.E.O.s use the pipes of the [[App Network]], past the apps themselves, with messages of their own. An app that does not answer the first one takes no merges.
- **The code:** the receiving N.E.O. runs a normal prompt, with the source and the instructions added to what the model reads. The change is patched in like every other ([[Hot Reload]]). When that is not possible the app is replaced, and what it held is put back.
- **The data:** the state image of the dropped program (all its fields that are plain data) moves over by name. The model is told to keep every field under its exact name and, only where the receiving program has one of that name, to put a prefix in front (`currency_`). A value looks for the prefixed name first. A field the receiving program had before stays its own, whatever comes in under its name: such a value stays behind, and the chat says so.
- **The layout:** two columns in the proportion of the two windows, so each part keeps the room it had. The window then takes in the room of the dropped one.
- **The connection:** the model is told that the user put exactly these two together for a reason: where one holds or produces something the other can use, it lets the other use it, live. It says in its explanation what it connected. Merging a gradient tool into a fractal zoom connected the two in 5 of 5 runs (Sonnet 5.5, effort low).

## Limits

- What moves is what lies in fields as data: text, numbers, lists, your own classes made of those. Open files and running timers are made anew by the program's own code.
- What a person typed into a field without pressing anything stays behind when it lives only in the control. A list of entries does not.
- How well two programs fit together is up to the model, and two that have nothing to give each other just stand side by side. Ask for more in the next prompt.
- Beside N.E.O. the window can only grow to the edge of the screen, because it stays docked. A living app moves left when it has to.
- Both N.E.O.s use their own settings: the one you drop on needs a model and a key. For an exported living app that is the key of the person using it.
- This has run on a single display at 100 %. Scaled displays are covered by the arithmetic only.
