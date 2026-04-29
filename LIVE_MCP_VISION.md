# Live-MCP — N.E.O. Vision

> **Status:** Architecture proposal · April 2026 · pre-implementation · **decisions locked 2026-04-29**
> **Name:** "Live-MCP" (überall, kein Marketing-Brand)
> **Modi:** *Dev-Mode* (sandboxed via Neo.McpServer) und *Frozen-Mode* (standalone exported EXE)

## Eine Zeile

Heute generiert N.E.O. Apps. Morgen generiert N.E.O. **Apps, die selbst zu MCP-Servern werden** — first-class citizens im Claude-Ecosystem, exportierbar als standalone Tools, von jedem N.E.O.-fremden Nutzer mit `claude mcp add` installierbar.

---

## Die drei Akte

| Akt | Status | Was es tut |
|---|---|---|
| **I — The App speaks** (Channels) | Live | App pusht Prompts an Claude (`Ai.Trigger`) |
| **II — The App listens** (Live-MCP, Dev-Mode) | This plan, Phase 1-3 | Claude ruft Methoden, liest State, simuliert Input |
| **III — The App lives alone** (Live-MCP, Frozen-Mode) | This plan, Phase 4-5 | Generated app = standalone stdio-MCP-Server-EXE, no Neo runtime needed at the consumer |

Akt I ist der Push-Channel. Akt II ist der Pull-Channel. Akt III ist die Befreiung — die App löst sich vom N.E.O.-MCP-Server und wird selbst eine.

---

## Warum das groß ist

Bisher hat N.E.O. eine einzige Erzählung: *„Beschreib eine App, ich baue sie."* Die App lebte im Sandkasten von N.E.O. — wenn der User Claude wechselt, ist die App weg.

Mit Live-MCP wird N.E.O. zur **Fabrik für MCP-Tools**:

1. **Du beschreibst eine App.** Claude generiert sie, du testest sie live im Dev-Mode.
2. **Du frostest sie ein.** N.E.O. exportiert ein single-file EXE, das simultan eine GUI hat **und** ein MCP-Server ist.
3. **Du teilst sie.** Per URL, GitHub-Release, Skill-Registry. Andere Nutzer installieren sie mit einem `claude mcp add`-Befehl.
4. **Sie wird Teil des Tool-Ökosystems.** Claude (oder ein anderes Agent-Framework) nutzt sie wie Slack, GitHub, Linear oder Excel-MCP.

**Das ist die Disruption.** Heute baut N.E.O. Apps für N.E.O.-Nutzer. Morgen baut N.E.O. Werkzeuge, die das gesamte AI-Tooling-Ecosystem nutzt — aus Prompts, in Sekunden, ohne Boilerplate-Server.

Die N.E.O.-Tagline ändert sich von

> *„Beschreib eine App, ich baue sie."*

zu

> *„Beschreib ein Tool, ich baue es — ready für Claude und das ganze MCP-Ökosystem, in Sekunden."*

---

## Drei Achsen einer Live-MCP-App

Jede Live-MCP-App exponiert drei Schnittstellentypen, alle **opt-in via Attribut**:

### 1. Capabilities — was die App kann

```csharp
[McpCallable("Filtert Produktliste nach Kategorie und Mindestpreis.")]
public void ApplyFilter(string category, decimal minPrice) { … }

[McpCallable("Lädt Daten frisch aus der API neu.", OffUiThread = true, TimeoutSeconds = 60)]
public async Task<int> RefreshFromApi() { … }
```

### 2. State — was die App weiß

```csharp
[McpObservable("Anzahl der gerade sichtbaren Produkte.")]
public int VisibleProductCount => _filtered.Count;

[McpObservable("Aktuelle Filter-Kategorie.", Watchable = true)]
public string CurrentCategory { get; private set; }
```

`Watchable = true` ⇒ Claude kann eine `watch_observable`-Subscription öffnen; bei jeder Änderung kommt eine MCP-Notification — kein Polling.

### 3. Surface — wie die App sich zeigt

```csharp
[McpTriggerable("Refresh-Button anklicken.")]
public Button RefreshButton { get; }
```

Drei Strategien (alle aus Phase 3 verfügbar, wählbar pro Aufruf):

- **Semantic** — direkter `Click()`-Call (am stabilsten, einfach)
- **EventSystem** — Avalonia-Event mit Bubbling/Tunneling (deckt UI-Logik ab)
- **InputSystem** — echter Input durch die Avalonia-Input-Pipeline (für Test-Automation, deckt Hover/Focus/Mouse-Coordinates ab)

---

## Architektur

### Dev-Mode (Phase 1-3)

App läuft im N.E.O.-Sandbox via `Neo.PluginWindowAvalonia.MCP`. Hot-reload, dynamic tool registration, multi-window.

```
       ┌─────────────────┐
       │  Claude Code    │
       └────┬────────┬───┘
            │ stdio  │ stdio (channel notifications)
            ▼        ▲
       ┌─────────────────┐
       │  Neo.McpServer  │
       │  (host)         │
       └─┬──────┬────────┘
         │ Pipe │ Pipe
         ▼      ▼
   ┌───────┐ ┌───────┐
   │ App A │ │ App B │   ← Hot-reloadable, multi-window
   └───────┘ └───────┘
```

Tool-Naming: `app.<appId>.<methodName>` (z. B. `app.products_dashboard_7af3.apply_filter`).

### Frozen-Mode (Phase 4-5)

Generated app + `Neo.App.Mcp` runtime → single-file EXE. Eigener stdio-MCP-Server. **Keine N.E.O.-Installation beim Endnutzer nötig.**

```
       ┌──────────────────┐
       │   Claude Code    │
       └─┬────────┬───────┘
         │ stdio  │ stdio
         ▼        ▼
  ┌─────────┐ ┌──────────────────┐
  │  Slack  │ │ MyTodoApp.exe    │
  │  MCP    │ │ ↳ MCP server     │
  └─────────┘ │ ↳ Avalonia GUI   │
              └──────────────────┘
```

Die EXE startet wahlweise:

| Aufruf | Modus |
|---|---|
| `MyTodoApp.exe` | GUI-Modus (legacy, wie heutiger Export) |
| `MyTodoApp.exe --mcp` | stdio-MCP + GUI sichtbar (Claude steuert, User schaut zu) |
| `MyTodoApp.exe --mcp --headless` | stdio-MCP, kein Fenster (Server-Deployment) |

Im `--mcp`-Modus gehen Logs auf stderr, JSON-RPC auf stdout — analog `Neo.McpServer`.

---

## Spielregeln

1. **Opt-in.** Nichts ist exponiert ohne `[McpCallable/Observable/Triggerable]`. Sicherer Default.
2. **Manifest-frozen-on-load.** Manifest wird beim App-Start einmal via Reflection gebaut, nicht zur Laufzeit verändert (außer bei explizitem `RefreshManifest`-Aufruf).
3. **Loop-Protection (Phase 0 finalisiert).** Server-seitiger `LoopProtection`-Service hält pro `appId` eine `CallChain { Hops, LastActivityUtc }`. Bei jedem `invoke_method` aus Claude wird inkrementiert; Decay 30 s ohne Aktivität setzt zurück. Default-Limit: 5 Hops. Env-Konfig: `NEO_LIVEMCP_MAX_DEPTH`. Bei Überschreitung: `MethodResult { Success=false, ErrorCode="loop_limit_exceeded" }` + stderr-Log. AppEvents von `Ai.Trigger` tragen `Hops` als Telemetrie und seeden den Server-Counter (`Math.Max`) — damit reisst eine Cross-App-Kette nicht das Budget zurück. Vollständiger Pseudocode: [LIVE_MCP_PHASE0_FINDINGS.md § Task 3](LIVE_MCP_PHASE0_FINDINGS.md).
4. **Sandboxing.** Dev-Mode bleibt im Collectible AssemblyLoadContext (heutige Sicherheit). Frozen-Mode hat denselben Trust wie jede andere lokale EXE — der Endnutzer entscheidet bei `mcp add`.
5. **Idempotente Tool-Identity.** Tool-Name folgt deterministisch aus `appId` (stable hash über Class+Methods) + Methodenname. Hot-reloads behalten Tool-Names, solange Signaturen unverändert sind. Bei Signatur-Änderung: neues Tool, altes wird per `tools/list_changed` entfernt.
6. **Multi-Window-aware.** Tool-Names enthalten `windowId` als Suffix nur, wenn mehrere Instanzen derselben App offen sind (1 Instanz: kein Suffix, kürzer für Claude).
7. **Deferred-schema-fetch latency (Phase 0 finalisiert).** Wenn ein Tool mid-session via `tools/list_changed` registriert wird, ist es in Claude Code CLI zunächst als *deferred entry* sichtbar — Claude muss das Schema mit `ToolSearch` (`select:<name>`) nachladen, bevor er es aufrufen kann. Heißt für Live-MCP: eine App, die ein Tool registriert und im selben Turn `Ai.Trigger("call app.foo.bar")` ruft, kann scheitern, weil Claude das Schema noch nicht nachgeladen hat. **Empfehlung:** Tools bei App-Start registrieren, nicht reaktiv mitten in einer Aufrufkette. (Belegt in [LIVE_MCP_PHASE0_FINDINGS.md § Task 1](LIVE_MCP_PHASE0_FINDINGS.md).)
8. **Watchable observables = MCP resources (Phase 0 finalisiert).** Jede `[McpObservable(Watchable=true)]`-Property wird als Resource mit URI `app://<appId>/<observable>` exponiert. Claude subscribiert via `resources/subscribe`; Server pusht `notifications/resources/updated` (mit 200 ms Coalesce-Window für rapid-fire Changes); Claude liest mit `resources/read`. Server cached den letzten Wert aus IPC-`ObservableValue`-Frames. App-side: `INotifyPropertyChanged` wenn vorhanden, sonst Polling-Fallback (default 500 ms, configurable). Standard-MCP-Path → portabel über CLI / Desktop / Cowork / third-party — wichtig für Frozen-Mode. Vollständiger Pseudocode: [LIVE_MCP_PHASE0_FINDINGS.md § Task 2](LIVE_MCP_PHASE0_FINDINGS.md).

---

## Roadmap

7-8 Wochen, in 6 Phasen. **Strikt sequenziell — Phase N+1 erst, wenn N abgeschlossen und vom User freigegeben.** Phase 0 ist non-negotiable und deckt das einzige existenzielle Risiko auf.

### Phase 0 — Reality Check (3 Tage)

- **Spike `notifications/tools/list_changed`** mit Claude Code 2.x. Kommt es beim User an? Wird die Tool-Liste re-fetched? Mitigation für nicht-unterstützende Clients (Claude Desktop, Cowork) entwerfen.
- **Spike Subscription-Pattern.** Welcher MCP-Notification-Pfad funktioniert für `watch_observable`? Falls keiner: Polling-Fallback skizzieren.
- **Loop-Protection-Modell** finalisieren (call-stack tracking, depth limits, telemetry).

**Ausgang:** Go/No-Go-Entscheidung für Phase 2. Bei No-Go: Pivot auf Meta-Tool-only Architektur (Phase 1 + 3-5 funktionieren ohne `tools/list_changed`).

### Phase 1 — Manifest & Invocation (2 Wochen)

- **M1.1** Attribute-Set in `Neo.App.Api`: `McpCallableAttribute`, `McpObservableAttribute`, `McpTriggerableAttribute` (mit Optionen: `OffUiThread`, `TimeoutSeconds`, `Watchable`).
- **M1.2** `ManifestBuilder` (Reflection über loaded UserControl, einmal beim Plugin-Load).
- **M1.3** IPC-Frames: `AppManifest`, `InvokeMethod`, `MethodResult`, `ReadObservable`, `ObservableValue`. Erweiterung von `Neo.IPC/IPC.cs`.
- **M1.4** Generic `invoke_method(app_id, method_name, params)` MCP-Tool. Statisch, immer da, funktioniert auch ohne `tools/list_changed`.
- **M1.5** Generic `read_observable(app_id, observable_name)`.
- **M1.6** Generic `inspect_app_api(app_id)` → returns manifest in human-readable form.
- **M1.7** Code-Agent System-Prompt erweitern: "Wenn der User Methoden/Properties/Events beschreibt, die Claude nutzen soll, exponiere sie mit `[McpCallable]/[McpObservable]/[McpTriggerable]` plus aussagekräftige Description."
- **M1.8** 5 Sample-Apps mit Live-MCP-Annotations + 5 End-to-End-Eval-Cases (idealerweise als CI-Test).

**Demo Phase 1:** Claude ruft `invoke_method` auf einer TODO-App, fügt 5 Items hinzu, liest `read_observable` für ItemCount, verifiziert via `capture_screenshot`. **Vollständig autonom, ohne `simulate_input`.**

### Phase 2 — Dynamic Tools & Subscriptions (1 Woche)

- **M2.1** Pro-Methoden-Tool-Generation (`app.<id>.<method>`). Aktiv nur, wenn Phase-0-Spike "Go" ergab.
- **M2.2** `notifications/tools/list_changed` push beim App-Start/-Stop/-Hot-Reload.
- **M2.3** `watch_observable` mit MCP-Notifications für `Watchable=true`-Properties. Property-Change-Detection via INotifyPropertyChanged oder polling-fallback.
- **M2.4** Hot-reload-stable Tool-Identity (deterministisches Hashing über Class+Method+Signature).

### Phase 3 — Events & Inputs (1 Woche)

- **M3.1** `raise_event(app_id, control_name, event_name, args)` mit Avalonia-Event-System.
- **M3.2** `simulate_input(app_id, control_name, input_kind, params)` — echter Input durch die Avalonia-Input-Pipeline (mouse-down/up, keyboard, focus, hover). **Test-Automation-grade.**
- **M3.3** Multi-Window-Routing (windowId in Tool-Params, falls mehrere Instanzen).

**Demo Phase 3:** Claude testet eine generierte App End-to-End — füllt Form via `simulate_input`, klickt echten Submit-Button, verifiziert Resultat via `read_observable` + `capture_screenshot`. **Self-testing apps.**

### Phase 4 — Standalone MCP Export (2 Wochen)

**Hier kippt die Geschichte.** Dies ist der eigentliche Game-Changer.

- **M4.1** Neue Library `Neo.App.Mcp`: embedded MCP-Server, der das App-eigene Manifest exponiert. Re-uses Phase-1-Manifest-Builder, aber direkt im App-Prozess statt über IPC.
- **M4.2** Export-Pipeline-Erweiterung: generated app + `Neo.App.Mcp` + ModelContextProtocol → single-file trimmed/AOT EXE. Erweitert das existierende `export_app` Tool um `--mcp-mode`.
- **M4.3** CLI-Modi: `--mcp`, `--headless`, `--mcp-help` (zeigt Manifest auf stderr).
- **M4.4** Auto-generated stdio-Entrypoint, der dasselbe Manifest produziert wie der Dev-Mode (Konsistenz-Garantie).
- **M4.5** Endnutzer-Doku: "So installierst du eine N.E.O.-App als MCP-Server" + Code-Signing-Guide.

**Demo Phase 4:** Claude generiert und friert eine App "GitHub-Issue-Tracker" ein. EXE liegt auf der Disk. User auf einem **anderen Rechner** installiert sie mit `claude mcp add issue-tracker ./issue-tracker.exe -- --mcp`. Claude auf dem zweiten Rechner kann sie sofort nutzen, **ohne N.E.O. installiert zu haben**. Das ist der "Holy Shit"-Moment.

### Phase 5 — Composability & Marketplace (1 Woche)

- **M5.1** App-to-App Discovery: eine Live-MCP-App im Dev-Mode kann andere registrierte Apps via `Ai.GetTool("appA.method")` aufrufen → Claude vermittelt.
- **M5.2** Skills-Registry (`Neo.McpServer/Services/SkillsRegistry.cs`) erweitert: nicht nur `.neo`-Source-Sessions, sondern auch frozen MCP-EXE-Pfade und URLs.
- **M5.3** `claude mcp add` Snippet pro Skill auto-generieren, copy-paste-ready.
- **M5.4** 60-Sekunden-Demo-Video: "From prompt to MCP-tool in 90 seconds."

---

## Killer-Demos

Sortiert nach narrativer Wucht — jede dieser Demos ist standalone tweetbar:

1. **Self-Testing TODO** (Phase 3): "Build a TODO app, then test it." Claude generiert → triggert eigene Inputs via `simulate_input` → verifiziert State via `read_observable` → Done. **Kein menschlicher Klick.**
2. **Excel × Live-MCP-App** (Phase 1): Excel-MCP + Live-MCP. User füllt Form in einer N.E.O.-App, Claude orchestriert Daten zwischen Form und Excel-Tabelle. Beidseitige State-Sync. Beweis: Zwei MCPs reden über Claude miteinander.
3. **App-as-Tool Export** (Phase 4): User exportiert "Currency Converter"-App als EXE. `claude mcp add` auf einem anderen System. Tool taucht in Claude auf wie Slack-MCP. **Mind blown.**
4. **App-Pipeline** (Phase 5): App A scrapt Reddit, App B visualisiert Sentiment. Claude pipet zwischen ihnen. Multi-App-Workflow ohne ein einziges Glue-Skript.
5. **Channel × Live-MCP Loop** (Phase 1+2): App pusht via `Ai.Trigger` → Claude reagiert via `invoke_method` → App pusht weiter. Vollständig autonomer Agent-Loop in einer GUI-App. (Mit Loop-Protection.)

---

## Risiken & Mitigations

| Risiko | Wahrsch. | Wirkung | Mitigation |
|---|---|---|---|
| `tools/list_changed` ungenügend in Claude Code CLI supported | Mittel | Phase-2-Killer | Phase 0 Spike + Meta-Tool-only Fallback (Phase 1 funktioniert ohne) |
| LLM setzt `[McpCallable]`-Attribute nicht zuverlässig | Mittel | Live-MCP wird in der Praxis tot | Strong system prompt + 20 evals + automated linting in `compile_and_preview` ("Du hast Public-Methoden ohne Annotation, ist das gewollt?") |
| Reentrancy/Loops zwischen Channels und Live-MCP | Hoch (initial) | Cost explosion, infinite turns | Call-stack tracking, depth-limit (default 5), per-app rate limit, telemetry |
| Frozen EXE-Größe | Mittel | UX leidet bei "ich teil mal eben die App" | Trimming, AOT, optionales `--no-gui`-Bundling für headless |
| Sicherheit bei `mcp add` einer unbekannten EXE | Hoch | Schadet Endnutzer-Vertrauen, Reputationsrisiko | Code-signing tooling in Phase 5, klarer Disclaimer in Doku, optional `--sandboxed`-Flag (later phase, AppContainer-style) |
| Property-Change-Detection ohne INotifyPropertyChanged | Niedrig | Nur `Watchable=true` betroffen | Polling-Fallback (configurable interval), Convention "POCO + manual notify" dokumentieren |

---

## Decisions (locked 2026-04-29)

| Punkt | Entscheidung |
|---|---|
| Name | "Live-MCP" überall — keine separate Marketing-Brand. |
| Reihenfolge | **Strikt sequenziell.** Phase N+1 startet erst, wenn N abgeschlossen + freigegeben. |
| Client-Scope v1 | **Nur Claude Code CLI.** Claude Desktop und Cowork explizit out-of-scope. Spätere Phase ggf. nachziehen. |

---

## Offene Fragen

### Phase 0 — geklärt 2026-04-29

1. ~~**Genauer Stand `tools/list_changed`** in Claude Code CLI~~ → ✅ funktioniert. Auto-trigger und manueller Trigger beide bestätigt; Caveat: deferred schema fetch (siehe Spielregel 7). Belege: [LIVE_MCP_PHASE0_FINDINGS.md § Task 1](LIVE_MCP_PHASE0_FINDINGS.md).
2. ~~**MCP-Subscription-Pattern**~~ → ✅ entschieden: `notifications/resources/updated` (Standard-MCP), nicht Custom-Capability. Begründung: Portabilität für Frozen-Mode. Siehe Spielregel 8 + [LIVE_MCP_PHASE0_FINDINGS.md § Task 2](LIVE_MCP_PHASE0_FINDINGS.md).

### Offen für spätere Phasen

3. **Skills-Registry-Kompatibilität** (Phase 5): Sollen alte `.neo`-Sessions migrieren oder coexistieren mit Frozen-EXE-Skills?
4. **Trust-Modell für Frozen-EXEs** (Phase 4-5): Code-Signing? Manifest-Signaturen? Sandbox-Flag (später)? Wie kommunizieren wir Risiken in der Doku?

---

## Was am Ende anders ist

**Vorher:** N.E.O. ist ein App-Generator. Der Output bleibt im N.E.O.-Sandkasten, sichtbar nur im N.E.O.-Fenster.

**Nachher:** N.E.O. ist eine **Fabrik für MCP-Tools**. Jede generierte App ist:

- ✓ live von Claude bedienbar (Dev-Mode)
- ✓ live von Claude testbar (`simulate_input`)
- ✓ exportierbar zu einem standalone MCP-Tool (Frozen-Mode)
- ✓ teilbar mit anderen Nutzern (Skills-Registry oder direkter Distribution)
- ✓ kombinierbar mit anderen MCPs (Excel, Slack, GitHub, …)

Channels haben den Tunnel gegraben. Live-MCP definiert das Protokoll im Tunnel. Frozen-Mode öffnet beide Enden des Tunnels in die Welt.

Das ist die Krönung von N.E.O.
