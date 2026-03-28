using System.ComponentModel;
using ModelContextProtocol.Server;
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
        "Pass all required NuGet packages as a dictionary of packageName -> version (use 'default' for latest). " +
        "Avalonia 11.3.12 packages are always included automatically.")]
    public static async Task<string> CompileAndPreview(
        CompilationPipeline compilation,
        PreviewSessionManager preview,
        [Description("Complete C# source code files. Each string is one .cs file. " +
            "The main file must contain a class 'DynamicUserControl : UserControl'.")] string[] sourceCode,
        [Description("NuGet packages required. Key = package name, Value = version or 'default'. " +
            "Avalonia packages are added automatically.")] Dictionary<string, string>? nugetPackages = null)
    {
        try
        {
            Console.Error.WriteLine($"[compile_and_preview] Called with {sourceCode.Length} source file(s), " +
                $"nugetPackages={(nugetPackages == null ? "null" : string.Join(", ", nugetPackages.Select(kv => $"{kv.Key}={kv.Value}")))}");

            // Sanitize NuGet packages — handle null values from JSON deserialization
            var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (nugetPackages != null)
            {
                foreach (var (key, value) in nugetPackages)
                {
                    if (!string.IsNullOrWhiteSpace(key))
                        packages[key] = string.IsNullOrWhiteSpace(value) ? "default" : value;
                }
            }
            EnsureAvaloniaPackages(packages);
            Console.Error.WriteLine($"[compile_and_preview] Packages after sanitize: {string.Join(", ", packages.Select(kv => $"{kv.Key}={kv.Value}"))}");
            Console.Error.WriteLine("[compile_and_preview] Starting compilation...");

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
        [Description("NuGet packages required.")] Dictionary<string, string>? nugetPackages = null)
    {
        try
        {
        if (!preview.IsRunning)
            return await CompileAndPreview(compilation, preview, sourceCode, nugetPackages);

        // Sanitize NuGet packages
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (nugetPackages != null)
        {
            foreach (var (key, value) in nugetPackages)
            {
                if (!string.IsNullOrWhiteSpace(key))
                    packages[key] = string.IsNullOrWhiteSpace(value) ? "default" : value;
            }
        }
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

        await preview.StopAsync();
        return "Preview window closed.";
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

        return $"Preview Status: {status}\n" +
               $"Avalonia Version: 11.3.12\n" +
               $"Framework: .NET 9\n" +
               $"Recent Logs:\n{logs}";
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
