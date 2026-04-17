# Troubleshooting

## "No AI service configured"

**Cause:** No API keys set and no local model server detected.

**Fix:**
1. Set at least one API key as a Windows user environment variable:
   ```powershell
   [Environment]::SetEnvironmentVariable("ANTHROPIC_API_KEY", "your-key", "User")
   ```
2. Or start a local server (Ollama or LM Studio)
3. Restart N.E.O.

## Build Fails with "net9.0 targeting pack not found"

**Cause:** You have a different .NET SDK version installed.

**Fix:** Open `Directory.Build.props` in the solution root and change `NeoNetMajor` to match your installed SDK:

```xml
<NeoNetMajor>10</NeoNetMajor>  <!-- Change to your .NET version -->
```

## Compilation Errors in Generated Code

**Cause:** The AI generated code that doesn't compile.

**What happens:** N.E.O. automatically retries up to 5 times (configurable in Settings), feeding the compiler errors back to the AI.

**If it keeps failing:**
- Try rephrasing your prompt
- Switch to a different AI provider (Ctrl+1)
- Open the Code Editor (Ctrl+Shift+C) and fix the issue manually

## Preview Panel is Empty

**Cause:** The child process may have crashed or failed to start.

**Fix:**
1. Click **Clear** to reset the session
2. Try a simple prompt like "Create a button that says Hello"
3. If the problem persists, check that no antivirus is blocking the child process

## Hotkey Ctrl+Shift+F Not Working (WPF Host Only)

**Cause:** Another application has registered the same global hotkey. This only affects the WPF host which uses a Windows global hotkey.

**Fix:** Close the conflicting application or use the toolbar/Ctrl+2 to switch view modes instead.

**Note:** The Avalonia host uses F11 for main window fullscreen and Ctrl+Shift+F for Live Preview fullscreen — these do not use global hotkeys and should always work.

## Python Mode: "Python runtime not found"

**Cause:** The embedded Python 3.11 runtime hasn't been downloaded yet.

**Fix:** Enable Python in Settings — N.E.O. will automatically download the runtime on first use. Requires an internet connection.

## Export: Missing DLLs

**Cause:** Some NuGet packages may not have been resolved.

**Fix:**
1. Make sure the app compiles and runs correctly in N.E.O. first
2. Try the export again
3. Check the output directory for error messages

## Sandbox Mode: App Doesn't Work

**Cause:** The generated app needs resources that are blocked by the sandbox.

**Fix:**
1. Enable **Internet Access** if the app makes network requests
2. Enable **Folder Access** if the app reads/writes files
3. Or disable Sandbox Mode for development and re-enable for testing

## Settings Not Saving

**Cause:** The settings file may be corrupted or the directory missing.

**Fix:** Delete the settings file and restart:
```
%LOCALAPPDATA%\Neo\settings.json
```

N.E.O. will create a fresh settings file with defaults.

## Web App — browser shows a blank page

**Cause:** The backend is running but the WASM bundle hasn't been published,
or an old bundle is cached.

**Fix:**

1. Publish the client bundle once:
   ```bash
   dotnet publish Neo.App.WebApp/Neo.App.WebApp.Browser/Neo.App.WebApp.Browser.csproj \
       -c Release -o Neo.App.WebApp/Neo.App.WebApp.Browser/bin/Release/net9.0-browser/publish
   ```
2. Rebuild the backend (`dotnet build Neo.Backend -c Release`) so its
   `wwwroot` picks up the fresh bundle.
3. Hard-reload the browser (**Ctrl+Shift+R**). DevTools → Network →
   "Disable cache" helps if it persists.

See [[Web App]] for the full workflow.

## Web App — "Load failed: Exception has been thrown by the target of an invocation"

**Cause:** The browser is still running a stale compiled DLL from the WASM
cache. The current error handler unwraps `TargetInvocationException` and
shows the real inner cause — seeing only the outer message means old code.

**Fix:** Hard-reload with cache disabled (Ctrl+Shift+R in DevTools with the
Network "Disable cache" checkbox active). If the issue persists, clear the
browser's site data for `localhost:5099`.

## Web App — "NuGet resolve failed (404)"

**Cause:** You're running an old backend that doesn't have the
`/api/nuget/resolve` endpoint.

**Fix:** Kill any lingering backend process and rebuild:

```bash
# Windows
taskkill /F /IM Neo.Backend.exe
# then
dotnet build Neo.Backend -c Release
dotnet run   --project Neo.Backend -c Release --urls=http://localhost:5099
```

## Web App — first NuGet resolve feels like it's hanging

**Cause:** First-time download of transitive NuGet dependencies can take
5–30 seconds per package. The status line shows *"Resolving NuGet: …"* while
this happens.

**Fix:** It's not hung, just slow on the cold path. Subsequent resolves with
the same packages are ~1 second because the backend caches them at
`%LocalAppData%/Neo.Backend/nuget-cache/<tfm>/`.

## Web App — bundle seems too large (~27 MB brotli)

**Cause:** Trimming is intentionally disabled
(`<PublishTrimmed>false</PublishTrimmed>`) because `System.Text.Json` needs
reflection for DTO deserialization.

**Known follow-up:** introduce a `JsonSerializerContext` for every wire-format
type, then re-enable trimming. Target size: ~9 MB brotli. Not done yet.

## Still having issues?

Open an issue on [GitHub](https://github.com/hf75/N.E.O/issues) with:
- Your .NET SDK version (`dotnet --version`)
- Your Windows version
- The exact error message or screenshot
- Steps to reproduce
