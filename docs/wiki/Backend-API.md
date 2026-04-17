# Backend API

`Neo.Backend` is the local ASP.NET Core helper that powers the [[Web App]]. This page documents its HTTP surface so you can build alternative clients, automate Neo, or debug issues.

Everything runs on `localhost`. There is **no auth, no rate limiting, no persistence beyond a NuGet cache**. Every developer runs their own instance.

## Start it

```bash
dotnet run --project Neo.Backend -c Release --urls=http://localhost:5099
```

Startup prints which AI providers have an env var set:

```
Neo.Backend — provider status:
  claude     available
  openai     available
  gemini     missing env var
  ollama     missing env var
  lmstudio   missing env var
```

## Endpoints

| Method | Path | Returns |
|---|---|---|
| `GET`  | `/api/health`                 | `{status, time}` |
| `GET`  | `/api/providers`              | Array describing which providers have env vars set |
| `POST` | `/api/ai/{providerId}/stream` | SSE stream with Anthropic/OpenAI/Gemini-shaped `data:` events |
| `POST` | `/api/nuget/resolve`          | `application/zip` of DLLs (transitive-deps resolved, TFM-matched) |
| `GET`  | `/`, `/<static>`              | Serves the `wwwroot/` bundle (the Web App) |

### GET `/api/health`

```json
{"status":"ok","time":"2026-04-17T10:58:03.1234567Z"}
```

Useful as a liveness probe and to confirm the backend is reachable before hammering the other endpoints.

### GET `/api/providers`

```json
[
  {"id":"claude",  "name":"Anthropic Claude","envVar":"ANTHROPIC_API_KEY","available":true, "defaultModel":"claude-opus-4-7"},
  {"id":"openai",  "name":"OpenAI ChatGPT", "envVar":"OPENAI_API_KEY",   "available":true, "defaultModel":"gpt-4o-mini"},
  {"id":"gemini",  "name":"Google Gemini",  "envVar":"GEMINI_API_KEY",   "available":false,"defaultModel":"gemini-1.5-flash"},
  {"id":"ollama",  "name":"Ollama (local)", "envVar":"OLLAMA_HOST",      "available":false,"defaultModel":"llama3.1:latest"},
  {"id":"lmstudio","name":"LM Studio",      "envVar":"LMSTUDIO_HOST",    "available":false,"defaultModel":"local-model"}
]
```

`available` is computed at request time, so newly-set env vars show up without a restart — except for children the backend spawns, whose env is captured at launch.

### POST `/api/ai/{providerId}/stream`

**Body**

```json
{
  "prompt": "…required…",
  "model": null,
  "maxTokens": 2048,
  "systemPrompt": null,
  "history": [
    {"role": "user",      "content": "previous prompt"},
    {"role": "assistant", "content": "{…previous JSON reply with code…}"}
  ]
}
```

- `model` — optional. Defaults per provider (see `/api/providers`).
- `maxTokens` — optional.
- `systemPrompt` — optional. Not merged with `history`; sent as a `system` message (Claude / OpenAI) or prepended to `contents` (Gemini).
- `history` — optional. Role is `"user"` or `"assistant"`. For Gemini, `"assistant"` is translated to `"model"`. Include all prior turns so the model sees its own code back on iterative prompts.

**Response** — `text/event-stream`. Neo emits three own-event types around the provider's native `data:` lines:

```
event: meta
data: {"ttfb_ms":312}

data: {"type":"content_block_delta","delta":{"text":"Hello"}}
data: {"type":"content_block_delta","delta":{"text":", world"}}

event: done
data: {"total_ms":1408,"events":42}
```

Anthropic-, OpenAI- and Gemini-shaped `data:` lines are relayed **verbatim** — the client is responsible for extracting text deltas from each provider's native format. Ollama and LM Studio use OpenAI's shape via their OpenAI-compatible endpoints.

**Error responses**

| Status | When |
|---|---|
| 400 | `prompt` empty or missing, body not JSON |
| 404 | `providerId` not one of `claude`/`openai`/`gemini`/`ollama`/`lmstudio` |
| 503 | Provider's env var is not set |
| 200 + `event: error` | Upstream API returned non-2xx; body contains the upstream error |

### POST `/api/nuget/resolve`

**Body**

```json
{
  "packages": {
    "MathNet.Numerics": "5.0.0",
    "NodaTime": "default"
  },
  "targetFramework": "net9.0"
}
```

- `packages` — map of `{id → version}`. `"default"` selects the latest stable from the NuGet API. `"[3.0,)"` and similar ranges are honored.
- `targetFramework` — optional. Defaults to `net9.0`. Used by the resolver for `lib/{tfm}` selection.

**Response** — `application/zip`. One flat entry per resolved DLL. Transitive dependencies are included automatically — the backend uses the same `NuGetPackageService` that Neo.App.Core and Neo.McpServer use (NuGet.Protocol 7.0.0).

Response headers:

| Header | Value |
|---|---|
| `Content-Type` | `application/zip` |
| `Content-Length` | Total ZIP size |
| `X-Neo-Package-Count` | Count of resolved packages (roots + transitive) |
| `X-Neo-Dll-Count` | Count of DLL entries in the ZIP |

**Cache** — persistent at `%LocalAppData%/Neo.Backend/nuget-cache/<tfm>/` on Windows, `~/.local/share/Neo.Backend/nuget-cache/<tfm>/` on Linux. First resolve of a given package: 5–15 s (cold download). Same packages later: ~1 s. Delete the cache directory to force re-download.

**Error responses**

| Status | When |
|---|---|
| 400 | `packages` empty or missing, body not JSON |
| 500 | Upstream NuGet failure (rare) |

## Environment variables

| Variable | Purpose |
|---|---|
| `ANTHROPIC_API_KEY` | Unlocks `/api/ai/claude/stream` |
| `OPENAI_API_KEY` | Unlocks `/api/ai/openai/stream` |
| `GEMINI_API_KEY` | Unlocks `/api/ai/gemini/stream` |
| `OLLAMA_HOST` | URL of a running Ollama server (e.g. `http://localhost:11434`) |
| `LMSTUDIO_HOST` | URL of a running LM Studio server (e.g. `http://localhost:1234`) |

## Static serving

The project's build target copies the published Avalonia.Browser bundle from
`Neo.App.WebApp/Neo.App.WebApp.Browser/bin/<config>/net9.0-browser/publish/wwwroot/`
into `Neo.Backend/bin/<config>/net9.0/wwwroot/` after every backend build. `Program.cs`
points `WebRootPath` at `AppContext.BaseDirectory/wwwroot` so `dotnet run` and `dotnet publish` behave the same.

Unknown MIME types (`.dll`, `.wasm`, `.blat`, `.dat`, `.webcil`) are explicitly registered as `application/octet-stream`; `ServeUnknownFileTypes = true` covers anything new.

## Tests

```bash
dotnet test Neo.Backend.Tests/Neo.Backend.Tests.csproj
```

12 xUnit tests — `ProviderRegistry`, `/api/health`, `/api/providers`, and `/api/ai/*/stream` error paths. Live streaming isn't covered (it'd need a real API key). The NuGet endpoint isn't tested in CI because it downloads megabytes from nuget.org; exercise it manually with `curl` (see [[Web App]]).
