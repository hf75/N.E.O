using System.Reflection;
using FluentAssertions;
using ModelContextProtocol.Server;
using Neo.McpServer.Tools;
using Xunit;

namespace Neo.McpServer.Tests;

/// <summary>
/// The static tool surface is discovered by reflection (<c>WithToolsFromAssembly</c>), and the tool
/// classes are split over several partial files. This pins the set down, so a tool that gets lost
/// in a refactoring — or added without updating the docs — shows up here.
/// </summary>
public class ToolSurfaceTests
{
    private static readonly string[] ExpectedTools =
    [
        // preview lifecycle
        "compile_and_preview", "update_preview", "patch_preview", "close_preview", "get_preview_status",
        // inspection
        "capture_screenshot", "get_runtime_errors", "inspect_visual_tree", "extract_code", "run_test",
        // live editing
        "set_property", "inject_data", "read_data",
        // web bridge
        "start_web_bridge", "send_to_web", "stop_web_bridge",
        // sessions, skills, export
        "save_session", "load_session", "register_skill", "unregister_skill", "export_app",
        // multi-window
        "list_windows", "close_all_windows", "position_window", "layout_windows",
        // Live-MCP
        "inspect_app_api", "invoke_method", "read_observable", "raise_event", "simulate_input",
        // app network
        "list_running_apps", "call_running_app", "read_running_app",
        // other programs and their interfaces
        "inspect_foreign_program",
        // the Claude bridge (desktop app)
        "start_claude_bridge",
    ];

    [Fact]
    public void Static_tool_set_is_exactly_the_documented_one()
    {
        var discovered = typeof(PreviewTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name != null)
            .ToList();

        discovered.Should().OnlyHaveUniqueItems();
        discovered.Should().BeEquivalentTo(ExpectedTools);
    }
}
