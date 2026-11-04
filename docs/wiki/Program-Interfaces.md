# Program Interfaces — every program gets an interface

"Give Stockroom 2003 an interface." A slim panel docks to the old program and lists what it can now do for others: `Stock(item)`, `BookIn(item, quantity)`, `BookOut(item, quantity)`. From then on another N.E.O. app, Claude Code or a link uses the stockroom like a service, and the stockroom does it on screen, the way a person would. Nobody had its source, and it has no API.

The interface is code: written once by the AI from the structure of the program's window, compiled, and called without any AI in the loop. Before a call changes something in the program, the panel asks you. And when the program gets an update that renames a button or moves a field, the next call notices, waits while N.E.O. repairs the interface with the program's new look, and goes on.

Windows only, like the [[Exoskeleton]] it is built on.

## What you see

1. Have the program open and ask for its interface: "Give Stockroom 2003 an interface", "Make the calculator usable by other apps".
2. The chat says what the AI gets to see: *the window title and the structure of Stockroom2003 (the names of its buttons and menus and the labels of its fields, not what is written in it)*.
3. The panel docks to the program, and the chat says *On the app network as 'Stockroom': 3 method(s)*.
4. Another app calls a method: it happens in the program, and the panel notes the call.
5. A method that changes something waits for you. The panel asks *Claude Code wants Stockroom to do: BookOut("4711", 3)*: **Allow** lets this call run, **Always for Claude Code** every call of that caller while the panel is open, **No** refuses it. No answer within a minute is a no.
6. The program changes under the interface: *Stockroom2003 has changed: repairing the interface, the call waits…*, a few seconds later *Repaired: the interface fits Stockroom2003 again, and the call goes on.* The caller gets its answer, as if nothing had happened.

## Who calls it

| From | How |
|------|-----|
| Another N.E.O. app | `Apps.CallAsync` and `Apps.ReadAsync` ([[App Network]]). The AI finds the interface in the list of running apps it is given. |
| Claude Code | `call_running_app` and `read_running_app` of the [[MCP Server]]. With `inspect_foreign_program`, Claude reads a program's structure and can write an interface itself. |
| A link | Drag an app onto the program's window: the halves *Drop to merge* and *Drop to link* show over the program. Or drag the program's own window onto an app: it can only be linked, its code is not here ([[App Link]]). |

## In generated apps

The AI writes the interface as an app like any other, with three things more: the name of the program in `[AppInfo]`, one method per thing a person does in it, and `Changes = true` on every method that changes something.

```csharp
using Neo.App;

[AppInfo("Stockroom", "The stockroom: stock of an item, booking items in and out.", Program = "Stockroom2003")]
public class DynamicUserControl : UserControl
{
    private readonly ForeignApp _stockroom = ForeignApp.Attach("Stockroom2003", size: 260);

    [McpCallable("The stock of an item.")]
    public async Task<int> Stock(string item)
    {
        await _stockroom.SetAsync("Item no.:", item);
        await _stockroom.PressAsync("Look up");
        return int.Parse(await _stockroom.GetAsync("In stock:>Text"));
    }

    [McpCallable("Books a quantity of an item out.", Changes = true)]
    public async Task<string> BookOut(string item, int quantity)
    {
        await _stockroom.SetAsync("Item no.:", item);
        await _stockroom.SetAsync("Quantity:", quantity.ToString());
        await _stockroom.PressAsync("Book out");
        if (await _stockroom.DialogAsync(seconds: 1) is { } dialog)
        {
            await _stockroom.PressAsync(dialog.Buttons[0]);
            throw new InvalidOperationException(dialog.Text);
        }
        return await _stockroom.GetAsync("StatusBar:1>Text");
    }
}
```

These calls of `ForeignApp` throw instead of returning `false`, so that a call never pretends to have worked:

| Call | Does |
|------|------|
| `SetAsync(control, value)` | sets the text of a field, or picks the item of a combo box |
| `PressAsync(control)` | presses a button, a menu item, a link, a tab or a list item |
| `GetAsync(control)` | what a control shows: a field's text, a label, the selected item, `on` or `off` |
| `CheckAsync(control, on)` | ticks or clears a check box |
| `SelectAsync(control, item)` | selects an item of a list, a combo box, a tab or a tree |
| `RowsAsync(control)` | the rows of a list or a table, each as the texts of its cells |
| `WaitForAsync(control, seconds)` | waits until a control is there and can be used |
| `DialogAsync(seconds)` | a dialog of the program, open now or within the seconds: title, text, buttons |

A control that is not there throws `ForeignControlMissingException`: that is what starts a repair. A program that is not open throws `ForeignProgramClosedException`, a control that does not allow the call an `InvalidOperationException`.

A control goes by its name, by its id, or by an address from the structure the AI saw:

| Address | Is |
|---------|----|
| `Quantity:>Edit` | the first field after the label *Quantity:* |
| `StatusBar:1>Text` | the first text in the first status bar |
| `Booking>Pane` | the first pane after the control called *Booking* |
| `Edit:2` | the second field of the window |

An address does not move when a list gets more rows, and the title bar of the window does not count. A label or a button that changes its name, or a field that moves to another place, is what an update looks like, and the interface is repaired.

## How it works

- **What the model sees** (`Neo.Shared.ForeignWindows`): one line per control, indented. The names of buttons, menus, tabs and column headers; the labels of fields (a text that ends with a colon and has no digit); a field's name only when it has no digit in it. What a text, a row or a document says goes as its shape only, `Total: 250,00` as `Xxxxx: 999,99`, so that the model knows how a number is written and not what it is. A password field shows nothing. Three rows of a list, then how many more. A minimised program is restored first, without taking the keyboard: minimised, it shows nothing but its title bar.
- **The calls**: UI Automation, as for the exoskeleton, with one exception. A button that has a window of its own (Windows Forms, classic Windows dialogs) is pressed the way Windows tells its parent window that it was clicked. Pressed through UI Automation, a button that opens a message box holds the whole program: it answers no other UI Automation call until the box is closed, so nobody could read or close it. A press that opens a dialog does not wait for it. After a press the program gets a moment, and is asked whether it is done (a message it answers once it is free). A control that is not found is looked for once more before it counts as missing, so that a program between two states does not start a repair.
- **Asking first**: a method with `Changes = true` runs for another app only when the window that runs the interface says yes (`AppNetwork.ChangeGate`). The Avalonia window asks over the app, the WPF host with a message box. A window that cannot ask refuses. `invoke_method` of the MCP server asks the same way. Who calls is what the caller says it is (an app's name, *Claude Code*): it is shown to you, and proves nothing.
- **Repair while the call waits**: a call that runs into `ForeignControlMissingException` goes to `AppNetwork.RepairHandler`. The window that runs the interface reports a recoverable error to its N.E.O., with how the program looks now: that part goes to the model and not into the chat. [[Hot Reload]] patches the running interface in place (heal in place), the window hears that the repair is in, and the call is made once more, now with the new code. The caller is told how long to wait (`RepairSeconds`, 150 s on top of the method's own time), and calls that run into the change meanwhile wait for the same repair.
- **Only where a model is**: a desktop host repairs with its own model. In a preview of the MCP server the caller gets the error, and `call_running_app` adds how the program looks now, so that Claude patches the interface itself.
- **In a preview**: the MCP server compiles the helper into every app that uses `ForeignApp`, as source, as the desktop hosts do. The list of open programs that `inspect_foreign_program` gives says which programs are open, by the program part of their titles: `Bericht.txt - Editor` goes out as `Editor`, and so does a title that separates with a dash of another kind, as Edge and the Explorer do.

## Measured

With the stand-in stockroom (`samples/foreign-programs/Stockroom2003`), the Avalonia host, Sonnet 5.5 at effort low, the prompt "Give Stockroom 2003 an interface.", and a driver that calls the interface the way another app does, answers the question in the panel, starts the next version of the stockroom and calls again:

- 11 runs. In each the interface was there after 12.5 to 16.9 s, with a method to look an item up and one each to book in and out, both marked as changing something.
- Looking up an item: 0.3 to 2.4 s. Booking out, the question in the panel answered: 2 to 5 s.
- After the next version of the stockroom (the quantity field and two buttons called differently), the call that found the change waited while the interface was repaired, patched into the running interface each time, and came back booked after 8.2 to 12.6 s, the repair included. The model mapped the new names itself: *Quantity:* to *Amount:*, *Book out* to *Issue*, *Book in* to *Receive*.
- Booking out more than there is (5 runs): the stockroom answers with a message box, the interface read it, closed it, and gave its text back, nothing booked, no dialog left open. In 3 of the 5 it was thrown as an error as the prompt asks, in 2 it was returned as the answer.
- One run of the 11 failed, on the driver: it handed a quantity the interface took as text, the stockroom showed a message box, and the interface could not reach it. That is how the message box problem above was found; since the fix the 5 runs above all passed.

Through Claude Code, against the real server over stdio: the structure of the stockroom, the interface as a preview, the question answered in the preview window, a booking, the error after the next version with how the program looks now, a patch, and the booking again.
- **A drop on the program's window**: the dragged app looks at the window under the pointer. A window of no N.E.O. app belongs to the interface whose `Program` it matches (process name or title, as ForeignApp finds it). That interface shows the two halves in a window of its own over the program, which takes no input. When this app is the interface, a drag of the program's window is noticed through a system hook (out of context: nothing is loaded into the other program), and links.

## Limits

- What the exoskeleton cannot reach, no interface can: a program that shows little to the accessibility interface, or one that runs with more rights than N.E.O. (as administrator).
- A long list may only have the rows that are visible.
- The interface works through the program's window, so the program must be open. It may be behind other windows; a minimised one is restored.
- How good the methods are is up to the model, and up to what the program shows: a field without a label or a name is found by its place, and a moved field then starts a repair.
- A repair needs a model and a key in the N.E.O. behind the interface, and takes as long as a prompt.
- The question names the caller as it names itself. Everything the interface does runs with your rights, like everything N.E.O. builds.

## Code map

| Piece | Where |
|-------|-------|
| The calls, addresses, exceptions | `Neo.App.Core/ForeignApp.cs` (compiled into every app as source) |
| What the model sees | `Neo.Shared/ForeignWindows.cs`; `Neo.App.Core/ForeignPrograms.cs` (desktop prompts) |
| Prompt | `Neo.AssemblyForge.Core/ForeignAppGuidance.cs` (`Interfaces`); `ForeignProgramTools.Guidance` in the MCP server |
| `Program`, `Changes`, asking, repair | `Neo.App.Api/AppInfoAttribute.cs`, `McpAttributes.cs`, `AppNetwork/AppNetwork.cs`, `Apps.cs` |
| The question, the repair in the window | `MainWindow.AskToAllowAsync`, `WireInterfaces` and `RepairInterfaceAsync` in `Neo.PluginWindowAvalonia.Core`; `AskToAllowAsync` in `Neo.PluginWindowWPF.Core/App.cs` |
| Drops on the program's window | `Neo.PluginWindowAvalonia.Core/FusionDrag.cs`, `ProgramOverlay.cs` |
| Claude Code | `inspect_foreign_program`, `call_running_app`, `read_running_app` in `Neo.McpServer/Tools/ForeignProgramTools.cs` |
| A program to try it on | `samples/foreign-programs/Stockroom2003` (start it with `--version 2` for the next version) |
| Tests | `Neo.App.Api.Tests/ProgramInterfaceTests` (asking, refusing, the repair and the second go, over the real pipes), `Neo.App.Core.Tests/ForeignAppTests` (the helper compiles alone and as an export, addresses, what the model sees), `Neo.PluginWindow.SmokeTests/ProgramInterfaceSmokeTests` (real host, stand-in model, a stockroom window: the question in the panel, then an update repaired while the call waits) |
