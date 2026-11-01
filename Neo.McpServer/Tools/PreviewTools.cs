using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Neo.IPC;
using Neo.McpServer.Services;

namespace Neo.McpServer.Tools;

[McpServerToolType]
public sealed partial class PreviewTools
{
    /// <summary>
    /// Compiles C# UserControl source code and displays it as a live preview window.
    /// If no preview window is running, one is started automatically.
    /// Supports both Avalonia (cross-platform) and WPF (Windows-only) frameworks.
    /// </summary>
    [McpServerTool(Name = "compile_and_preview")]
    [Description("Compiles C# UserControl code and shows it in a LIVE PREVIEW window on the user's desktop. " +
        "Supports TWO frameworks: 'avalonia' (default, cross-platform) and 'wpf' (Windows-only). " +
        "WHEN THE USER ASKS FOR A WPF APP: Use this tool with framework='wpf'. " +
        "Do NOT create project files — this tool compiles and previews WPF apps live, just like Avalonia. " +
        "For WPF: use System.Windows.Controls.UserControl, System.Windows.* namespaces, no extra NuGet needed. " +
        "For Avalonia: use Avalonia.Controls.UserControl, Avalonia packages are added automatically. " +
        "Both: class MUST be named 'DynamicUserControl', pure C# code-behind (no XAML files).")]
    public static async Task<string> CompileAndPreview(
        CompilationPipeline compilation,
        PreviewSessionManager preview,
        [Description("Complete C# source code files. Each string is one .cs file. " +
            "The main file must contain a class 'DynamicUserControl : UserControl'.")] string[] sourceCode,
        [Description("NuGet packages as JSON object string, e.g. '{\"Humanizer\": \"default\", \"Bogus\": \"35.6.1\"}'. " +
            "Use 'default' for latest stable version. Avalonia packages are added automatically for Avalonia framework. " +
            "Omit or pass empty string if no extra packages needed.")] string? nugetPackages = null,
        [Description("Window ID for multi-window mode. Use different IDs to create multiple windows. " +
            "Omit for single-window mode (uses default window).")] string? windowId = null,
        [Description("UI framework: 'avalonia' (default, cross-platform) or 'wpf' (Windows-only). " +
            "The preview window and compilation will use the specified framework.")] string? framework = null)
    {
        try
        {
            var fw = ResolveFramework(framework, preview, compilation, windowId);
            var packages = ParseNuGetPackages(nugetPackages);
            Console.Error.WriteLine($"[compile_and_preview] Called with {sourceCode.Length} source file(s), " +
                $"framework={fw}, packages={string.Join(", ", packages.Select(kv => $"{kv.Key}={kv.Value}"))}");

            if (fw == "avalonia")
                EnsureAvaloniaPackages(packages);

            // Compile
            var result = await compilation.CompileAsync(sourceCode, packages, fw);
            if (!result.Success)
            {
                return $"COMPILATION FAILED:\n{string.Join("\n", result.Errors)}\n\n" +
                       "Fix the errors and try again.";
            }

            // Start preview if not running
            if (!preview.IsRunning(windowId))
            {
                var started = await preview.StartAsync(windowId, fw);
                if (!started)
                {
                    return $"Compilation succeeded but preview window failed to start.\n" +
                           $"Logs: {string.Join("\n", preview.GetChildLogs(windowId))}";
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
                deps,
                windowId: windowId,
                dllPath: result.DllPath);

            if (!sent)
            {
                return $"Compilation succeeded but failed to send DLL to preview window.\n" +
                       $"Logs: {string.Join("\n", preview.GetChildLogs(windowId))}";
            }
            await preview.SendSourceAsync(sourceCode, windowId);

            return $"SUCCESS: App compiled and displayed in live preview window.\n" +
                   $"DLL size: {result.DllBytes!.Length:N0} bytes, Dependencies: {deps.Count}" +
                   LoadNote(preview, windowId);
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
        "The running app is patched in place when possible (hot reload): its data and UI state survive, " +
        "and the result says 'Hot reload (generation n)' — or 'Hot reload not possible — <reason>' when it had to restart the app. " +
        "Use this (not compile_and_preview) for every change to the running app.")]
    public static async Task<string> UpdatePreview(
        CompilationPipeline compilation,
        PreviewSessionManager preview,
        [Description("Updated C# source code files.")] string[] sourceCode,
        [Description("NuGet packages as JSON object string, e.g. '{\"Humanizer\": \"default\"}'. " +
            "Omit or pass empty string if no extra packages needed.")] string? nugetPackages = null,
        [Description("Window ID for multi-window mode.")] string? windowId = null,
        [Description("UI framework: 'avalonia' (default) or 'wpf' (Windows-only). " +
            "Auto-detected from running window if omitted.")] string? framework = null)
    {
        try
        {
            var fw = ResolveFramework(framework, preview, compilation, windowId);

            if (!preview.IsRunning(windowId))
                return await CompileAndPreview(compilation, preview, sourceCode, nugetPackages, windowId, fw);

            var packages = ParseNuGetPackages(nugetPackages);
            if (fw == "avalonia")
                EnsureAvaloniaPackages(packages);

            var result = await compilation.CompileAsync(sourceCode, packages, fw);
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
                deps,
                windowId: windowId,
                dllPath: result.DllPath);

            if (!updated)
            {
                return $"Compilation succeeded but hot-reload failed.\n" +
                       $"Logs: {string.Join("\n", preview.GetChildLogs(windowId))}";
            }
            await preview.SendSourceAsync(sourceCode, windowId);

            return $"SUCCESS: Preview updated live.\n" +
                   $"DLL size: {result.DllBytes!.Length:N0} bytes, Dependencies: {deps.Count}" +
                   LoadNote(preview, windowId);
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
    public static async Task<string> ClosePreview(
        PreviewSessionManager preview,
        [Description("Window ID for multi-window mode. Use different IDs to create multiple windows. " +
            "Omit for single-window mode (uses default window).")] string? windowId = null)
    {
        if (!preview.IsRunning(windowId))
            return "No preview window is currently running.";

        try
        {
            await preview.StopAsync(windowId);
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
    public static string GetPreviewStatus(
        PreviewSessionManager preview,
        CompilationPipeline compilation,
        [Description("Window ID for multi-window mode. Use different IDs to create multiple windows. " +
            "Omit for single-window mode (uses default window).")] string? windowId = null)
    {
        var status = preview.IsRunning(windowId) ? "RUNNING" : "STOPPED";
        var fw = preview.GetFramework(windowId) ?? compilation.LastFramework ?? "avalonia";
        var logs = preview.GetChildLogs(windowId).Count > 0
            ? string.Join("\n", preview.GetChildLogs(windowId).TakeLast(20))
            : "(no logs)";

        var errorsSection = preview.GetRuntimeErrors(windowId).Count > 0
            ? $"\nRuntime Errors ({preview.GetRuntimeErrors(windowId).Count}):\n{string.Join("\n", preview.GetRuntimeErrors(windowId).TakeLast(10))}"
            : "\nRuntime Errors: none";

        var frameworkInfo = fw == "wpf" ? $"WPF (.NET {Neo.Shared.NeoBuildInfo.RuntimeMajor} Windows Desktop)" : "Avalonia " + Neo.Shared.NeoBuildInfo.AvaloniaVersion;

        return $"Preview Status: {status}\n" +
               $"UI Framework: {frameworkInfo}\n" +
               $"Framework: .NET {Neo.Shared.NeoBuildInfo.RuntimeMajor}\n" +
               $"Recent Logs:\n{logs}{errorsSection}";
    }

    /// <summary>
    /// Applies a unified diff patch to the last compiled code and hot-reloads.
    /// </summary>
    [McpServerTool(Name = "patch_preview")]
    [Description("Applies a unified diff patch to the LAST compiled source code and updates the running app like update_preview (hot reload: state survives when possible). " +
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
            "Omit to keep the same packages from the last compilation.")] string? nugetPackages = null,
        [Description("Window ID for multi-window mode. Use different IDs to create multiple windows. " +
            "Omit for single-window mode (uses default window).")] string? windowId = null)
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

            // Determine NuGet packages and framework
            var fw = compilation.LastFramework ?? "avalonia";
            var packages = nugetPackages != null
                ? ParseNuGetPackages(nugetPackages)
                : compilation.LastNuGetPackages != null
                    ? new Dictionary<string, string>(compilation.LastNuGetPackages, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (fw == "avalonia")
                EnsureAvaloniaPackages(packages);

            // Compile the patched code
            var result = await compilation.CompileAsync(new[] { patchedCode }, packages, fw);
            if (!result.Success)
                return $"PATCH applied but COMPILATION FAILED:\n{string.Join("\n", result.Errors)}";

            // Hot-reload if preview is running
            if (!preview.IsRunning(windowId))
                return $"PATCH applied and compiled, but no preview window is running.\n" +
                       $"DLL size: {result.DllBytes!.Length:N0} bytes";

            var deps = new Dictionary<string, byte[]>();
            foreach (var dllPath in result.DependencyDllPaths)
                if (File.Exists(dllPath))
                    deps[Path.GetFileName(dllPath)] = await File.ReadAllBytesAsync(dllPath);

            var updated = await preview.UpdateAsync(result.DllBytes!, "DynamicUserControl.dll", deps,
                windowId: windowId, dllPath: result.DllPath);
            if (!updated)
                return $"PATCH applied and compiled, but hot-reload failed.\n" +
                       $"Logs: {string.Join("\n", preview.GetChildLogs(windowId))}";
            await preview.SendSourceAsync(new[] { patchedCode }, windowId);

            return $"SUCCESS: Patch applied and preview updated.\n" +
                   $"DLL size: {result.DllBytes!.Length:N0} bytes, Dependencies: {deps.Count}" +
                   LoadNote(preview, windowId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[patch_preview] EXCEPTION: {ex}");
            return $"ERROR: {ex.GetType().Name}: {ex.Message}";
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

    /// <summary>
    /// Tells the model whether the running app was patched in place (its state is still there) or
    /// replaced (it starts from scratch, and why) — it decides what to verify next based on that.
    /// </summary>
    internal static string LoadNote(PreviewSessionManager preview, string? windowId)
        => preview.GetLastLoadMessage(windowId) is { Length: > 0 } message ? "\n" + message : "";

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
                packages[pkg] = Neo.Shared.NeoBuildInfo.AvaloniaVersion;
        }
    }

    /// <summary>
    /// Resolves the UI framework to use. Priority: explicit parameter > running window > last compilation > default.
    /// </summary>
    private static string ResolveFramework(string? requested, PreviewSessionManager preview,
        CompilationPipeline compilation, string? windowId)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested!.ToLowerInvariant();
        var running = preview.GetFramework(windowId);
        if (running != null) return running;
        if (compilation.LastFramework != null) return compilation.LastFramework;
        return "avalonia";
    }
}
