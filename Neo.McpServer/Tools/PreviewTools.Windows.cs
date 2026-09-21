using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Neo.IPC;
using Neo.McpServer.Services;

namespace Neo.McpServer.Tools;

public sealed partial class PreviewTools
{
    // ========================================
    // Multi-Window Tools
    // ========================================

    /// <summary>Lists all running preview windows.</summary>
    [McpServerTool(Name = "list_windows")]
    [Description("Lists all running preview windows with their IDs and status. " +
        "Use this to see which windows are currently open in multi-window mode.")]
    public static string ListWindows(PreviewSessionManager preview)
    {
        var running = preview.GetRunningWindowIds();
        var all = preview.GetAllWindowIds();

        if (all.Count == 0)
            return "No windows. Use compile_and_preview to create one.";

        var lines = all.Select(id =>
            $"  {id}: {(preview.IsRunning(id) ? "RUNNING" : "STOPPED")}");

        return $"{running.Count} running, {all.Count} total:\n{string.Join("\n", lines)}";
    }

    /// <summary>Closes all preview windows.</summary>
    [McpServerTool(Name = "close_all_windows")]
    [Description("Closes all running preview windows at once.")]
    public static async Task<string> CloseAllWindows(PreviewSessionManager preview)
    {
        var running = preview.GetRunningWindowIds();
        if (running.Count == 0)
            return "No windows are running.";

        await preview.StopAllAsync();
        return $"Closed {running.Count} window(s): {string.Join(", ", running)}";
    }

    /// <summary>Positions a window at specific screen coordinates.</summary>
    [McpServerTool(Name = "position_window")]
    [Description("Positions a preview window at specific screen coordinates. " +
        "Use this to arrange multiple windows on screen.")]
    public static async Task<string> PositionWindow(
        PreviewSessionManager preview,
        [Description("Window ID to position.")] string windowId,
        [Description("X coordinate (pixels from left).")] double x,
        [Description("Y coordinate (pixels from top).")] double y,
        [Description("Window width in pixels.")] double width,
        [Description("Window height in pixels.")] double height)
    {
        if (!preview.IsRunning(windowId))
            return $"Window '{windowId}' is not running.";

        await preview.PositionWindowAsync(windowId, x, y, width, height);
        return $"Window '{windowId}' positioned at ({x}, {y}) size {width}x{height}.";
    }

    /// <summary>Arranges all running windows in a predefined layout.</summary>
    [McpServerTool(Name = "layout_windows")]
    [Description("Arranges all running windows in a predefined layout. " +
        "Layouts: 'side_by_side' (2 windows, 50/50 horizontal), " +
        "'top_bottom' (2 windows, 50/50 vertical), " +
        "'left_half_right_stack' (1 large left, rest stacked right), " +
        "'grid' (equal grid arrangement).")]
    public static async Task<string> LayoutWindows(
        PreviewSessionManager preview,
        [Description("Layout preset: 'side_by_side', 'top_bottom', 'left_half_right_stack', or 'grid'.")] string layout)
    {
        var windowIds = preview.GetRunningWindowIds();
        if (windowIds.Count < 2)
            return $"Need at least 2 running windows for layout. Currently: {windowIds.Count}";

        // Use reasonable defaults for screen size
        // The windows will be positioned within these bounds
        double screenW = 1920, screenH = 1040; // leave space for taskbar
        double gap = 8;

        switch (layout.ToLowerInvariant().Replace(" ", "_").Replace("-", "_"))
        {
            case "side_by_side":
            {
                double w = (screenW - gap * (windowIds.Count + 1)) / windowIds.Count;
                for (int i = 0; i < windowIds.Count; i++)
                    await preview.PositionWindowAsync(windowIds[i],
                        gap + i * (w + gap), gap, w, screenH - gap * 2);
                break;
            }

            case "top_bottom":
            {
                double h = (screenH - gap * (windowIds.Count + 1)) / windowIds.Count;
                for (int i = 0; i < windowIds.Count; i++)
                    await preview.PositionWindowAsync(windowIds[i],
                        gap, gap + i * (h + gap), screenW - gap * 2, h);
                break;
            }

            case "left_half_right_stack":
            {
                // First window takes left half, rest stack on the right
                double leftW = (screenW - gap * 3) / 2;
                double rightW = leftW;
                double rightH = (screenH - gap * (windowIds.Count)) / (windowIds.Count - 1);

                await preview.PositionWindowAsync(windowIds[0],
                    gap, gap, leftW, screenH - gap * 2);

                for (int i = 1; i < windowIds.Count; i++)
                    await preview.PositionWindowAsync(windowIds[i],
                        leftW + gap * 2, gap + (i - 1) * (rightH + gap), rightW, rightH);
                break;
            }

            case "grid":
            {
                int cols = (int)Math.Ceiling(Math.Sqrt(windowIds.Count));
                int rows = (int)Math.Ceiling((double)windowIds.Count / cols);
                double cellW = (screenW - gap * (cols + 1)) / cols;
                double cellH = (screenH - gap * (rows + 1)) / rows;

                for (int i = 0; i < windowIds.Count; i++)
                {
                    int col = i % cols;
                    int row = i / cols;
                    await preview.PositionWindowAsync(windowIds[i],
                        gap + col * (cellW + gap), gap + row * (cellH + gap), cellW, cellH);
                }
                break;
            }

            default:
                return $"Unknown layout '{layout}'. Use: side_by_side, top_bottom, left_half_right_stack, grid.";
        }

        return $"Applied '{layout}' layout to {windowIds.Count} windows: {string.Join(", ", windowIds)}";
    }
}
