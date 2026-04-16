# Neo.ExcelMcp — Test Bring-Up

Minimale, experimentelle Version. Ein Tool (`excel_context`), ein Pipe, eine Bridge.
Ziel: beweisen, dass die Architektur trägt. Alles Polish (Installer, Trust-UI,
Writes, Pulse-Animation) kommt erst, wenn dieser Proof steht.

```
Claude (stdio) ──► Neo.ExcelMcp.Bridge.exe ──(named pipe)──► Neo.ExcelMcp.AddIn.xll
                                                                     │
                                                                     ▼
                                                               Excel.exe (COM)
```

---

## Build

Beide Projekte bauen standalone, kein `.sln`-Eintrag nötig:

```bash
dotnet build Neo.ExcelMcp/Neo.ExcelMcp.AddIn   -c Debug
dotnet build Neo.ExcelMcp/Neo.ExcelMcp.Bridge  -c Debug
```

Build-Output (verifiziert auf dieser Maschine):

| Was | Pfad |
|---|---|
| **Add-in (Excel lädt das)** | `Neo.ExcelMcp/Neo.ExcelMcp.AddIn/bin/Debug/net9.0-windows/publish/Neo.ExcelMcp.AddIn64-packed.xll` |
| **Bridge (Claude startet das)** | `Neo.ExcelMcp/Neo.ExcelMcp.Bridge/bin/Debug/net9.0/Neo.ExcelMcp.Bridge.exe` |

Die `-packed.xll` hat die Managed-DLL LZMA-komprimiert inside — einzelne Datei,
Hot-Reload ohne Excel-Neustart funktioniert (`LoadFromBytes="true"` im .dna File).

---

## Excel: Add-in laden

1. Excel 365 x64 starten (die 32-bit-Variante wird nicht unterstützt — wir bauen nur `AddIn64`).
2. **Datei → Optionen → Add-Ins → Verwalten: Excel-Add-Ins → Gehe zu → Durchsuchen…**
3. Die `Neo.ExcelMcp.AddIn64-packed.xll` aus dem Publish-Ordner oben auswählen.
4. Haken setzen, OK.

**Verifikation**: Öffne in einem zweiten Terminal das Log und lass es live mitlaufen:

```powershell
Get-Content "$env:LOCALAPPDATA\NeoExcelMcp\addin.log" -Wait -Tail 30
```

Du solltest sehen:
```
... [INFO] [T1] AutoOpen: log at C:\Users\...\AppData\Local\NeoExcelMcp\addin.log
... [INFO] [T1] PipeServer started on \\.\pipe\neo-excel-test
... [INFO] [T4] Waiting for client connection...
```

Wenn `AutoOpen` fehlt → Excel hat den Add-in nicht geladen. Häufige Ursachen:
- Falsche .xll (32-bit statt 64-bit)
- .NET 9 Desktop Runtime fehlt — [Download](https://dotnet.microsoft.com/download/dotnet/9.0)
- Excel blockiert den Add-in als untrusted. In Excel: **Datei → Optionen → Trust Center → Einstellungen für das Trust Center → Vertrauenswürdige Speicherorte** — Publish-Ordner hinzufügen.

---

## Claude: Bridge registrieren

### Claude Desktop

Datei: `%APPDATA%\Claude\claude_desktop_config.json`

```json
{
  "mcpServers": {
    "neo-excel": {
      "command": "C:\\Home\\Code\\nw.Create.VX-Avalonia-OpenSource\\Neo.ExcelMcp\\Neo.ExcelMcp.Bridge\\bin\\Debug\\net9.0\\Neo.ExcelMcp.Bridge.exe"
    }
  }
}
```

Claude Desktop komplett beenden und neu starten (Tray-Icon rechtsklick → Quit,
nicht nur das Fenster schließen).

### Claude Code (in diesem Repo)

```bash
claude mcp add neo-excel "C:\Home\Code\nw.Create.VX-Avalonia-OpenSource\Neo.ExcelMcp\Neo.ExcelMcp.Bridge\bin\Debug\net9.0\Neo.ExcelMcp.Bridge.exe"
```

Oder manuell in `.mcp.json`:

```json
{
  "mcpServers": {
    "neo-excel": {
      "type": "stdio",
      "command": "C:\\Home\\Code\\nw.Create.VX-Avalonia-OpenSource\\Neo.ExcelMcp\\Neo.ExcelMcp.Bridge\\bin\\Debug\\net9.0\\Neo.ExcelMcp.Bridge.exe",
      "args": []
    }
  }
}
```

---

## Der erste Test

1. Excel offen, ein beliebiges Workbook geladen (leer oder mit Daten), irgendwas selektiert.
2. Im Log die Zeile `Waiting for client connection...` muss stehen.
3. In Claude sagen:
   > *Ruf das Tool `excel_context` auf und zeig mir das Ergebnis.*
4. Erwartete Antwort von Claude:
   ```json
   {
     "result": {
       "workbook": "Mappe1.xlsx",
       "activeSheet": "Tabelle1",
       "selection": "$B$2:$D$10",
       "rows": 9,
       "cols": 3,
       "sheets": ["Tabelle1", "Tabelle2"]
     }
   }
   ```
5. Im Log solltest du sehen:
   ```
   ... [INFO] [T4] Client connected
   ... [INFO] [T4] <- {"method":"excel_context","params":{}}
   ... [INFO] [T1] -> {"result":{"workbook":"Mappe1.xlsx",...}}
   ... [INFO] [T4] Client disconnected
   ... [INFO] [T4] Waiting for client connection...
   ```

Beachte die Thread-IDs: **T4** ist ein ThreadPool-Thread (der Pipe-Server).
**T1** ist Excels Main-Thread (der Reply wird geschrieben, nachdem
`ExcelAsyncUtil.QueueAsMacro` das COM-Work auf den Main-Thread marshallt
und zurückkommt). Wenn beide IDs gleich wären, wäre unser STA-Marshalling
kaputt — sehr unwahrscheinlich, aber ein schnelles visuelles Sanity-Check.

---

## Akzeptanzkriterien

Nur zwei. Beide müssen stimmen:

1. `excel_context` erscheint in der Tool-Liste von Claude und liefert echte
   Workbook-Daten für ein beliebiges offenes Excel-Fenster.
2. Nach 10 Tool-Calls und Excel-Schließen steht **kein** `EXCEL.EXE` mehr
   im Task Manager. (Zombie-Process-Check: wenn doch → COM-Release-Leak
   in `ExcelGateway.GetContextAsync`.)

Wenn beide stimmen, ist die Architektur validiert und `excel_read`/`excel_write`
können geradeaus drauf.

---

## Debug-Gotchas

**Bridge crasht sofort beim Claude-Start**
Stdin/Stdout sind tabu für Logs — die Bridge schreibt alles nach stderr (und
Claude Code zeigt stderr im MCP-Log). `%APPDATA%\Claude\logs\mcp-server-neo-excel.log`
hat die ersten Meldungen.

**Bridge meldet "Could not connect to pipe"**
Excel läuft nicht, oder der Add-in wurde nicht geladen. Check:
- `addin.log` existiert und hat aktuelle Zeilen
- `\\.\pipe\neo-excel-test` existiert (PowerShell: `Get-ChildItem \\.\pipe\ | Where-Object Name -like '*neo-excel*'`)

**Call timeout nach 30 s**
Excel ist busy — F2-Modus, ein offener Dialog, oder lange Neuberechnung.
Excel beenden, schließen und wieder öffnen, erneut versuchen.

**Excel-Zombie im Task Manager nach Excel-Close**
COM-Release-Leak. Der Add-in hält noch eine COM-Referenz, die nicht released
wurde. In `ExcelGateway.GetContextAsync` suchen: jede `dynamic`-Variable, die
über `.` aus einem COM-Objekt gezogen wird, muss im `finally` released werden.
Einzige Ausnahme: `ExcelDnaUtil.Application`.

**Der Add-in lädt, aber `AutoOpen` läuft nie**
Excel hat die .xll als untrusted blockiert. Trust Center → Vertrauenswürdige
Speicherorte → Publish-Ordner hinzufügen. Alternativ: die .xll nach
`%APPDATA%\Microsoft\AddIns\` kopieren — diesen Ordner trusted Excel per default.

**Hot-Reload-Cycle (sobald was funktioniert)**
Dank `LoadFromBytes="true"` im .dna File hält Excel keinen File-Lock auf die .xll.
Du kannst die .xll neu bauen, ohne Excel zu schließen:
1. Add-in in Excel deaktivieren (Excel-Add-Ins → Haken weg → OK)
2. `dotnet build` erneut aufrufen
3. Add-in wieder aktivieren (Haken setzen → OK)
Das spart viel Zeit im Dev-Cycle.

---

## Was absichtlich fehlt

- Keine Writes, keine Trust-UI, keine Pulse-Animation
- Keine Ribbon-Präsenz, kein Status-Dot
- Keine Installer-.exe, keine Auto-Config
- Nur `excel_context` — kein `excel_read`, kein `excel_list_tables`
- Kein Multi-Instance-Handling — eine Excel-Instanz, fertig
- Kein Retry/Reconnect auf der Bridge-Seite — frische Connection pro Call
- Kein Auth/Token — wir sind auf Named Pipes, lokale Maschine, Test-Phase

Jedes dieser Dinge steht im großen Plan — und kommt erst nach dem Proof.
