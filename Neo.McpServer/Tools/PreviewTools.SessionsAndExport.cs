using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Neo.IPC;
using Neo.McpServer.Services;

namespace Neo.McpServer.Tools;

public sealed partial class PreviewTools
{
    /// <summary>
    /// Exports the current app as a standalone executable that can be shared and run without N.E.O.
    /// </summary>
    [McpServerTool(Name = "export_app")]
    [Description("Exports the generated app as a standalone executable. " +
        "The exported app runs independently — no N.E.O., no MCP server, no .NET SDK needed (only the .NET runtime). " +
        "All dependencies (Avalonia, NuGet packages) are included in the export directory. " +
        "Set mcpMode=true (Phase 4 Frozen-Mode) to ALSO embed an MCP stdio server in the exported EXE: " +
        "the app then exposes its [McpCallable] / [McpObservable(Watchable=true)] members as MCP tools / resources " +
        "when launched with --mcp, so any Claude session can `claude mcp add app-name path/to/exe -- --mcp` and use it.")]
    public static async Task<string> ExportApp(
        CompilationPipeline compilation,
        [Description("Complete C# source code files (same as compile_and_preview).")] string[] sourceCode,
        [Description("Name for the exported application (used as folder name and window title).")] string appName,
        [Description("Absolute path to the directory where the app should be exported to, " +
            "e.g. 'C:/Users/heiko/Desktop' or 'C:/tmp'. Must be a full absolute path. " +
            "A subfolder with the app name will be created.")] string exportPath,
        [Description("Target platform: 'windows', 'linux', or 'osx'. Defaults to 'windows'.")] string platform = "windows",
        [Description("NuGet packages as JSON object string, e.g. '{\"Humanizer\": \"default\"}'. " +
            "Omit if no extra packages needed.")] string? nugetPackages = null,
        [Description("Phase 4 Frozen-Mode: when true, the exported EXE also serves as a stdio MCP server " +
            "via Neo.App.Mcp. The CLI exposes three modes: '<exe>' (GUI only, identical to mcpMode=false), " +
            "'<exe> --mcp' (GUI + stdio MCP for Claude to drive the app), '<exe> --mcp-help' (dump the " +
            "manifest to stderr and exit). Adds ~5 MB of runtime DLLs to the export. Defaults to false.")] bool mcpMode = false)
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

            var modeLabel = mcpMode ? "frozen-mode" : "GUI-only";
            Console.Error.WriteLine($"[export_app] Exporting '{appName}' ({modeLabel}) to {exportPath} for {platform}...");

            var packages = ParseNuGetPackages(nugetPackages);

            var result = await compilation.ExportAsync(
                sourceCode, appName, exportPath, platform, packages, mcpMode);

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

            var mcpHints = mcpMode
                ? $"\n\nFrozen-Mode usage:\n" +
                  $"  {result.ExePath}                  → GUI only\n" +
                  $"  {result.ExePath} --mcp            → GUI + stdio MCP server (registerable via `claude mcp add`)\n" +
                  $"  {result.ExePath} --mcp-help       → dump manifest to stderr and exit (smoke test)\n" +
                  $"\nTo register with Claude on another machine: `claude mcp add {appName.ToLowerInvariant()} \"{result.ExePath}\" -- --mcp`."
                : "\n\nThe user can run this app directly — no N.E.O. or MCP server needed.";

            return $"SUCCESS: App exported as standalone executable.\n" +
                   $"Executable: {result.ExePath}\n" +
                   $"Directory: {result.ExportDirectory}\n" +
                   $"Files: {fileCount}, Size: {dirSize / 1024.0 / 1024.0:F1} MB" +
                   mcpHints;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[export_app] EXCEPTION: {ex}");
            return $"ERROR in export_app: {ex.GetType().Name}: {ex.Message}";
        }
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
                ["webBridgeHtml"] = preview.GetLastWebBridgeHtml(),
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
        [Description("Absolute path to the .neo session file.")] string path,
        [Description("Window ID for multi-window mode. Use different IDs to create multiple windows. " +
            "Omit for single-window mode (uses default window).")] string? windowId = null)
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
            if (!preview.IsRunning(windowId))
            {
                var started = await preview.StartAsync(windowId);
                if (!started)
                    return $"Compiled but preview window failed to start.\n" +
                           $"Logs: {string.Join("\n", preview.GetChildLogs(windowId))}";
            }

            // Send DLL
            var deps = new Dictionary<string, byte[]>();
            foreach (var dllPath in result.DependencyDllPaths)
                if (File.Exists(dllPath))
                    deps[Path.GetFileName(dllPath)] = await File.ReadAllBytesAsync(dllPath);

            var sent = await preview.SendDllAsync(result.DllBytes!, "DynamicUserControl.dll", deps);
            if (!sent)
                return $"Compiled but failed to send DLL to preview.\n" +
                       $"Logs: {string.Join("\n", preview.GetChildLogs(windowId))}";

            // Restore WebBridge if saved
            string webBridgeInfo = "";
            if (root.TryGetProperty("webBridgeHtml", out var htmlProp) &&
                htmlProp.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var html = htmlProp.GetString();
                if (!string.IsNullOrWhiteSpace(html))
                {
                    var bridgeResult = await preview.StartWebBridgeAsync(
                        new Neo.IPC.StartWebBridgeRequest(html), windowId: windowId);
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
    /// Registers a saved session as a reusable skill that Claude can automatically
    /// recognize and load in future conversations.
    /// </summary>
    [McpServerTool(Name = "register_skill")]
    [Description("Registers a saved .neo session as a reusable 'skill'. " +
        "Skills are remembered across conversations — when a user asks for something " +
        "that matches a skill's keywords, Claude can load it instantly instead of " +
        "generating new code. Use after save_session to make an app permanently available.")]
    public static string RegisterSkill(
        SkillsRegistry skills,
        [Description("Display name for the skill (e.g. 'Email Reader', 'Calculator').")] string name,
        [Description("Short description of what the app does.")] string description,
        [Description("Comma-separated keywords for auto-matching. Claude uses these to " +
            "recognize when the user wants this app. Example: 'email, mails, inbox, nachrichten'.")] string keywords,
        [Description("Absolute path to the .neo session file (from save_session).")] string sessionPath)
    {
        var (success, message) = skills.Register(name, description, keywords, sessionPath);
        return success ? $"SUCCESS: {message}" : $"FAILED: {message}";
    }

    /// <summary>
    /// Removes a skill from the registry.
    /// </summary>
    [McpServerTool(Name = "unregister_skill")]
    [Description("Removes a skill from the registry. The .neo session file is NOT deleted — " +
        "only the registry entry is removed. The app can still be loaded manually with load_session.")]
    public static string UnregisterSkill(
        SkillsRegistry skills,
        [Description("Name of the skill to remove.")] string name)
    {
        var (success, message) = skills.Unregister(name);
        return success ? $"SUCCESS: {message}" : $"FAILED: {message}";
    }
}
