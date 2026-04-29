# Live-MCP — Handover-Briefing für Claude Code CLI

> **Du übernimmst eine Architektur-Diskussion aus einer vorherigen Claude-App-Session.**
> Plan ist fertig, Entscheidungen sind getroffen, du startest mit Phase 0.

## TL;DR

N.E.O. bekommt **Live-MCP**: jede generierte App wird selbst zum MCP-Server. Vollständiger Plan in [LIVE_MCP_VISION.md](LIVE_MCP_VISION.md). Drei Akte:

1. **The App speaks** (Channels, existiert) — `Ai.Trigger`
2. **The App listens** (Dev-Mode, Phase 1-3) — Claude ruft Methoden, liest State, simuliert Input
3. **The App lives alone** (Frozen-Mode, Phase 4-5) — generated app = standalone stdio-MCP-EXE

## Festgenagelte Entscheidungen (vom User bestätigt 2026-04-29)

| Punkt | Entscheidung |
|---|---|
| Name | **"Live-MCP"** — kein Marketing-Brand, kein "Living Apps" |
| Reihenfolge | **Strikt sequenziell.** Phase N+1 erst, wenn N abgeschlossen. Keine Parallelisierung. |
| Client-Scope | **Nur Claude Code CLI.** Claude Desktop & Cowork explizit out-of-scope für v1. |

→ Diese drei Punkte sind im VISION-Doc unter "## Decisions (Locked 2026-04-29)" verankert. Nicht ohne Rückfrage abweichen.

## Was schon existiert

| Bereich | Pfad | Was es liefert |
|---|---|---|
| MCP-Server (heute, 25 Tools) | [Neo.McpServer/Program.cs](Neo.McpServer/Program.cs) | Host, channel capability, Tool-Discovery via Reflection |
| MCP-Tools | [Neo.McpServer/Tools/PreviewTools.cs](Neo.McpServer/Tools/PreviewTools.cs) | `compile_and_preview`, `set_property`, `inject_data`, ... |
| Channels-API | [Neo.App.Api/Ai.cs](Neo.App.Api/Ai.cs) | `Ai.Trigger`, `Ai.ScheduleTrigger` (statisches Emitter-Pattern) |
| Plugin-Host (MCP-Variante) | [Neo.PluginWindowAvalonia.MCP/App.axaml.cs](Neo.PluginWindowAvalonia.MCP/App.axaml.cs) (Zeilen ~150) | Setzt Emitter, kommuniziert via Pipe |
| IPC-Protokoll | [Neo.IPC/IPC.cs](Neo.IPC/IPC.cs) | Frame-Typen (ControlJson/BlobStart/Chunk/End), `IpcTypes.AppEvent`, `IpcEnvelope` |
| Channels-Doku | [docs/wiki/Channels.md](docs/wiki/Channels.md) | Wie das Push-System funktioniert |
| Session-Manager | [Neo.McpServer/Services/PreviewSessionManager.cs](Neo.McpServer/Services/PreviewSessionManager.cs) | `PushChannelEventAsync`, `ListenLoopAsync` — hier verzweigen neue IPC-Typen |

## Dein erster Job: Phase 0 (3 Tage budgetiert)

Reality-Check-Spike. Outcome: Go/No-Go-Entscheidung für Phase 2 (dynamische Tool-Registrierung).

### Tasks

**Task 1 — `notifications/tools/list_changed` Spike**

Frage: Bekommt Claude Code CLI mit, wenn ein MCP-Server seine Tool-Liste während einer Session ändert?

Vorgehensvorschlag:
- Minimal-MCP-Test-Projekt anlegen (oder kleines Subprojekt in `Neo.McpServer/Tests/Spike/`)
- Server registriert initial 1 Tool, sendet nach 5s `notifications/tools/list_changed` mit 2 Tools
- In Claude Code CLI einhängen, beobachten ob das zweite Tool sichtbar wird ohne Restart
- Falls **ja** → Phase 2 (dynamische Tools, `app.<id>.<method>`) ist real
- Falls **nein** → Phase 2 entfällt, Phase 1 macht stattdessen Meta-Tool-only (`invoke_method(app_id, method, params)`); VISION.md entsprechend updaten

**Task 2 — Subscription-Spike (für Phase 2 `watch_observable`)**

Frage: Welcher MCP-Notification-Pfad eignet sich für Property-Change-Subscriptions?

Optionen:
- Custom-Capability analog `claude/channel` (siehe [Program.cs:42-46](Neo.McpServer/Program.cs#L42))
- Standard-Pfad wie `notifications/resources/updated`
- Fallback: Polling von Claude-Seite

Output: Ein dokumentierter Mechanismus mit Pseudocode-Beispiel.

**Task 3 — Loop-Protection-Modell**

Wenn ein `invoke_method`-Aufruf eine Methode trifft, die intern `Ai.Trigger` ruft, entsteht ein Loop:
```
Claude → invoke_method → App.Method → Ai.Trigger → Claude → invoke_method → ...
```

Modell ausarbeiten:
- Depth-Counter im `AppEventMessage` (neues Feld) und im `InvokeMethod`-Frame (neues Frame)
- Counter wird vom Server inkrementiert beim Forward, von der App durchgereicht
- Default-Limit: 5. Konfigurierbar via Env (`NEO_LIVEMCP_MAX_DEPTH`)
- Bei Limit-Überschreitung: error response, telemetry log
- Pseudocode in VISION.md unter "## Spielregeln" ergänzen

### Phase-0-Abschluss

PR mit:
- Spike-Code (kann nach Phase 0 wieder weg)
- Update von `LIVE_MCP_VISION.md`: neue Sektion "## Phase 0 — Findings" mit ✓/✗ pro Task
- Bei Pivot (Task 1 = nein): Roadmap angepasst

Dann User berichten, Freigabe für Phase 1 holen.

## Setup vor Start

Damit Phase-1-Tests später überhaupt gehen:

1. **Claude Code CLI mit Channels:**
   ```bash
   claude --dangerously-load-development-channels server:neo-preview
   ```
   (Channels sind für Phase 0 noch nicht direkt nötig — aber für jeden End-to-End-Test ab Phase 1.)

2. **Neo.McpServer in `.claude/settings.json` registriert.** Falls noch nicht: siehe [docs/wiki/MCP-Server.md](docs/wiki/MCP-Server.md).

3. **Build-Sanity-Check:**
   ```bash
   dotnet build Neo.McpServer -c Release
   dotnet build Neo.PluginWindowAvalonia.MCP -c Release
   ```

## Was du *nicht* tun sollst (Anti-Tasks)

- Nicht mit Phase 1 anfangen, bevor Phase 0 abgeschlossen und vom User freigegeben ist.
- Nicht versuchen, Cowork- oder Claude-Desktop-Kompatibilität "nebenbei" einzubauen.
- Den Marketing-Namen "Living Apps" nicht in die Codebase einsickern lassen — überall "Live-MCP".
- Keine `Neo.App.Api`-Erweiterung ohne Rücksprache (wegen des kürzlich stattgefundenen Renames `Neo.App.Neo → Neo.App.Ai` — das Naming-Risiko in der Assembly ist heikel).

## Auto-Memory

Es gibt einen Project-Memory-Eintrag `project_live_mcp.md` mit Status und Roadmap-Stand. Beim Start sollte er geladen sein.

## Bei Unklarheit

Frag den User. Lieber eine kurze Klärung als drei Tage in die falsche Richtung. Der User ist Hobby-Entwickler mit klarer Vision; er will keine Fehlinterpretation, aber auch keine Mikromanagement-Rückfragen.
