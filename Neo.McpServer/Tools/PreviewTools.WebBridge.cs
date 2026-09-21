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
        [Description("Port number for the HTTP server. Default: auto-detect a free port.")] int port = 0,
        [Description("Window ID for multi-window mode. Use different IDs to create multiple windows. " +
            "Omit for single-window mode (uses default window).")] string? windowId = null)
    {
        if (!preview.IsRunning(windowId))
            return "No preview is running. Call compile_and_preview first.";

        try
        {
            var request = new StartWebBridgeRequest(htmlContent, port);
            var result = await preview.StartWebBridgeAsync(request, windowId: windowId);

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
        [Description("JSON message to send to all connected browsers.")] string message,
        [Description("Window ID for multi-window mode. Use different IDs to create multiple windows. " +
            "Omit for single-window mode (uses default window).")] string? windowId = null)
    {
        if (!preview.IsRunning(windowId))
            return "No preview is running.";

        var ok = await preview.SendToWebBridgeAsync(message, windowId: windowId);
        return ok ? "Message sent to web bridge clients." : "Failed to send — web bridge may not be running.";
    }

    /// <summary>
    /// Stops the web bridge server.
    /// </summary>
    [McpServerTool(Name = "stop_web_bridge")]
    [Description("Stops the HTTP + WebSocket server started by start_web_bridge.")]
    public static async Task<string> StopWebBridge(
        PreviewSessionManager preview,
        [Description("Window ID for multi-window mode. Use different IDs to create multiple windows. " +
            "Omit for single-window mode (uses default window).")] string? windowId = null)
    {
        if (!preview.IsRunning(windowId))
            return "No preview is running.";

        await preview.StopWebBridgeAsync(windowId: windowId);
        return "Web bridge stopped.";
    }
}
