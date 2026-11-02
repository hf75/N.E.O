# App Link — connect two running programs without changing them

Two programs run side by side, neither written for the other. Drag one onto the right half of the other and let go. The AI reads both as they are running and suggests what they could do together: *the total of the expense list goes into the amount of the converter*, *pressing Add also stores it there*. You tick what you want, and from then on the two work together. Both windows stay, no line of either program changes, nothing is saved. Close one of them, and the link is gone.

[[Fusion]] makes one program of two and keeps it. A link is for the two you want to keep as they are, and for what you only need today.

Windows only, and only between app windows of the Avalonia host and its exports, like Fusion.

## What you see

1. Drag a program by its title bar over another one. That one shows two halves: on the left *Drop to merge*, on the right *Drop to link*. The half under the pointer stands out.
2. Let go on the right half. The dragged window goes back to where it was, and a window opens: *Link Meter with Board*.
3. The AI reads both programs, which takes a few seconds, and says what it suggests, each with a line of what will happen. Untick what you do not want. Below is a field for what you want in your own words; *Ask again* sends it. *The code that would run* shows what you are agreeing to.
4. *Connect*. Each program now shows a small chip in its corner: *Linked to Board ✕*. What was connected happens from now on, and a value that should be there is there at once.
5. The cross on either chip ends the link, on both sides. So does closing either program.

If the two have nothing to give each other, or the good idea would need a program to change, the window says so. A grey picture cannot show colours it has no code for: that is what merging is for.

## What a link can do

What a person could do by hand, and what the program can already do:

| | |
|---|---|
| Watch a control | Any property of it, like the value of a slider or the text of a box |
| Watch a button | It was pressed, by the user or by the program |
| Watch a field | A field or property of the program itself, looked at 20 times a second |
| Set a control | The program reacts as it does when a person changes it |
| Press a button | A disabled button stays unpressed: a person could not press it either |
| Set a field, call a method | Where no control does it, the source shows what to call. Private ones too |

Between these sits a little C#: *parse the number out of "Total: 250 €"*, *multiply by the rate*. Anything else, files or the network, it is told to leave alone; it is shown to you before it runs.

## How it works

- **Where the drop goes:** the dragged window looks at the pointer. On the left half of the other window a drop merges, on the right half it links.
- **What the model reads:** the source of both programs, their controls with their addresses (`Slider:2`, or the name if a control has one) and what stands beside them, and the names of their fields. What you entered or loaded stays on your PC: a text that is not in the source goes as its shape only, `Total: 250,00` as `Xxxxx: 999,99`, so that the model can see how a number is written and not what it is. Fields go without their values. It answers with blocks: a title and a line in your words, and the code.
- **The model:** one of its own for this, set up like the one you chose. The conversation is not in the history, so the N.E.O. of the program can go on working on it meanwhile.
- **The communicator:** one method per block, compiled against a small interface (`ILinkSide`) and nothing of the toolkit, so it cannot touch the UI itself. What does not compile goes back to the model once; what still does not stays out, and the window says so. The bytes go to the window of the program that was dropped on, into a load context of their own, and the compiled file is deleted. Only the blocks you ticked run.
- **Between the windows:** the app network ([[App Network]]), past the N.E.O.s. The window of the other program watches and does what the communicator asks of it.
- **Order and speed:** what is asked for happens in that order, so a value set and a button pressed after it arrive in that order. A slider moved faster than the other side takes it sends its latest value; a press is never left out.
- **No going round in circles:** a change made by the link does not go back through that same link for the same control. Two sliders that mirror each other stop by themselves.
- **When a program changes:** the app may build its UI anew: the link finds the control again by its address, and reports a value only if it differs. If a program is replaced it gets a new address on the app network, so the link goes by its process.

Measured on small tools the model built itself, each knowing nothing of the others (Sonnet 5.5, effort low), 22 runs over five pairs: the answer took 3 to 16 s. A colour mixer drove the background of a poster, the total of an expense list filled the amount of a currency converter, the laps of a stopwatch became notes, and a wish in plain words added a second kind of note. One run failed, on a list of the toolkit that did not cross between the programs; that is fixed. For a palette and a fractal drawn in grey the model said that a link cannot do it, and suggested nothing.

## Limits

- A link can only use what is there. It cannot show what a program has no code for: then merge them, or ask for the change in a prompt.
- Addresses count controls of a type in the order they appear. A control that moves because a prompt changed the program may then be another one. What cannot be found any more is said in the chat.
- How good the suggestions are is up to the model. Read what it says before you connect.
- Nothing asks again: the code you agreed to runs with your rights, like everything N.E.O. builds.
- Each link lives in memory. After a restart of either program it is gone, and you make it again with a drop.
- The N.E.O. of the program you drop on needs a model and a key. For an exported living app that is the key of the person using it.
- This has run on a single display at 100 %. Scaled displays are covered by the arithmetic only.
