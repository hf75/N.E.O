# Neo.WebApp

**N.E.O. running entirely in your browser.** A local-first, open-source dev tool
that lets you describe an app in natural language and see it compiled, loaded,
and running — all inside a WASM sandbox, with no installation beyond .NET 9.

## What it is

A browser-hosted variant of Neo. Same idea as the desktop app (prompt → C# →
DLL → live UI), but the entire compile+load+mount loop runs inside the
browser's WebAssembly runtime. A minimal ASP.NET Core "backend" only acts as a
CORS-safe proxy to the AI providers and serves the static bundle.

Key design choices:

- **Local-only.** Every dev starts their own backend with `dotnet run`; there's
  no hosted service, no user accounts, no cloud state.
- **BYO keys.** API keys come from the same env vars the desktop app uses
  (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `GEMINI_API_KEY`, …). The backend
  reads them; the browser never sees them.
- **Client-side compile.** Roslyn runs inside the WASM runtime. Your prompt
  and generated code never leave the browser (only the AI round-trip does).
- **Collectible ALC.** Each compiled plugin lives in its own
  `AssemblyLoadContext`; unloading reclaims memory so long sessions don't grow
  the heap (validated at −0.7 % growth over 50 iterations in the POC).

## Layout

```
Neo.App.WebApp/
├── Neo.App.WebApp/          shared Avalonia library (Services + Views)
├── Neo.App.WebApp.Browser/  net9.0-browser WASM head (real target)
├── Neo.App.WebApp.Desktop/  net9.0 dev-iteration head (optional)
└── README.md                this file

Neo.Backend/                 ASP.NET Core helper — AI proxy + NuGet proxy + static server
Neo.Backend.Tests/           xUnit (12 tests)
Neo.App.WebApp.Tests/        xUnit (16 tests — parser, analyzer, session store)
```

## Running it end-to-end

Everything builds with the standard SDK (Avalonia template's xplat layout,
`Microsoft.NET.Sdk.WebAssembly`, etc.). Workload required: `wasm-tools` /
`wasm-tools-net9`.

```bash
# 1) Publish the browser client (~50 s first time, ~10 s afterwards)
cd Neo.App.WebApp
dotnet publish Neo.App.WebApp.Browser/Neo.App.WebApp.Browser.csproj -c Release \
  -o Neo.App.WebApp.Browser/bin/Release/net9.0-browser/publish

# 2) Build the backend (copies the wwwroot bundle into its own bin)
cd ..
dotnet build Neo.Backend/Neo.Backend.csproj -c Release

# 3) Run it
cd Neo.Backend
dotnet run -c Release --urls=http://localhost:5099
```

Then open http://localhost:5099 . First paint takes a couple seconds as the
WASM runtime (~9 MB brotli) loads. The UI shows a chat column on the left
and a live Preview / Code tab on the right.

### Trying it

- Pick a provider from the dropdown (must have the matching env var set).
- Type a prompt: *"A pomodoro timer with start/pause/reset and a circular progress ring."*
- **Ctrl+Enter** or **Generate & Run**.
- Watch the assistant's reply stream into the left chat pane live, then the
  generated app appears in the right-side Preview slot.
- **Iterate**: follow up with *"make the background dark blue"* — the AI sees
  the previous turn's code and patches just the requested part.
- **NuGet**: if the AI requests a package (`"nuget": [{"id":"NodaTime","version":"3.1.9"}]`),
  Neo.Backend runs the same `NuGetPackageService` the desktop Neo uses
  (transitive-dep resolution via `NuGet.Protocol`), zips every matched DLL,
  and streams it back. The client expands the ZIP into
  `MetadataReference`s and hands them to Roslyn.
- **Code tab**: edit the generated source directly and click **Recompile** to
  apply.
- **Monaco tab**: an HTML-overlay Monaco editor sits on top of the Avalonia
  canvas — full C#-flavoured editor with syntax highlighting. Click
  *Recompile (Monaco)* to apply.
- **Save / Load .neo**: session is persisted to IndexedDB AND downloaded as a
  JSON file. **Load…** opens the saved-sessions list (delete from there too).
- Click **New** to unload and start over.

## Bundle size

After `dotnet publish -c Release`:

| Variant | Total |
|---|---|
| Uncompressed | ~56 MB |
| **Brotli (what browsers fetch)** | **~9.2 MB** |
| Gzip (older browser fallback) | ~12.0 MB |

The biggest single payloads: `dotnet.native.wasm` (~8 MB),
`Microsoft.CodeAnalysis.CSharp.dll` (~5 MB — Roslyn itself),
`Avalonia.Fonts.Inter.dll` (~1.8 MB, easy to trim for a smaller bundle).

## Security

Generated C# is screened by `SecurityAnalyzer` before Roslyn ever compiles it:

| Rule | Blocked |
|---|---|
| `ForbiddenType`       | `System.IO.File`, `System.IO.Directory`, `System.Diagnostics.Process`, `System.Net.Sockets.Socket`, `System.Reflection.Emit.*`, `System.Runtime.InteropServices.Marshal` |
| `ForbiddenNamespace`  | `System.Windows.*` (WPF), `Microsoft.Win32.*` |
| `PInvoke`             | `DllImport`, `LibraryImport` |
| `Unsafe`              | `unsafe {…}` blocks |

The browser sandbox already blocks real filesystem / network / process access
at the runtime layer; the analyzer just gives a fast, meaningful error message
before the user ever waits for a compile.

Disabling it: `WasmCompiler.EnforceSecurityAnalyzer = false` (tests only).

## Known caveats

- **Bundle is untrimmed** (~27 MB brotli) because `System.Text.Json` needs
  reflection for DTO deserialization and trimming would strip the DTOs'
  constructors. Re-introducing `JsonSerializerContext` for all wire-format
  types is a follow-up that brings the bundle back down to ~9 MB.
- **Collectible ALC wrapper lingers** in Mono WASM even after full GC — a
  known runtime quirk. Heap stays flat though, so no real leak.
- **Monaco overlay position** is tracked via `Control.LayoutUpdated`. On some
  transform-heavy layouts this might lag a frame; if it does, wrap the Monaco
  tab in a simple `Grid` cell rather than nested splitters.

## Tests

```bash
dotnet test Neo.Backend.Tests/Neo.Backend.Tests.csproj
dotnet test Neo.App.WebApp.Tests/Neo.App.WebApp.Tests.csproj
```

Coverage:

- **Neo.Backend.Tests** (12): `ProviderRegistry`, `/api/health`,
  `/api/providers`, `/api/ai/{provider}/stream` error paths (404 for unknown
  provider, 503 for missing env var, 400 for invalid body).
- **Neo.App.WebApp.Tests** (22): `StructuredResponseParser` (plain/fenced/
  embedded JSON, escaped quotes, NuGet array, chat-only replies),
  `SecurityAnalyzer` (clean, File I/O, Process.Start, DllImport, unsafe, WPF
  namespace), `InMemorySessionStore` (round-trip, missing, idempotent save),
  `ChatEntry` INotifyPropertyChanged semantics.

End-to-end (browser-execution) testing isn't automated yet — covered by
manual smoke-testing against a running backend.
