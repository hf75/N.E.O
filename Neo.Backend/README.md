# Neo.Backend

Minimal local-first ASP.NET Core helper for Neo.WebApp. Its only jobs are:

1. **Serve the Avalonia.Browser WASM bundle** out of `wwwroot/`.
2. **Proxy AI streaming requests** (Claude / OpenAI / Gemini / Ollama / LM
   Studio) — necessary because browsers can't call `api.anthropic.com` directly
   (CORS), and because it keeps API keys off the client.
3. **Proxy NuGet** so the browser can pull .dll bytes from inside a `.nupkg`
   without running a NuGet client.

No auth, no DB, no rate limiting. Every developer runs their own copy locally.
Deployed as part of the OSS repo — check out, `dotnet run`, open the browser.

## Endpoints

| Method | Path | Returns |
|---|---|---|
| GET  | `/api/health`                    | `{status, time}` |
| GET  | `/api/providers`                 | Array describing which providers have env vars set |
| POST | `/api/ai/{providerId}/stream`    | SSE stream; `event: meta`/`data`/`done`/`error` |
| POST | `/api/nuget/resolve`             | ZIP of DLLs (transitive-deps resolved, TFM-matched) |

The NuGet endpoint delegates to the same `NuGetPackageService` that
Neo.App.Core and Neo.McpServer use — full transitive-dependency resolution
via NuGet.Protocol 7.0.0, not a hand-rolled proxy.

### Body shape for `/api/ai/{provider}/stream`

```json
{
  "prompt": "…",          // required
  "model": null,          // optional; defaults per-provider (e.g. "claude-opus-4-7")
  "maxTokens": 2048,      // optional
  "systemPrompt": null    // optional
}
```

Response is SSE. Each `data:` line contains the provider's native event payload
(Claude's `content_block_delta`, OpenAI's `choices[0].delta.content`, Gemini's
`candidates[0].content.parts[0].text`). A `meta` event reports server-side
TTFB. `done` reports total time and event count. `error` wraps any upstream
failure.

## Env vars

Set the same ones the desktop Neo reads:

| Provider | Env var |
|---|---|
| Anthropic Claude | `ANTHROPIC_API_KEY` |
| OpenAI ChatGPT | `OPENAI_API_KEY` |
| Google Gemini | `GEMINI_API_KEY` |
| Ollama (local) | `OLLAMA_HOST` (e.g. `http://localhost:11434`) |
| LM Studio (local) | `LMSTUDIO_HOST` (e.g. `http://localhost:1234`) |

Providers without env vars set are reported `available: false` by `/api/providers`
so the UI can grey them out.

## Static bundle integration

The `.csproj` has an `AfterTargets="Build"` target that copies
`../Neo.App.WebApp/Neo.App.WebApp.Browser/bin/$(Config)/net9.0-browser/publish/wwwroot/**`
into `bin/$(Config)/net9.0/wwwroot/`. `Program.cs` points `WebRootPath` at
`AppContext.BaseDirectory/wwwroot` so the same copy is found under `dotnet run`
as under `dotnet publish`.

First-time setup:

```bash
# Publish the WASM client once
dotnet publish ../Neo.App.WebApp/Neo.App.WebApp.Browser/Neo.App.WebApp.Browser.csproj \
  -c Release -o ../Neo.App.WebApp/Neo.App.WebApp.Browser/bin/Release/net9.0-browser/publish

# Then
dotnet run -c Release --urls=http://localhost:5099
```

Open http://localhost:5099 .

## Tests

```bash
dotnet test ../Neo.Backend.Tests/Neo.Backend.Tests.csproj
```

12 tests covering the `ProviderRegistry` abstraction and the error paths of
`/api/ai/{provider}/stream` (`WebApplicationFactory`-based integration tests).
Live streaming tests aren't part of CI — they'd need a real API key.
