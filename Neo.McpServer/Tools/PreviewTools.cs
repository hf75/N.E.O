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
