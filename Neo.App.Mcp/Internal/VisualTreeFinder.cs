using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Neo.App.Mcp.Internal;

/// <summary>
/// Locate a control inside a loaded UserControl's visual tree by name (preferred), then by
/// Type[:Index], then by partial-name. Same lookup semantics as
/// <c>Neo.PluginWindowAvalonia.MCP.App.FindControlInTree</c> so user code that targets
/// controls by name keeps working unchanged when the app is exported to Frozen-Mode.
/// </summary>
internal static class VisualTreeFinder
{
    public static Control? Find(Control root, string target)
    {
        var all = new List<Control>();
        Collect(root, all);

        var byName = all.FirstOrDefault(c => string.Equals(c.Name, target, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName;

        var parts = target.Split(':', 2);
        var typeName = parts[0];
        int index = parts.Length > 1 && int.TryParse(parts[1], out var idx) ? idx : 0;
        var byType = all.Where(c => c.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (index < byType.Count) return byType[index];
        if (byType.Count > 0) return byType[0];

        return all.FirstOrDefault(c => c.Name != null &&
            c.Name.Contains(target, StringComparison.OrdinalIgnoreCase));
    }

    private static void Collect(Visual v, List<Control> result)
    {
        if (v is Control c) result.Add(c);
        foreach (var child in v.GetVisualChildren())
            if (child is Visual vc) Collect(vc, result);
    }
}
