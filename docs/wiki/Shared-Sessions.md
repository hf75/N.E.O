# Shared Sessions — build one app together, over the internet, without a server

Two or more people work on the same app, each in their own N.E.O., wherever they are. One sends a prompt, and a few seconds later the change is in everybody's running app — hot-reloaded, so what each person has typed or loaded into their copy of the app stays there. No account, no server: the two PCs connect directly, set up with an **invitation code** and a **reply code** that people pass on however they like (chat, e-mail, phone).

## How to use it

**The host** (the person whose session it is):

1. Click **Share** in the toolbar (the share icon next to Export).
2. **Create invitation** — the code is copied to the clipboard. Send it to the person.
3. They send a reply code back. Paste it, click **Connect**.
4. For every further person: a new invitation (each code works once).

**A guest:**

1. Click **Share**, paste the invitation, click **Create reply** — the reply is copied. Send it back.
2. As soon as the host has pasted it, you are in: your N.E.O. gets the host's app, its packages and its conversation with the AI. *Your previous app is replaced.*

The share button turns green while others are connected; its tooltip says who. **Leave** (guest) or **End session** (host) in the same dialog.

Exchange the two codes within a few minutes of each other (while you are on the phone is ideal) — both sides keep the connection attempt open for five minutes.

## What is shared, what is not

| Shared | Stays on each PC |
|---|---|
| The app's code, NuGet packages, the AI conversation history (the next prompt, from whoever, has the full context) | The running app and its data — each person's copy keeps what they entered |
| The chat: prompts (with the author's name), answers, errors | Local notes (hot reload results, …), settings, API keys |
| Prompts, code-editor edits, undo/redo, history jumps, import, clear, automatic repairs | PowerShell scripts and console apps a prompt produces run only on the PC of the person who sent it |

**One person changes the app at a time.** While someone works on a change, everybody else's prompt box is locked and the chat says *"Ben is working on a prompt: …"* — their app stays usable. The host decides whose turn it is, so two people pressing Enter at the same moment cannot collide; the one who has to wait keeps their prompt text. The AI call runs on the machine of the person who prompted, with their key or plan.

A **living app** can share its session too: its user presses Ctrl+Shift+N, clicks Share, and a friend who knows N.E.O. changes the app from afar while the user keeps working in it. Only code travels; the user's data never leaves their PC.

## How it works

```
 Anna (host)                                              Ben (guest)
 ───────────                                              ───────────
 Create invitation ── NEO1… (ICE user/password, DTLS fingerprint, addresses) ──►  Create reply
 Connect   ◄── NEO1… (Ben's credentials, fingerprint, addresses) ──────────────────
      ◄═══════ WebRTC data channel: direct, DTLS-encrypted, both punch out through their routers ═══════►
 Hello/Welcome, the session state (initial) ──────────────────────────────────────►  import, show
 turn → change → state (code, packages, history, chat) ──────────────────────────►  hot reload
                      ◄──────────────────────────── turn request / state from Ben
```

- **Codes** (`Neo.Collab/ConnectionCode.cs`): about 170 characters — ICE credentials, the SHA-256 fingerprint of the DTLS certificate, the public address (learned from a free public STUN server) and a few local ones, plus a checksum against copy mistakes. The fingerprint is what authenticates: only the holder of the matching key can finish the handshake, so a code that leaks cannot be used to break into a session the host has not accepted a reply for.
- **Transport** (`WebRtcPeerLink.cs`): [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) (pure C#, BSD) data channels. Both sides send outwards at the same time, which gets through ordinary home routers (NAT hole punching) — no port forwarding, no fixed port. While the codes travel, the guest's ICE agent keeps checking (its give-up time is raised from 16 seconds to 5 minutes) and the host refreshes its public address with the STUN server, so both routers keep their holes open.
- **Messages** (`MessageFramer`): Brotli-compressed, in pieces of 60 KB (the data channel takes at most 256 KB per message; a long AI history is larger).
- **Session** (`SharedSession.cs`): a star — guests connect to the host, which forwards every change to the others. Turns, joining (the host's state goes to whoever joins), leaving, heartbeat (25 seconds without a sign of life ends a connection), names made unique.
- **In N.E.O.** (`AppController.Sharing.cs`): every way the app changes goes through `RunSharedChangeAsync` — take the turn, change, publish the state, give the turn back. A state from someone else is applied like a code-editor edit (new packages first, then compile and hot reload) and becomes a node in the local undo history; joining and imports replace the session like an import. `SessionChatRecorder` collects the chat lines a change produces.

## Limits and good to know

- **Networks that block UDP** (many company, school, hotel and mobile networks) cannot connect directly, and there is no relay: the connection attempt fails after a while with a message. Two people on the same local network always connect.
- **Windows Firewall** asks the first time N.E.O. shares whether it may communicate on the network. Allow it (private networks are enough).
- **Trust**: what the others change runs as code on your PC, with your rights (the Avalonia host has no sandbox). Share only with people you trust.
- Everyone needs **the same N.E.O. version and UI framework** (Avalonia or WPF; Python and React modes too). A guest that does not fit is turned away with the reason.
- The session lives with the host: when the host leaves, it ends for everybody; everyone keeps the app as it was.
- Two N.E.O.s on one PC (for trying it out) each build into a folder of their own (`InstanceFolder`: the second one uses `…\Neo\Instances\1`).

## Code map

| Piece | Where |
|---|---|
| Codes, SDP from/to code | `Neo.Collab/ConnectionCode.cs` |
| WebRTC link, keep-alive, candidate choice | `Neo.Collab/WebRtcPeerLink.cs` |
| Framing, compression; in-memory link for tests | `Neo.Collab/PeerLink.cs` |
| Protocol, session state | `Neo.Collab/SessionMessages.cs` |
| Turns, relay, joining, heartbeat | `Neo.Collab/SharedSession.cs` |
| Wiring into N.E.O. | `Neo.App.Core/AppController.Sharing.cs`, `SessionChatRecorder.cs`, `InstanceFolder.cs` |
| Share dialog | `Neo.App.Avalonia/Views/ShareSessionWindow.axaml`, `Neo.App/Views/ShareSessionWindow.xaml` |
| Tests | `Neo.Collab.Tests` (codes, framing, real WebRTC in one process incl. a reply pasted 25 s late and 1.5 MB messages, session rules with three participants), `Neo.App.Core.Tests/SharedSessionControllerTests` (two real `AppController`s over real WebRTC: join, changes both ways, turn lock, undo, leave) |
