using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Neo.IPC;
using Neo.McpServer.Services;

namespace Neo.McpServer.Tools;

[McpServerToolType]
public sealed class PreviewTools
{
    /// <summary>
    /// Compiles C# Avalonia UserControl source code and displays it as a live preview window.
    /// If no preview window is running, one is started automatically.
    /// </summary>
    [McpServerTool(Name = "compile_and_preview")]
    [Description("Compiles C# Avalonia UserControl code and shows it in a live preview window on the user's desktop. " +
        "The code must define a class 'DynamicUserControl' inheriting from Avalonia.Controls.UserControl. " +
        "Avalonia 11.3.12 packages are always included automatically.")]
    public static async Task<string> CompileAndPreview(
        CompilationPipeline compilation,
        PreviewSessionManager preview,
        [Description("Complete C# source code files. Each string is one .cs file. " +
            "The main file must contain a class 'DynamicUserControl : UserControl'.")] string[] sourceCode,
        [Description("NuGet packages as JSON object string, e.g. '{\"Humanizer\": \"default\", \"Bogus\": \"35.6.1\"}'. " +
            "Use 'default' for latest stable version. Avalonia packages are added automatically. " +
            "Omit or pass empty string if no extra packages needed.")] string? nugetPackages = null)
    {
        try
        {
            var packages = ParseNuGetPackages(nugetPackages);
            Console.Error.WriteLine($"[compile_and_preview] Called with {sourceCode.Length} source file(s), " +
                $"packages={string.Join(", ", packages.Select(kv => $"{kv.Key}={kv.Value}"))}");

            EnsureAvaloniaPackages(packages);

            // Compile
            var result = await compilation.CompileAsync(sourceCode, packages);
            if (!result.Success)
            {
                return $"COMPILATION FAILED:\n{string.Join("\n", result.Errors)}\n\n" +
                       "Fix the errors and try again.";
            }

            // Start preview if not running
            if (!preview.IsRunning)
            {
                var started = await preview.StartAsync();
                if (!started)
                {
                    return $"Compilation succeeded but preview window failed to start.\n" +
                           $"Logs: {string.Join("\n", preview.ChildLogs)}";
                }
            }

            // Load dependency DLLs as byte arrays
            var deps = new Dictionary<string, byte[]>();
            foreach (var dllPath in result.DependencyDllPaths)
            {
                if (File.Exists(dllPath))
                    deps[Path.GetFileName(dllPath)] = await File.ReadAllBytesAsync(dllPath);
            }

            // Send to preview
            var sent = await preview.SendDllAsync(
                result.DllBytes!,
                "DynamicUserControl.dll",
                deps);

            if (!sent)
            {
                return $"Compilation succeeded but failed to send DLL to preview window.\n" +
                       $"Logs: {string.Join("\n", preview.ChildLogs)}";
            }

            return $"SUCCESS: App compiled and displayed in live preview window.\n" +
                   $"DLL size: {result.DllBytes!.Length:N0} bytes, Dependencies: {deps.Count}";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[compile_and_preview] EXCEPTION: {ex}");
            return $"ERROR in compile_and_preview: {ex.GetType().Name}: {ex.Message}\n" +
                   $"Stack: {ex.StackTrace?.Split('\n').FirstOrDefault()}";
        }
    }

    /// <summary>
    /// Updates the live preview with new code. The preview window stays open.
    /// </summary>
    [McpServerTool(Name = "update_preview")]
    [Description("Updates the live preview window with modified C# code. " +
        "The existing preview window is reused — the user sees the change in-place.")]
    public static async Task<string> UpdatePreview(
        CompilationPipeline compilation,
        PreviewSessionManager preview,
        [Description("Updated C# source code files.")] string[] sourceCode,
        [Description("NuGet packages as JSON object string, e.g. '{\"Humanizer\": \"default\"}'. " +
            "Omit or pass empty string if no extra packages needed.")] string? nugetPackages = null)
    {
        try
        {
            if (!preview.IsRunning)
                return await CompileAndPreview(compilation, preview, sourceCode, nugetPackages);

            var packages = ParseNuGetPackages(nugetPackages);
            EnsureAvaloniaPackages(packages);

            var result = await compilation.CompileAsync(sourceCode, packages);
            if (!result.Success)
            {
                return $"COMPILATION FAILED:\n{string.Join("\n", result.Errors)}\n\n" +
                       "Fix the errors and try again. The previous preview is still showing.";
            }

            var deps = new Dictionary<string, byte[]>();
            foreach (var dllPath in result.DependencyDllPaths)
            {
                if (File.Exists(dllPath))
                    deps[Path.GetFileName(dllPath)] = await File.ReadAllBytesAsync(dllPath);
            }

            var updated = await preview.UpdateAsync(
                result.DllBytes!,
                "DynamicUserControl.dll",
                deps);

            if (!updated)
            {
                return $"Compilation succeeded but hot-reload failed.\n" +
                       $"Logs: {string.Join("\n", preview.ChildLogs)}";
            }

            return $"SUCCESS: Preview updated live.\n" +
                   $"DLL size: {result.DllBytes!.Length:N0} bytes, Dependencies: {deps.Count}";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update_preview] EXCEPTION: {ex}");
            return $"ERROR in update_preview: {ex.GetType().Name}: {ex.Message}\n" +
                   $"Stack: {ex.StackTrace?.Split('\n').FirstOrDefault()}";
        }
    }

    /// <summary>
    /// Closes the live preview window.
    /// </summary>
    [McpServerTool(Name = "close_preview")]
    [Description("Closes the live preview window.")]
    public static async Task<string> ClosePreview(PreviewSessionManager preview)
    {
        if (!preview.IsRunning)
            return "No preview window is currently running.";

        try
        {
            await preview.StopAsync();
            return "Preview window closed.";
        }
        catch (Exception ex)
        {
            return $"Preview window closed (with warnings: {ex.Message}).";
        }
    }

    /// <summary>
    /// Returns information about the N.E.O. preview system capabilities.
    /// </summary>
    [McpServerTool(Name = "get_preview_status")]
    [Description("Returns the current status of the preview system: whether a window is running, " +
        "child process logs, and available Avalonia version.")]
    public static string GetPreviewStatus(PreviewSessionManager preview)
    {
        var status = preview.IsRunning ? "RUNNING" : "STOPPED";
        var logs = preview.ChildLogs.Count > 0
            ? string.Join("\n", preview.ChildLogs.TakeLast(20))
            : "(no logs)";

        var errorsSection = preview.RuntimeErrors.Count > 0
            ? $"\nRuntime Errors ({preview.RuntimeErrors.Count}):\n{string.Join("\n", preview.RuntimeErrors.TakeLast(10))}"
            : "\nRuntime Errors: none";

        return $"Preview Status: {status}\n" +
               $"Avalonia Version: 11.3.12\n" +
               $"Framework: .NET 9\n" +
               $"Recent Logs:\n{logs}{errorsSection}";
    }

    /// <summary>
    /// Captures a screenshot of the live preview window. Returns a Base64-encoded PNG image.
    /// Claude can use this to SEE what the app looks like and suggest visual improvements.
    /// </summary>
    [McpServerTool(Name = "capture_screenshot")]
    [Description("Captures a screenshot of the running preview window and returns it as a PNG image. " +
        "Use this to SEE what the generated app looks like and suggest visual improvements. " +
        "The preview must be running (call compile_and_preview first).")]
    public static async Task<IEnumerable<ContentBlock>> CaptureScreenshot(PreviewSessionManager preview)
    {
        if (!preview.IsRunning)
            return [new TextContentBlock { Text = "No preview window is running. Call compile_and_preview first." }];

        var result = await preview.CaptureScreenshotAsync();
        if (result == null)
            return [new TextContentBlock { Text = "Screenshot capture failed. The preview window may not be visible." }];

        var pngBytes = Convert.FromBase64String(result.Base64Png);

        return [
            ImageContentBlock.FromBytes(pngBytes, "image/png"),
            new TextContentBlock
            {
                Text = $"Screenshot captured: {result.Width}x{result.Height} pixels."
            }
        ];
    }

    /// <summary>
    /// Returns runtime errors from the running app. Claude can use these to auto-fix code.
    /// </summary>
    [McpServerTool(Name = "get_runtime_errors")]
    [Description("Returns runtime errors thrown by the generated app since the last compile_and_preview. " +
        "Use this to detect crashes and auto-fix the code. Returns empty if no errors occurred.")]
    public static string GetRuntimeErrors(PreviewSessionManager preview)
    {
        if (!preview.IsRunning)
            return "No preview is running.";

        if (preview.RuntimeErrors.Count == 0)
            return "No runtime errors. The app is running cleanly.";

        return $"RUNTIME ERRORS ({preview.RuntimeErrors.Count}):\n\n" +
               string.Join("\n---\n", preview.RuntimeErrors) +
               "\n\nFix the code and call update_preview to hot-reload.";
    }

    /// <summary>
    /// Inspects the visual tree of the running app and returns a structured JSON representation.
    /// </summary>
    [McpServerTool(Name = "inspect_visual_tree")]
    [Description("Returns the complete visual tree of the running app as JSON. " +
        "Shows all controls, their types, names, key properties (text, colors, font sizes, " +
        "enabled state, item counts), bounds, and child hierarchy. " +
        "Use this to understand the UI structure, diagnose layout issues, find controls for " +
        "set_property, or verify changes. Much more precise than a screenshot.")]
    public static async Task<string> InspectVisualTree(PreviewSessionManager preview)
    {
        if (!preview.IsRunning)
            return "No preview is running. Call compile_and_preview first.";

        var json = await preview.InspectVisualTreeAsync();
        if (json == null)
            return "Failed to inspect visual tree. The preview window may not be visible.";

        return json;
    }

    /// <summary>
    /// Modifies a property on a running control without recompilation.
    /// Instant change, preserves app state (scroll position, user input, timers).
    /// </summary>
    [McpServerTool(Name = "set_property")]
    [Description("Changes a property on a control in the running app WITHOUT recompilation. " +
        "Instant change that preserves app state (scroll positions, user input, timers). " +
        "Use for visual tweaks like colors, font sizes, text, margins, visibility. " +
        "For structural changes (adding controls, changing logic), use update_preview instead.")]
    public static async Task<string> SetProperty(
        PreviewSessionManager preview,
        [Description("Target control. Can be: a Name (e.g. 'myButton'), " +
            "a type name (e.g. 'TextBlock' for first match), " +
            "or type:index (e.g. 'TextBlock:2' for third TextBlock).")] string target,
        [Description("Property name, e.g. 'Foreground', 'FontSize', 'Text', 'IsVisible', 'Opacity', " +
            "'Background', 'Margin', 'FontWeight', 'Width', 'Height'.")] string propertyName,
        [Description("New value as string. Examples: 'Red', '#FF5500', '24', 'Hello World', 'true', " +
            "'10,5,10,5' (for Thickness/Margin), 'Bold' (for FontWeight).")] string value)
    {
        if (!preview.IsRunning)
            return "No preview is running. Call compile_and_preview first.";

        var request = new SetPropertyRequest(target, propertyName, value);
        var result = await preview.SetPropertyAsync(request);

        if (result == null)
            return "SetProperty failed: no response from preview window.";

        if (!result.Success)
            return $"SetProperty FAILED: {result.Message}";

        return $"OK: {result.Message}" +
               (result.OldValue != null ? $"\n  Old: {result.OldValue}" : "") +
               (result.NewValue != null ? $"\n  New: {result.NewValue}" : "");
    }

    /// <summary>
    /// Exports the current app as a standalone executable that can be shared and run without N.E.O.
    /// </summary>
    [McpServerTool(Name = "export_app")]
    [Description("Exports the generated app as a standalone executable. " +
        "The exported app runs independently — no N.E.O., no MCP server, no .NET SDK needed (only the .NET runtime). " +
        "All dependencies (Avalonia, NuGet packages) are included in the export directory.")]
    public static async Task<string> ExportApp(
        CompilationPipeline compilation,
        [Description("Complete C# source code files (same as compile_and_preview).")] string[] sourceCode,
        [Description("Name for the exported application (used as folder name and window title).")] string appName,
        [Description("Absolute path to the directory where the app should be exported to, " +
            "e.g. 'C:/Users/heiko/Desktop' or 'C:/tmp'. Must be a full absolute path. " +
            "A subfolder with the app name will be created.")] string exportPath,
        [Description("Target platform: 'windows', 'linux', or 'osx'. Defaults to 'windows'.")] string platform = "windows",
        [Description("NuGet packages as JSON object string, e.g. '{\"Humanizer\": \"default\"}'. " +
            "Omit if no extra packages needed.")] string? nugetPackages = null)
    {
        try
        {
            // Validate inputs
            if (string.IsNullOrWhiteSpace(appName))
                return "EXPORT FAILED: appName cannot be empty.";
            if (string.IsNullOrWhiteSpace(exportPath))
                return "EXPORT FAILED: exportPath cannot be empty. Use an absolute path like 'C:/tmp'.";
            if (!Path.IsPathRooted(exportPath))
                return $"EXPORT FAILED: exportPath must be an absolute path. Got: '{exportPath}'. Use e.g. 'C:/tmp'.";

            Console.Error.WriteLine($"[export_app] Exporting '{appName}' to {exportPath} for {platform}...");

            var packages = ParseNuGetPackages(nugetPackages);

            var result = await compilation.ExportAsync(
                sourceCode, appName, exportPath, platform, packages);

            if (!result.Success)
            {
                return $"EXPORT FAILED:\n{string.Join("\n", result.Errors)}";
            }

            // Count files in export directory
            var fileCount = Directory.GetFiles(result.ExportDirectory!, "*", SearchOption.AllDirectories).Length;
            var dirSize = new DirectoryInfo(result.ExportDirectory!)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);

            Console.Error.WriteLine($"[export_app] Exported to {result.ExportDirectory}");

            return $"SUCCESS: App exported as standalone executable.\n" +
                   $"Executable: {result.ExePath}\n" +
                   $"Directory: {result.ExportDirectory}\n" +
                   $"Files: {fileCount}, Size: {dirSize / 1024.0 / 1024.0:F1} MB\n\n" +
                   $"The user can run this app directly — no N.E.O. or MCP server needed.";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[export_app] EXCEPTION: {ex}");
            return $"ERROR in export_app: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Parses NuGet packages from a JSON string like '{"Humanizer": "default", "Bogus": "35.6.1"}'.
    /// Using string instead of Dictionary parameter to avoid MCP SDK deserialization issues.
    /// </summary>
    /// <summary>
    /// Injects live data into a running app's controls without recompilation.
    /// </summary>
    [McpServerTool(Name = "inject_data")]
    [Description("Injects live data into a running app's controls WITHOUT recompilation. " +
        "Three modes: 'replace' pushes JSON array data into ListBox/ItemsControl/ComboBox (replaces existing). " +
        "'append' adds items to existing data (for live feeds). " +
        "'fill' sets values on multiple form controls (TextBox, CheckBox, Slider, ComboBox) in one call. " +
        "Auto-generates display templates for items controls. Preserves app state.")]
    public static async Task<string> InjectData(
        PreviewSessionManager preview,
        [Description("Target control. For 'replace'/'append': the items control (e.g. 'myListBox', 'ListBox:0'). " +
            "For 'fill': 'root' or a parent container name.")] string target,
        [Description("Injection mode: 'replace' (set new items), 'append' (add to existing), " +
            "or 'fill' (set form control values by name).")] string mode,
        [Description("JSON data. For 'replace'/'append': a JSON array of objects, e.g. " +
            "'[{\"name\":\"Alice\",\"age\":30},{\"name\":\"Bob\",\"age\":25}]'. " +
            "For 'fill': a JSON object mapping control names to values, e.g. " +
            "'{\"nameBox\":\"Alice\",\"ageSlider\":30,\"activeCheck\":true}'.")] string dataJson,
        [Description("Auto-generate ItemTemplate if the control has none. Default true.")] bool autoTemplate = true,
        [Description("Comma-separated field names for auto-template (shows only these fields). " +
            "If omitted, all fields are shown. Example: 'name,email,role'.")] string? focusFields = null)
    {
        if (!preview.IsRunning)
            return "No preview is running. Call compile_and_preview first.";

        try
        {
            var request = new InjectDataRequest(target, mode, dataJson, autoTemplate, focusFields);
            var result = await preview.InjectDataAsync(request);

            if (result == null)
                return "InjectData failed: no response from preview window.";

            if (!result.Success)
                return $"INJECT FAILED: {result.Message}";

            var fieldsInfo = result.DetectedFields != null
                ? $"\nFields: {string.Join(", ", result.DetectedFields)}"
                : "";

            return $"OK: {result.Message}" +
                   (result.ItemCount.HasValue ? $"\nItem count: {result.ItemCount}" : "") +
                   fieldsInfo;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[inject_data] EXCEPTION: {ex}");
            return $"ERROR: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Reads current data from a running app's controls.
    /// </summary>
    [McpServerTool(Name = "read_data")]
    [Description("Reads current data from a running app's controls. " +
        "For items controls (ListBox, ComboBox): returns all items as JSON array. " +
        "For form containers: returns all named children's current values as JSON object. " +
        "For individual controls: returns the control's current value. " +
        "Use this to see what the user typed, verify inject_data results, or read app state.")]
    public static async Task<string> ReadData(
        PreviewSessionManager preview,
        [Description("Target control. A Name (e.g. 'myListBox'), type (e.g. 'ListBox'), " +
            "type:index (e.g. 'TextBox:2'), or 'root' for the entire UserControl.")] string target,
        [Description("What to read: 'items' (ItemsSource data), 'form' (all named children values), " +
            "'value' (single control's value). If omitted, auto-detects based on control type.")] string? scope = null)
    {
        if (!preview.IsRunning)
            return "No preview is running. Call compile_and_preview first.";

        try
        {
            var request = new ReadDataRequest(target, scope);
            var result = await preview.ReadDataAsync(request);

            if (result == null)
                return "ReadData failed: no response from preview window.";

            if (!result.Success)
                return $"READ FAILED: {result.Message}";

            return $"{result.Message}\n{result.DataJson}";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[read_data] EXCEPTION: {ex}");
            return $"ERROR: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Extracts the current visual state of the running app as compilable C# source code.
    /// All set_property changes, inject_data modifications, and visual tweaks are captured.
    /// </summary>
    [McpServerTool(Name = "extract_code")]
    [Description("Reverse-engineers the running app back into clean C# source code. " +
        "Captures the CURRENT state — including all changes made via set_property, inject_data, " +
        "and other live modifications. The generated code is a complete DynamicUserControl " +
        "that can be compiled with compile_and_preview or exported with export_app. " +
        "Use this after live-designing an app to get production-ready code.")]
    public static async Task<string> ExtractCode(PreviewSessionManager preview)
    {
        if (!preview.IsRunning)
            return "No preview is running. Call compile_and_preview first.";

        var code = await preview.ExtractCodeAsync();
        if (code == null)
            return "Failed to extract code. The preview window may not be visible.";

        return code;
    }

    /// <summary>
    /// Applies a unified diff patch to the last compiled code and hot-reloads.
    /// </summary>
    [McpServerTool(Name = "patch_preview")]
    [Description("Applies a unified diff patch to the LAST compiled source code and hot-reloads. " +
        "Much more efficient than update_preview — send only the changed lines instead of the full code. " +
        "Uses standard unified diff format (--- a/file, +++ b/file, @@ hunks). " +
        "Supports fuzzy matching for context lines. Requires a previous compile_and_preview call.")]
    public static async Task<string> PatchPreview(
        CompilationPipeline compilation,
        PreviewSessionManager preview,
        [Description("Unified diff patch text. Standard format with @@ hunk headers. " +
            "Target file should be './currentcode.cs'. Example:\n" +
            "--- a/currentcode.cs\n+++ b/currentcode.cs\n@@ -5,3 +5,3 @@\n " +
            "old context\n-old line\n+new line\n old context")] string patch,
        [Description("NuGet packages as JSON object string. Only needed if adding new packages. " +
            "Omit to keep the same packages from the last compilation.")] string? nugetPackages = null)
    {
        if (compilation.LastSourceCode == null || compilation.LastSourceCode.Count == 0)
            return "PATCH FAILED: No previous source code found. Call compile_and_preview first.";

        try
        {
            // Apply patch to the last source code (typically single file)
            var originalCode = string.Join("\n", compilation.LastSourceCode);
            var patchResult = Neo.AssemblyForge.UnifiedDiffPatcher.TryApply(
                originalCode, patch, "./currentcode.cs", "DynamicUserControl");

            if (!patchResult.Success)
                return $"PATCH FAILED: {patchResult.ErrorMessage}\n\n" +
                       "Make sure the patch context lines match the current code. " +
                       "Use extract_code to see the current state.";

            var patchedCode = patchResult.PatchedText!;

            // Determine NuGet packages
            var packages = nugetPackages != null
                ? ParseNuGetPackages(nugetPackages)
                : compilation.LastNuGetPackages != null
                    ? new Dictionary<string, string>(compilation.LastNuGetPackages, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            EnsureAvaloniaPackages(packages);

            // Compile the patched code
            var result = await compilation.CompileAsync(new[] { patchedCode }, packages);
            if (!result.Success)
                return $"PATCH applied but COMPILATION FAILED:\n{string.Join("\n", result.Errors)}";

            // Hot-reload if preview is running
            if (!preview.IsRunning)
                return $"PATCH applied and compiled, but no preview window is running.\n" +
                       $"DLL size: {result.DllBytes!.Length:N0} bytes";

            var deps = new Dictionary<string, byte[]>();
            foreach (var dllPath in result.DependencyDllPaths)
                if (File.Exists(dllPath))
                    deps[Path.GetFileName(dllPath)] = await File.ReadAllBytesAsync(dllPath);

            var updated = await preview.UpdateAsync(result.DllBytes!, "DynamicUserControl.dll", deps);
            if (!updated)
                return $"PATCH applied and compiled, but hot-reload failed.\n" +
                       $"Logs: {string.Join("\n", preview.ChildLogs)}";

            return $"SUCCESS: Patch applied and preview updated.\n" +
                   $"DLL size: {result.DllBytes!.Length:N0} bytes, Dependencies: {deps.Count}";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[patch_preview] EXCEPTION: {ex}");
            return $"ERROR: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Starts an HTTP + WebSocket server inside the preview process.
    /// Serves an HTML page and enables bidirectional real-time communication
    /// between a web browser and the running Avalonia app.
    /// </summary>
    [McpServerTool(Name = "start_web_bridge")]
    [Description("Starts a web server inside the preview process that serves an HTML page and " +
        "accepts WebSocket connections. The browser and the Avalonia app can communicate " +
        "bidirectionally in real-time. Use {{WS_URL}} in the HTML as a placeholder for the " +
        "WebSocket URL. The preview must be running first (call compile_and_preview).")]
    public static async Task<string> StartWebBridge(
        PreviewSessionManager preview,
        [Description("Complete HTML page content including JavaScript with WebSocket client code. " +
            "Use {{WS_URL}} as placeholder for the WebSocket URL — it will be replaced automatically. " +
            "Example: new WebSocket('{{WS_URL}}') in JS.")] string htmlContent,
        [Description("Port number for the HTTP server. Default: auto-detect a free port.")] int port = 0)
    {
        if (!preview.IsRunning)
            return "No preview is running. Call compile_and_preview first.";

        try
        {
            var request = new StartWebBridgeRequest(htmlContent, port);
            var result = await preview.StartWebBridgeAsync(request);

            if (result == null)
                return "Failed to start web bridge: no response.";

            if (!result.Success)
                return $"WEB BRIDGE FAILED: {result.Error}";

            return $"SUCCESS: Web bridge started.\n" +
                   $"URL: {result.Url}\n" +
                   $"WebSocket: {result.WsUrl}\n\n" +
                   $"Open {result.Url} in a browser to see the web app. " +
                   $"The browser and the Avalonia preview window can now communicate in real-time via WebSocket.";
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Sends a message to all connected web bridge clients (browsers).
    /// </summary>
    [McpServerTool(Name = "send_to_web")]
    [Description("Sends a JSON message to all browsers connected to the web bridge. " +
        "The message is delivered via WebSocket to all connected clients. " +
        "Use this to push data or commands from Claude to the web app.")]
    public static async Task<string> SendToWeb(
        PreviewSessionManager preview,
        [Description("JSON message to send to all connected browsers.")] string message)
    {
        if (!preview.IsRunning)
            return "No preview is running.";

        var ok = await preview.SendToWebBridgeAsync(message);
        return ok ? "Message sent to web bridge clients." : "Failed to send — web bridge may not be running.";
    }

    /// <summary>
    /// Stops the web bridge server.
    /// </summary>
    [McpServerTool(Name = "stop_web_bridge")]
    [Description("Stops the HTTP + WebSocket server started by start_web_bridge.")]
    public static async Task<string> StopWebBridge(PreviewSessionManager preview)
    {
        if (!preview.IsRunning)
            return "No preview is running.";

        await preview.StopWebBridgeAsync();
        return "Web bridge stopped.";
    }

    /// <summary>
    /// Saves the current session (source code, NuGet packages, WebBridge) to a .neo file.
    /// </summary>
    [McpServerTool(Name = "save_session")]
    [Description("Saves the current app session to a .neo file. " +
        "Includes source code, NuGet packages, and WebBridge HTML (if active). " +
        "The session can be loaded later with load_session to restore the app exactly as it was.")]
    public static string SaveSession(
        CompilationPipeline compilation,
        PreviewSessionManager preview,
        [Description("Session name (used as filename, e.g. 'MyCalculator').")] string name,
        [Description("Absolute path to the directory where the session file should be saved. " +
            "Must be a folder that Claude has write access to.")] string directory)
    {
        if (compilation.LastSourceCode == null || compilation.LastSourceCode.Count == 0)
            return "SAVE FAILED: No source code found. Call compile_and_preview first.";

        if (string.IsNullOrWhiteSpace(name))
            return "SAVE FAILED: name cannot be empty.";
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
            return "SAVE FAILED: directory must be an absolute path.";

        try
        {
            Directory.CreateDirectory(directory);

            var session = new Dictionary<string, object?>
            {
                ["version"] = 1,
                ["name"] = name,
                ["savedAt"] = DateTime.UtcNow.ToString("o"),
                ["sourceCode"] = compilation.LastSourceCode,
                ["nugetPackages"] = compilation.LastNuGetPackages,
                ["webBridgeHtml"] = preview.LastWebBridgeHtml,
            };

            var json = System.Text.Json.JsonSerializer.Serialize(session,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            var filePath = Path.Combine(directory, $"{name}.neo");
            File.WriteAllText(filePath, json);

            return $"SUCCESS: Session saved.\nFile: {filePath}\nSize: {json.Length:N0} bytes";
        }
        catch (Exception ex)
        {
            return $"SAVE FAILED: {ex.Message}";
        }
    }

    /// <summary>
    /// Loads a session from a .neo file and auto-compiles the app.
    /// </summary>
    [McpServerTool(Name = "load_session")]
    [Description("Loads a previously saved .neo session file and automatically compiles and " +
        "displays the app. If the session included a WebBridge, it is also restarted. " +
        "The app appears exactly as it was when saved.")]
    public static async Task<string> LoadSession(
        CompilationPipeline compilation,
        PreviewSessionManager preview,
        [Description("Absolute path to the .neo session file.")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "LOAD FAILED: path cannot be empty.";
        if (!File.Exists(path))
            return $"LOAD FAILED: File not found: {path}";

        try
        {
            var json = await File.ReadAllTextAsync(path);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Read source code
            var sourceCode = new List<string>();
            if (root.TryGetProperty("sourceCode", out var srcArray))
            {
                foreach (var item in srcArray.EnumerateArray())
                    sourceCode.Add(item.GetString() ?? "");
            }

            if (sourceCode.Count == 0)
                return "LOAD FAILED: Session file contains no source code.";

            // Read NuGet packages
            var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("nugetPackages", out var pkgs) &&
                pkgs.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var prop in pkgs.EnumerateObject())
                    packages[prop.Name] = prop.Value.GetString() ?? "default";
            }
            EnsureAvaloniaPackages(packages);

            // Compile
            var result = await compilation.CompileAsync(sourceCode, packages);
            if (!result.Success)
                return $"LOAD FAILED: Compilation error:\n{string.Join("\n", result.Errors)}";

            // Start preview
            if (!preview.IsRunning)
            {
                var started = await preview.StartAsync();
                if (!started)
                    return $"Compiled but preview window failed to start.\n" +
                           $"Logs: {string.Join("\n", preview.ChildLogs)}";
            }

            // Send DLL
            var deps = new Dictionary<string, byte[]>();
            foreach (var dllPath in result.DependencyDllPaths)
                if (File.Exists(dllPath))
                    deps[Path.GetFileName(dllPath)] = await File.ReadAllBytesAsync(dllPath);

            var sent = await preview.SendDllAsync(result.DllBytes!, "DynamicUserControl.dll", deps);
            if (!sent)
                return $"Compiled but failed to send DLL to preview.\n" +
                       $"Logs: {string.Join("\n", preview.ChildLogs)}";

            // Restore WebBridge if saved
            string webBridgeInfo = "";
            if (root.TryGetProperty("webBridgeHtml", out var htmlProp) &&
                htmlProp.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var html = htmlProp.GetString();
                if (!string.IsNullOrWhiteSpace(html))
                {
                    var bridgeResult = await preview.StartWebBridgeAsync(
                        new Neo.IPC.StartWebBridgeRequest(html));
                    if (bridgeResult?.Success == true)
                        webBridgeInfo = $"\nWebBridge: {bridgeResult.Url}";
                }
            }

            var sessionName = root.TryGetProperty("name", out var nm) ? nm.GetString() : Path.GetFileNameWithoutExtension(path);

            return $"SUCCESS: Session '{sessionName}' loaded and running.\n" +
                   $"DLL size: {result.DllBytes!.Length:N0} bytes, Dependencies: {deps.Count}" +
                   webBridgeInfo;
        }
        catch (Exception ex)
        {
            return $"LOAD FAILED: {ex.Message}";
        }
    }

    /// <summary>
    /// Runs UI assertions against the running app's visual tree.
    /// </summary>
    [McpServerTool(Name = "run_test")]
    [Description("Runs UI test assertions against the running app. " +
        "Checks property values on controls without modifying anything. " +
        "Pass a JSON array of assertions, each with: target (control name/type), " +
        "property (property name), expected (expected value), and optional operator " +
        "(=, !=, >, <, >=, <=, contains, exists). Default operator is '='.")]
    public static async Task<string> RunTest(
        PreviewSessionManager preview,
        [Description("JSON array of assertions. Example: " +
            "'[{\"target\":\"title\",\"property\":\"Text\",\"expected\":\"Hello\"}," +
            "{\"target\":\"slider\",\"property\":\"Value\",\"operator\":\">\",\"expected\":\"50\"}," +
            "{\"target\":\"submitBtn\",\"property\":\"IsEnabled\",\"expected\":\"true\"}]'")] string assertions)
    {
        if (!preview.IsRunning)
            return "No preview is running. Call compile_and_preview first.";

        // Get the visual tree
        var treeJson = await preview.InspectVisualTreeAsync();
        if (treeJson == null)
            return "Failed to inspect visual tree.";

        // Parse assertions
        List<TestAssertion> tests;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(assertions);
            tests = new List<TestAssertion>();
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                tests.Add(new TestAssertion(
                    Target: elem.GetProperty("target").GetString()!,
                    Property: elem.GetProperty("property").GetString()!,
                    Expected: elem.GetProperty("expected").GetString() ?? elem.GetProperty("expected").GetRawText(),
                    Operator: elem.TryGetProperty("operator", out var op) ? op.GetString() ?? "=" : "="
                ));
            }
        }
        catch (Exception ex)
        {
            return $"Invalid assertions JSON: {ex.Message}";
        }

        // Parse visual tree and build a flat lookup
        Dictionary<string, Dictionary<string, string>> controlProps;
        try
        {
            using var treeDoc = System.Text.Json.JsonDocument.Parse(treeJson);
            controlProps = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            FlattenTree(treeDoc.RootElement, controlProps, 0);
        }
        catch (Exception ex)
        {
            return $"Failed to parse visual tree: {ex.Message}";
        }

        // Run assertions
        int passed = 0;
        int failed = 0;
        var results = new List<string>();

        foreach (var test in tests)
        {
            // Find control
            if (!controlProps.TryGetValue(test.Target, out var props))
            {
                if (test.Operator == "exists")
                {
                    failed++;
                    results.Add($"  FAIL: '{test.Target}' does not exist");
                    continue;
                }
                failed++;
                results.Add($"  FAIL: Control '{test.Target}' not found");
                continue;
            }

            if (test.Operator == "exists")
            {
                passed++;
                results.Add($"  PASS: '{test.Target}' exists");
                continue;
            }

            // Get property value
            if (!props.TryGetValue(test.Property, out var actual))
            {
                // Also check type-independent "type" key
                if (test.Property.Equals("type", StringComparison.OrdinalIgnoreCase) && props.TryGetValue("__type__", out actual))
                { /* ok */ }
                else
                {
                    failed++;
                    results.Add($"  FAIL: {test.Target}.{test.Property} — property not found");
                    continue;
                }
            }

            // Compare
            bool pass = EvaluateAssertion(actual, test.Operator, test.Expected);

            if (pass)
            {
                passed++;
                results.Add($"  PASS: {test.Target}.{test.Property} {test.Operator} \"{test.Expected}\" (actual: \"{actual}\")");
            }
            else
            {
                failed++;
                results.Add($"  FAIL: {test.Target}.{test.Property} {test.Operator} \"{test.Expected}\" — actual: \"{actual}\"");
            }
        }

        var summary = failed == 0
            ? $"ALL {passed} PASSED"
            : $"{passed}/{passed + failed} passed, {failed} FAILED";

        return $"{summary}\n\n{string.Join("\n", results)}";
    }

    private record TestAssertion(string Target, string Property, string Expected, string Operator);

    private static void FlattenTree(
        System.Text.Json.JsonElement node,
        Dictionary<string, Dictionary<string, string>> result,
        int typeCounter)
    {
        var typeName = node.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        var name = node.TryGetProperty("name", out var n) ? n.GetString() : null;

        // Build property dict for this control
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        props["__type__"] = typeName;

        // Add bounds
        if (node.TryGetProperty("bounds", out var bounds))
        {
            if (bounds.TryGetProperty("w", out var w)) props["Width"] = w.GetRawText();
            if (bounds.TryGetProperty("h", out var h)) props["Height"] = h.GetRawText();
            if (bounds.TryGetProperty("x", out var x)) props["X"] = x.GetRawText();
            if (bounds.TryGetProperty("y", out var y)) props["Y"] = y.GetRawText();
        }

        // Add properties
        if (node.TryGetProperty("properties", out var nodeProps))
        {
            foreach (var prop in nodeProps.EnumerateObject())
                props[prop.Name] = prop.Value.GetString() ?? prop.Value.GetRawText();
        }

        // Register by name (priority) and by type:index
        if (!string.IsNullOrEmpty(name) && !name.StartsWith("PART_"))
            result[name] = props;

        // Also register by Type:Index
        var typeKey = $"{typeName}:{typeCounter}";
        if (!result.ContainsKey(typeKey))
            result[typeKey] = props;
        if (!result.ContainsKey(typeName))
            result[typeName] = props;

        // Recurse into children
        if (node.TryGetProperty("children", out var children))
        {
            var childCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in children.EnumerateArray())
            {
                var childType = child.TryGetProperty("type", out var ct) ? ct.GetString() ?? "" : "";
                if (!childCounters.TryGetValue(childType, out var idx))
                    idx = 0;
                FlattenTree(child, result, idx);
                childCounters[childType] = idx + 1;
            }
        }
    }

    private static bool EvaluateAssertion(string actual, string op, string expected)
    {
        switch (op.ToLowerInvariant())
        {
            case "=" or "==" or "equals":
                return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

            case "!=" or "notequals":
                return !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

            case "contains":
                return actual.Contains(expected, StringComparison.OrdinalIgnoreCase);

            case ">" or "<" or ">=" or "<=":
                if (double.TryParse(actual, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var actualNum) &&
                    double.TryParse(expected, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var expectedNum))
                {
                    return op switch
                    {
                        ">" => actualNum > expectedNum,
                        "<" => actualNum < expectedNum,
                        ">=" => actualNum >= expectedNum,
                        "<=" => actualNum <= expectedNum,
                        _ => false
                    };
                }
                return false;

            default:
                return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, string> ParseNuGetPackages(string? nugetPackagesJson)
    {
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(nugetPackagesJson))
            return packages;

        try
        {
            using var doc = JsonDocument.Parse(nugetPackagesJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var name = prop.Name;
                var version = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    _ => null,
                };

                if (!string.IsNullOrWhiteSpace(name))
                    packages[name] = string.IsNullOrWhiteSpace(version) ? "default" : version;
            }
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[ParseNuGetPackages] Failed to parse: {nugetPackagesJson} — {ex.Message}");
            // Try fallback: comma-separated "Name|Version" format
            foreach (var entry in nugetPackagesJson.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split('|', 2);
                if (parts.Length >= 1 && !string.IsNullOrWhiteSpace(parts[0]))
                    packages[parts[0].Trim()] = parts.Length > 1 ? parts[1].Trim() : "default";
            }
        }

        return packages;
    }

    private static void EnsureAvaloniaPackages(Dictionary<string, string> packages)
    {
        var avaloniaPackages = new[]
        {
            "Avalonia",
            "Avalonia.Desktop",
            "Avalonia.Themes.Fluent",
            "Avalonia.Fonts.Inter",
        };

        foreach (var pkg in avaloniaPackages)
        {
            if (!packages.ContainsKey(pkg))
                packages[pkg] = "11.3.12";
        }
    }
}
