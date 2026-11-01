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
    /// Captures a screenshot of the live preview window. Returns a Base64-encoded PNG image.
    /// Claude can use this to SEE what the app looks like and suggest visual improvements.
    /// </summary>
    [McpServerTool(Name = "capture_screenshot")]
    [Description("Captures a screenshot of the running preview window and returns it as a PNG image. " +
        "Use this to SEE what the generated app looks like and suggest visual improvements. " +
        "The preview must be running (call compile_and_preview first). " +
        "The image is rendered by Avalonia/WPF itself, so the content of an embedded native web view " +
        "(NativeWebView, WebView2) shows up as an empty area — check those through the app's own state instead.")]
    public static async Task<IEnumerable<ContentBlock>> CaptureScreenshot(
        PreviewSessionManager preview,
        [Description("Window ID for multi-window mode. Use different IDs to create multiple windows. " +
            "Omit for single-window mode (uses default window).")] string? windowId = null)
    {
        if (!preview.IsRunning(windowId))
            return [new TextContentBlock { Text = "No preview window is running. Call compile_and_preview first." }];

        var result = await preview.CaptureScreenshotAsync(windowId: windowId);
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
    public static string GetRuntimeErrors(
        PreviewSessionManager preview,
        [Description("Window ID for multi-window mode. Use different IDs to create multiple windows. " +
            "Omit for single-window mode (uses default window).")] string? windowId = null)
    {
        if (!preview.IsRunning(windowId))
            return "No preview is running.";

        if (preview.GetRuntimeErrors(windowId).Count == 0)
            return "No runtime errors. The app is running cleanly.";

        return $"RUNTIME ERRORS ({preview.GetRuntimeErrors(windowId).Count}):\n\n" +
               string.Join("\n---\n", preview.GetRuntimeErrors(windowId)) +
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
    public static async Task<string> InspectVisualTree(
        PreviewSessionManager preview,
        [Description("Window ID for multi-window mode. Use different IDs to create multiple windows. " +
            "Omit for single-window mode (uses default window).")] string? windowId = null)
    {
        if (!preview.IsRunning(windowId))
            return "No preview is running. Call compile_and_preview first.";

        var json = await preview.InspectVisualTreeAsync(windowId: windowId);
        if (json == null)
            return "Failed to inspect visual tree. The preview window may not be visible.";

        return json;
    }

    /// <summary>
    /// The real source of the running app, including what Smart Edit changed inside the window. Only on
    /// request the UI rebuilt from its current state, which has none of the app's logic in it.
    /// </summary>
    [McpServerTool(Name = "extract_code")]
    [Description("Returns the C# source of the running app: what was last compiled, including changes the user made with Smart Edit (Ctrl+K). " +
        "Start every code change from this; patch_preview patches exactly this text. " +
        "An app of several files comes back with a '// ===== File n of m =====' line before each file; pass them to update_preview as separate entries. " +
        "Changes made with set_property and inject_data only exist in the running app and are not in the source. " +
        "To see those as code, set fromLiveUi.")]
    public static async Task<string> ExtractCode(
        PreviewSessionManager preview,
        [Description("Window ID for multi-window mode. Use different IDs to create multiple windows. " +
            "Omit for single-window mode (uses default window).")] string? windowId = null,
        [Description("Avalonia: rebuild the UI from its current state instead, including set_property and inject_data changes. " +
            "That is layout and property values with NO event handlers, fields or methods: copy the values you need into the source, " +
            "never compile it over an app that has behaviour. WPF cannot do this and returns the source.")] bool fromLiveUi = false)
    {
        if (!fromLiveUi && preview.GetSourceCode(windowId) is { Count: > 0 } source)
            return source.Count == 1 ? source[0]
                : string.Join("\n", source.Select((file, i) => $"// ===== File {i + 1} of {source.Count} =====\n{file}"));

        if (!preview.IsRunning(windowId))
            return "No preview is running. Call compile_and_preview first.";

        var code = await preview.ExtractCodeAsync(windowId: windowId);
        if (code == null)
            return "Failed to extract code. The preview window may not be visible.";

        return code;
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
            "{\"target\":\"submitBtn\",\"property\":\"IsEnabled\",\"expected\":\"true\"}]'")] string assertions,
        [Description("Window ID for multi-window mode.")] string? windowId = null)
    {
        if (!preview.IsRunning(windowId))
            return "No preview is running. Call compile_and_preview first.";

        // Get the visual tree
        var treeJson = await preview.InspectVisualTreeAsync(windowId: windowId);
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
}
