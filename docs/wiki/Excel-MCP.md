# Excel MCP Server (Neo for Excel)

N.E.O. includes a second MCP server — **Neo for Excel** — that gives Claude direct, live access to the active Excel workbook. Unlike file-based Excel tools, this runs as an in-process COM add-in inside `Excel.exe` itself, with sub-50ms response times.

Combined with the existing [[MCP Server]], Claude can read your spreadsheet, generate a native desktop UI, and write results back — all from a single conversation.

## What This Enables

Claude sees your live workbook — the active sheet, your current selection, all tables. No file uploads, no copy-paste.

```
Claude Code / Desktop                Neo.ExcelMcp
+------------------+   stdio/pipe   +-----------------------------+
| "What's in my    | ----Bridge---> | Excel COM (in-process)      |
|  spreadsheet?"   |               | Named Pipe IPC              |
|                  | <------------ | Active workbook data        |
| "A1:C5 contains  |               |                             |
|  a budget table" |               +-----------------------------+
+------------------+                          |
                                   +----------v----------+
                                   | Excel.exe           |
                                   | (your workbook)     |
                                   +---------------------+
```

## Architecture

```
Claude <==(stdio)==> Neo.ExcelMcp.Bridge.exe <==(named pipe)==> Neo.ExcelMcp.AddIn.xll (inside Excel.exe)
```

- **Neo.ExcelMcp.AddIn** — An Excel-DNA `.xll` add-in that loads directly into the Excel process. Opens a Named Pipe server and handles tool requests by calling the Excel COM object model on the main (STA) thread.
- **Neo.ExcelMcp.Bridge** — A tiny STDIO-to-pipe translator. Claude spawns it as a standard MCP server (same pattern as Neo.McpServer). It forwards JSON-RPC requests to the add-in's Named Pipe and returns responses.

The bridge is invisible to the user — no window, no tray icon, ~8 MB in memory, starts and stops with Claude.

## Prerequisites

- .NET 9 Desktop Runtime (already installed if you run the N.E.O. WPF host)
- Microsoft Excel 365 x64 (Windows)
- The N.E.O. repository cloned and built

## Setup

### 1. Build

```bash
cd N.E.O
dotnet build Neo.ExcelMcp/Neo.ExcelMcp.AddIn -c Debug
dotnet build Neo.ExcelMcp/Neo.ExcelMcp.Bridge -c Debug
```

### 2. Load the add-in in Excel

1. Open Excel
2. **File > Options > Add-ins > Manage: Excel Add-ins > Go > Browse**
3. Select `Neo.ExcelMcp/Neo.ExcelMcp.AddIn/bin/Debug/net9.0-windows/publish/Neo.ExcelMcp.AddIn64-packed.xll`
4. Check the box, click OK

The packed `.xll` is a single file with the managed DLL embedded (LZMA-compressed).

> **Tip:** If Excel shows a ".NET Desktop Runtime 6.0.2" error, verify the `.dna` file contains `RuntimeVersion="v9"`. See Troubleshooting below.

### 3. Configure Claude

**Claude Code** (via CLI):
```bash
claude mcp add neo-excel -- "/path/to/Neo.ExcelMcp/Neo.ExcelMcp.Bridge/bin/Debug/net9.0/Neo.ExcelMcp.Bridge.exe"
```

**Claude Desktop** (`%APPDATA%\Claude\claude_desktop_config.json`):
```json
{
  "mcpServers": {
    "neo-excel": {
      "command": "/path/to/Neo.ExcelMcp/Neo.ExcelMcp.Bridge/bin/Debug/net9.0/Neo.ExcelMcp.Bridge.exe"
    }
  }
}
```

Use absolute paths. Restart Claude after changing the config.

### 4. Verify

Open Excel with a workbook. In Claude, say:

> "Call excel_context and show me what you see."

Claude should return your workbook name, active sheet, current selection, and sheet list.

## Available Tools (8)

### Reading

| Tool | Description |
|------|-------------|
| `excel_context` | Workbook name, active sheet, current selection address, row/col counts, sheet list |
| `excel_read` | Cell values as 2D array. Omit `range` to read current selection. Omit `sheet` for active sheet |
| `excel_tables` | All Excel Tables (ListObjects) with name, sheet, range, row count, and column headers |
| `excel_read_table` | A specific Table as array of records (`{header: value}` per row) |

### Writing

| Tool | Description |
|------|-------------|
| `excel_write` | Write a 2D array of values to a range. Auto-resizes to fit. Max 50,000 cells per call |
| `excel_write_formula` | Write formulas (use English names: SUM, AVERAGE, IF, VLOOKUP). Excel translates automatically |

### Formatting & Management

| Tool | Description |
|------|-------------|
| `excel_format` | Bold, italic, font size, font/background color (hex or named), number format, alignment, borders |
| `excel_create_sheet` | Create a new worksheet (added after the last sheet) |

## Tool Details

### `excel_context`

No parameters. Returns:
```json
{
  "workbook": "Budget.xlsx",
  "activeSheet": "Sheet1",
  "selection": "$B$2:$D$10",
  "rows": 9,
  "cols": 3,
  "sheets": ["Sheet1", "Summary", "Charts"]
}
```

### `excel_read`

| Parameter | Required | Description |
|-----------|----------|-------------|
| `sheet` | No | Sheet name. Defaults to active sheet |
| `range` | No | A1 notation, e.g. `A1:D10`. Defaults to current selection |

### `excel_write`

| Parameter | Required | Description |
|-----------|----------|-------------|
| `range` | Yes | Target start cell, e.g. `A1` |
| `values` | Yes | 2D array: `[["Name","Age"],["Alice",30]]` |
| `sheet` | No | Target sheet. Defaults to active sheet |

### `excel_write_formula`

| Parameter | Required | Description |
|-----------|----------|-------------|
| `range` | Yes | Target start cell |
| `formulas` | Yes | 2D array of formula strings: `[["=SUM(A1:A10)"],["=AVERAGE(B1:B10)"]]` |
| `sheet` | No | Target sheet |

### `excel_read_table`

| Parameter | Required | Description |
|-----------|----------|-------------|
| `name` | Yes | Table name (from `excel_tables`) |

Returns:
```json
{
  "table": "Budget",
  "headers": ["Category", "Q1", "Q2"],
  "rows": 5,
  "records": [
    {"Category": "Salary", "Q1": 50000, "Q2": 52000},
    {"Category": "Rent", "Q1": 12000, "Q2": 12000}
  ]
}
```

### `excel_format`

| Parameter | Required | Description |
|-----------|----------|-------------|
| `range` | Yes | Target range |
| `bold` | No | `true` / `false` |
| `italic` | No | `true` / `false` |
| `fontSize` | No | Points, e.g. `14` |
| `fontColor` | No | `#FF0000` or named: `red`, `blue`, `green`, `white`, `black`, `orange`, `purple`, `gray` |
| `bgColor` | No | Same as fontColor |
| `numberFormat` | No | Excel format string: `#,##0.00`, `0%`, `dd/mm/yyyy` |
| `horizontalAlignment` | No | `left`, `center`, `right` |
| `borders` | No | `true` to add thin borders |
| `sheet` | No | Target sheet |

### `excel_create_sheet`

| Parameter | Required | Description |
|-----------|----------|-------------|
| `name` | Yes | Name for the new sheet |

## How It Works Internally

### STA Thread Marshalling

Excel's COM object model is single-threaded apartment (STA). The Named Pipe server runs on thread-pool threads. Every COM access is marshalled to Excel's main thread via `ExcelAsyncUtil.QueueAsMacro` with a `TaskCompletionSource` for async await. A 30-second timeout prevents hanging if Excel is busy (cell edit mode, modal dialog, long calculation).

### COM Object Lifecycle

Every COM reference obtained via the `.` accessor on a COM object (e.g. `workbook.Worksheets`, `range.Rows`) creates a Runtime Callable Wrapper (RCW) that must be explicitly released via `Marshal.ReleaseComObject`. Failure to release causes Excel to stay alive as a zombie process after closing.

The `ExcelGateway` class enforces this with `try/finally` blocks on every COM access. The one exception: `ExcelDnaUtil.Application` is never released — Excel-DNA manages its lifetime.

### Named Pipe Protocol

Wire format: `[4-byte little-endian length][UTF-8 JSON body]`

Request: `{"method": "excel_read", "params": {"range": "A1:C5"}}`
Response: `{"result": {"values": [...]}}` or `{"error": "message"}`

One connection per tool call (connect, send, receive, disconnect). The add-in accepts one client at a time via `NamedPipeServerStream` with `maxNumberOfServerInstances: 1`.

### Excel-DNA and .NET 9

The `.xll` native host bootstrapper is compiled against .NET 6. To use .NET 9:

1. Set `RuntimeVersion="v9"` in the `.dna` file — this tells the native host to request .NET 9 from `hostfxr`
2. Set `<ExcelAddInCustomRuntimeConfiguration>true</ExcelAddInCustomRuntimeConfiguration>` in the `.csproj` — this prevents ExcelDna from generating a .NET 6 runtimeconfig
3. Set `<UseWindowsForms>true</UseWindowsForms>` — adds `Microsoft.WindowsDesktop.App` framework to the runtimeconfig (the ExcelDna host needs the Desktop Runtime)
4. Set `<RollForward>LatestMajor</RollForward>` — allows framework version roll-forward

## Using with N.E.O. Preview Server

When both MCP servers are configured (`neo-preview` and `neo-excel`), Claude can orchestrate them together:

1. `excel_read` — Claude sees your spreadsheet data
2. `compile_and_preview` (neo-preview) — Claude builds a native Avalonia form
3. User interacts with the form
4. `read_data` (neo-preview) — Claude reads form state
5. `excel_write` — Claude writes results back to Excel

Example prompt:
> "Read my budget table in Excel, build an Avalonia data-entry form for new rows, and when I fill it out, write the data back to Excel."

## Project Structure

```
Neo.ExcelMcp/
  Neo.ExcelMcp.AddIn/
    AddIn.cs                  — IExcelAddIn lifecycle (start/stop pipe server)
    PipeServer.cs             — Named Pipe JSON-RPC server (accept loop, message routing)
    ExcelGateway.cs           — All Excel COM access (STA marshalling, COM release)
    Log.cs                    — File logger (%LOCALAPPDATA%\NeoExcelMcp\addin.log)
    Neo.ExcelMcp.AddIn.csproj — .NET 9, x64, ExcelDna.AddIn 1.9.0
    Neo.ExcelMcp.AddIn.dna    — Excel-DNA config (RuntimeVersion="v9")
    exceldna.runtimeconfig.json — Custom runtime config for .NET 9 Desktop

  Neo.ExcelMcp.Bridge/
    Program.cs                — MCP STDIO host (same pattern as Neo.McpServer)
    ExcelTools.cs             — MCP tool definitions (8 tools)
    PipeClient.cs             — Named Pipe client (connect-per-call)
    Neo.ExcelMcp.Bridge.csproj — ModelContextProtocol SDK 1.1.0
```

## Logging

The add-in writes to `%LOCALAPPDATA%\NeoExcelMcp\addin.log`. Tail it live:

```powershell
Get-Content "$env:LOCALAPPDATA\NeoExcelMcp\addin.log" -Wait -Tail 30
```

The bridge logs to stderr (visible in Claude Code's MCP server logs or `%APPDATA%\Claude\logs\mcp-server-neo-excel.log` for Claude Desktop).

## Troubleshooting

**".NET Desktop Runtime 6.0.2" error when loading the add-in:**
The `.dna` file must contain `RuntimeVersion="v9"`. Without it, the ExcelDna host defaults to .NET 6.0.2. Rebuild after changing the `.dna` file.

**"Could not connect to pipe" when calling tools:**
Excel isn't running or the add-in isn't loaded. Check `addin.log` for `PipeServer started` messages. If missing, the add-in's `AutoOpen` didn't run — verify the add-in is checked in Excel's Add-ins dialog.

**Tool calls time out after 30 seconds:**
Excel is busy — the user is editing a cell (F2 mode), a modal dialog is open, or a long recalculation is running. Finish the current operation and retry.

**Excel zombie process after closing:**
A COM object wasn't released. Check `ExcelGateway` for any `dynamic` variable obtained from a COM property that isn't released in a `finally` block. Never release `ExcelDnaUtil.Application`.

**Add-in loads but no tools appear in Claude:**
The bridge must be configured in Claude's MCP settings AND Claude must be restarted. New tools are only discovered at session start.

**Build fails with locked .xll:**
Deactivate the add-in in Excel (uncheck in Add-ins dialog), rebuild, then reactivate.

## Comparison with Other Excel MCP Servers

| Feature | Neo for Excel | File-based servers |
|---------|--------------|-------------------|
| **Architecture** | In-process COM add-in | Read/write .xlsx files |
| **Live workbook access** | Yes — sees active selection | No — works on saved files only |
| **Latency** | <50ms (direct COM) | 100ms+ (file I/O) |
| **Requires Excel** | Yes (Windows, x64) | No |
| **N.E.O. integration** | Yes — Claude uses both MCP servers | No |
| **Cross-platform** | Windows only | Some support Linux/macOS |
