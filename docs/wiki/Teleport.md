# Teleport — the running app jumps to another PC

Anna has an app open with an afternoon of entries in it. Ben should take over. She clicks **Teleport** and picks Ben. He says yes, and her window is gone: the app is on his screen, with everything she entered, the text she was halfway through typing, the tab she was on. He goes on from there, and when he is done he sends it back the same way.

It works within a [[Shared Sessions|shared session]], on both hosts, between any two people in it.

## What you see

1. In a shared session, open **Share**. Below the status there is a **Teleport the running app** line: pick the person, click **Teleport**.
2. The other person is asked: *Anna wants to send you the running app, with everything in it. What is in your copy of the app now is replaced.* Nothing but the question leaves your PC until they say yes.
3. They take it. The state of your app is taken at that moment, so whatever you did while they were thinking goes along. Your window goes away, and their app is the one you just left, with a green line: *The app came from Anna, as it was.*
4. Your N.E.O. says where the app is: *The app is at Ben's now.* You can go on building it from here: your prompts reach Ben's running app like every change in the session.

To get it back, Ben sends it to you. That takes no question, because it was yours. Your window comes back with what Ben did to it.

Your own copy waits hidden in the meantime, as it was when it left. **Show my copy** in the same dialog brings that one back, if you do not want to wait. It also comes back by itself when the other person leaves or the session ends. Nothing is ever lost on the way: if the app does not arrive, or the other side cannot take it, your window comes back with a line that says why.

## What goes along, what stays

| Goes along | Stays |
|---|---|
| Everything the app holds in fields as data: text, numbers, lists, your own classes made of those | Open files, running timers, connections: the program's own code makes them anew |
| What is in the controls: typed text, selections, the open tab, scroll positions (as far as the app's UI rebuilds to the same shape) | Files on the PC and anything else outside the app. A path in a field goes along; the file does not |

## Privacy and trust

- Without Teleport, a shared session sends code and the conversation, and each person's data stays on their PC. Teleport is the one exception, by a click of the sender and a yes of the receiver, for this one time. See [[Shared Sessions]].
- The data goes straight from PC to PC, encrypted (WebRTC with DTLS). Between two guests it passes through the host's N.E.O., which only forwards it.
- What arrives is data, not code: it is read into the types the app already has, and only into fields that hold plain data.
- What the others in the session change has always run as code on your PC; that has not changed.

## How it works

- **The messages:** offer, answer, state, arrived (`TeleportMessage` in `Neo.Collab/SessionMessages.cs`), addressed to one person and passed on by the host in the star of the session. A question to a person does not hold up the session: other changes keep arriving while they think.
- **The state:** the same image [[Fusion]] uses (`Neo.Shared/AppStateImage.cs`): the fields that are data as JSON, plus the state of the controls. Large images go in pieces through the session's framing, compressed.
- **Same code:** both copies must be the same version of the app, so a hash of the sender's code goes along. If a change is on its way, the receiver says so and nothing moves; try again in a moment.
- **The window that stays away:** a host brings a hidden app back whenever it comes to the front or finishes a change. A window sent away stays away until the host says otherwise, which only teleporting does.

## Measured

With a small tool the model wrote: the app was at the other N.E.O. 0.6 s after the yes, with its entries and the text halfway typed, and home again 1.2 s after the click. That is two N.E.O.s on one PC.

## Limits

- It needs a shared session, so the [limits of those](Shared-Sessions.md#limits-and-good-to-know) apply: no connection through networks that block UDP.
- Tested with two N.E.O.s on one PC over real WebRTC. Between two PCs over the internet it uses the same connection as the rest of the session, which has not been tested on a real internet connection yet.
- An app that cannot rebuild its UI from a known method (`BuildUi`, `RebuildUi` and the like) keeps the data, but may show the old state until something makes it draw again.
- Under the WPF host the app sits inside the N.E.O. window, so that place stays empty while the app is away. A living app on the Avalonia host keeps its N.E.O. on the screen then; if you close that too, your copy comes back, or there would be nothing left to click on.
- Up to 32 MB of data go along; tested over WebRTC with 3 MB. What does not fit stays behind, and the chat says so.
