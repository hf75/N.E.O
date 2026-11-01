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

## Build Fails with "net10.0 targeting pack not found"

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

**Avalonia apps written for an older N.E.O.:** N.E.O. ships Avalonia 12. Code that was generated for Avalonia 11 can use APIs that no longer exist (`CheckBox.Checked`, `OpenFileDialog`, `TextBox.Watermark`, `SystemDecorations`, …). The old package versions stored in a project are moved to the shipped version automatically; for the code, just ask the AI to fix the compiler errors — its prompt lists the changed APIs.

## The AI says it added a feature, but the app looks the same

**Cause:** The AI wrote the new code and forgot to use it, for example a method that draws a chart, with no line that calls it. It compiles, and hot reload reports success.

**What happens:** When a patch adds a private method nothing refers to, N.E.O. sends it back to the AI once to finish it. That takes a few seconds longer, and you only see the result.

**If it still happens:** Say so in the next prompt ("the chart does not show"). The check only covers private methods in patches: a new class nothing creates, or a public method, gets through.

## Web view in a generated app stays empty

**Cause:** The built-in `NativeWebView` renders with the platform browser. On Windows that is the WebView2 runtime — part of Windows 11, optional on Windows 10. On Linux it needs WebKitGTK (`libwebkit2gtk-4.1`) or WPE WebKit.

**Fix:** Install the [WebView2 runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (Windows 10) or the WebKitGTK packages of your distribution. The browser profile of preview windows lives in `%LOCALAPPDATA%\Neo\WebView2`; delete that folder to reset cookies and cache.

## The app showed a red "repairing" banner

The app threw an exception while you used it. It is not restarted: N.E.O. fixes the code and patches it into the running app, then does the failed click again (see [[Hot Reload]], *Heal in place*). Wait for the green banner. If the fix could not be patched in, the app is restarted with the fix as before — your data is gone in that case, the chat says which edit forced the restart.

## The app lost its state after a change

Normally the next prompt patches the running app ([[Hot Reload]]). When that is not possible, the chat says `Hot reload not possible — <reason>` and the app is restarted the old way. Typical reasons: a change Roslyn cannot patch into running code (the message names the `ENC…` rule), a NuGet package switched to another version (newly added packages are patched in), a constructor that was rewritten, or UI code in an app without a re-runnable UI method (`BuildUi()`). Ask the AI to keep the UI in `BuildUi()` and the data in fields — the next changes will then keep the state.

## Preview Panel is Empty

**Cause:** The child process may have crashed or failed to start.

**Fix:**
1. Click **Clear** to reset the session
2. Try a simple prompt like "Create a button that says Hello"
3. If the problem persists, check that no antivirus is blocking the child process

## Hotkey Ctrl+Shift+F Not Working (WPF Host Only)

**Cause:** Another application, or a second N.E.O., has registered the same global hotkey. This only affects the WPF host which uses a Windows global hotkey. N.E.O. says so in the chat when it starts.

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
       -c Release -o Neo.App.WebApp/Neo.App.WebApp.Browser/bin/Release/net10.0-browser/publish
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
