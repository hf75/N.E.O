using System.ComponentModel;
using ModelContextProtocol.Server;
using Neo.IPC;
using Neo.McpServer.Services;

namespace Neo.McpServer.Tools;

public sealed partial class PreviewTools
{
    // ========================================
    // The Claude bridge
    // ========================================

    /// <summary>Starts the Claude bridge (or binds the running one to the session anew).</summary>
    [McpServerTool(Name = "start_claude_bridge")]
    [Description("Claude desktop app on Windows: channels do not arrive there, so the prompts that apps send with Ai.Trigger would be lost. " +
        "This docks a small panel, the Claude bridge, to the Claude window. It types each such prompt into this session's prompt box, " +
        "starting with 'From the N.E.O. app \"<window id>\":', while this session is in front, Claude is not working and the box is empty; " +
        "until then the prompt waits in the panel. Pass this session's title and link from get_session with 'self'. " +
        "Calling it again while the bridge runs binds it anew, for instance after the session was renamed. " +
        "close_preview with windowId 'claude-bridge' closes it.")]
    public static async Task<string> StartClaudeBridge(
        CompilationPipeline compilation,
        PreviewSessionManager preview,
        [Description("This session's title, as get_session with 'self' returns it: the bridge recognizes the session in the Claude window by it.")] string sessionTitle,
        [Description("This session's link from get_session with 'self' (claude://…): the bridge offers to open the session with it.")] string? sessionLink = null)
    {
        if (!OperatingSystem.IsWindows())
            return "The Claude bridge needs Windows: it docks to the window of the Claude desktop app.";
        if (string.IsNullOrWhiteSpace(sessionTitle))
            return "Pass this session's title (get_session with 'self').";

        try
        {
            var started = !preview.IsRunning(ClaudeBridgeApp.WindowId);
            if (started)
            {
                var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                EnsureAvaloniaPackages(packages);
                var result = await compilation.CompileAsync(ClaudeBridgeApp.Sources, packages, "avalonia", remember: false);
                if (!result.Success)
                    return "The Claude bridge did not compile:\n" + string.Join("\n", result.Errors);

                if (!await preview.StartAsync(ClaudeBridgeApp.WindowId, "avalonia"))
                    return "The bridge's window did not start.\nLogs: " + string.Join("\n", preview.GetChildLogs(ClaudeBridgeApp.WindowId));

                if (!await preview.SendDllAsync(result.DllBytes!, "DynamicUserControl.dll", windowId: ClaudeBridgeApp.WindowId, dllPath: result.DllPath))
                    return "The bridge compiled but did not load.\nLogs: " + string.Join("\n", preview.GetChildLogs(ClaudeBridgeApp.WindowId));
            }

            // The app may still be coming up in its window.
            MethodResultMessage? bound = null;
            for (var attempt = 0; attempt < 20 && bound is not { Success: true }; attempt++)
            {
                if (attempt > 0) await Task.Delay(500);
                bound = await preview.CallClaudeBridgeAsync("Bind", sessionTitle.Trim(), sessionLink?.Trim() ?? "");
            }
            if (bound is not { Success: true })
                return "The bridge runs but did not take the session: " + (bound?.Error ?? "no answer") +
                       LoadNote(preview, ClaudeBridgeApp.WindowId);

            return (started ? "The Claude bridge runs" : "The Claude bridge is bound anew") +
                   $" and types the Ai.Trigger prompts of this server's apps into the session \"{sessionTitle.Trim()}\", " +
                   "when it is in front, Claude is not working and the prompt box is empty. " +
                   "Tell the user that a small panel now sits at the lower right of the Claude window and shows what waits.";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[start_claude_bridge] EXCEPTION: {ex}");
            return $"ERROR in start_claude_bridge: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
