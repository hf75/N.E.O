using System.Reflection;
using Avalonia.Controls;

namespace Neo.App.Mcp.Internal;

/// <summary>
/// Reflects over a loaded <see cref="Control"/> and produces an <see cref="AppManifest"/>.
/// Mirrors the Dev-Mode <c>LiveMcpManifestBuilder</c> in <c>Neo.PluginWindowAvalonia.MCP</c> —
/// same attribute-by-FullName matching so the same user code produces the same manifest in both
/// Dev-Mode and Frozen-Mode (M4.4 consistency guarantee).
/// </summary>
internal static class AppManifestBuilder
{
    private const string CallableAttrName    = "Neo.App.McpCallableAttribute";
    private const string ObservableAttrName  = "Neo.App.McpObservableAttribute";
    private const string TriggerableAttrName = "Neo.App.McpTriggerableAttribute";

    public static AppManifest Build(Control userControl)
    {
        var type = userControl.GetType();
        return new AppManifest(
            ClassFullName: type.FullName ?? type.Name,
            Callables: BuildCallables(type),
            Observables: BuildObservables(type),
            Triggerables: BuildTriggerables(type, userControl));
    }

    private static List<CallableEntry> BuildCallables(Type type)
    {
        var result = new List<CallableEntry>();
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var attr = ReadAttr(m, CallableAttrName);
            if (attr == null) continue;

            var description = ReadString(attr, "Description") ?? "";
            var offUi = ReadBool(attr, "OffUiThread") ?? false;
            var timeout = ReadInt(attr, "TimeoutSeconds") ?? 30;

            var pars = m.GetParameters()
                .Select(p => new ParamEntry(p.Name ?? "_", p.ParameterType.FullName ?? p.ParameterType.Name))
                .ToList();

            result.Add(new CallableEntry(
                Name: m.Name,
                Description: description,
                Parameters: pars,
                ReturnTypeName: m.ReturnType.FullName ?? m.ReturnType.Name,
                OffUiThread: offUi,
                TimeoutSeconds: timeout));
        }
        return result;
    }

    private static List<ObservableEntry> BuildObservables(Type type)
    {
        var result = new List<ObservableEntry>();
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var attr = ReadAttr(p, ObservableAttrName);
            if (attr == null || !p.CanRead) continue;

            result.Add(new ObservableEntry(
                Name: p.Name,
                Description: ReadString(attr, "Description") ?? "",
                TypeName: p.PropertyType.FullName ?? p.PropertyType.Name,
                Watchable: ReadBool(attr, "Watchable") ?? false));
        }
        return result;
    }

    private static List<TriggerableEntry> BuildTriggerables(Type type, object instance)
    {
        var result = new List<TriggerableEntry>();
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var attr = ReadAttr(p, TriggerableAttrName);
            if (attr == null || !p.CanRead) continue;

            string controlName = p.Name;
            string controlType = "Unknown";
            try
            {
                if (p.GetValue(instance) is Control ctrl)
                {
                    if (!string.IsNullOrEmpty(ctrl.Name)) controlName = ctrl.Name;
                    controlType = ctrl.GetType().Name;
                }
            }
            catch { /* getter throws → keep fallbacks */ }

            result.Add(new TriggerableEntry(
                Name: p.Name,
                Description: ReadString(attr, "Description") ?? "",
                ControlName: controlName,
                ControlType: controlType));
        }
        return result;
    }

    // FullName-based attribute lookup so we don't hard-reference Neo.App.Api types here —
    // the user code carries its own copy via its Neo.App.Api project reference.

    private static object? ReadAttr(MemberInfo member, string fullName)
    {
        foreach (var a in member.GetCustomAttributes(inherit: false))
            if (a.GetType().FullName == fullName) return a;
        return null;
    }

    private static string? ReadString(object attr, string name) =>
        attr.GetType().GetProperty(name)?.GetValue(attr) as string;

    private static bool? ReadBool(object attr, string name) =>
        attr.GetType().GetProperty(name)?.GetValue(attr) as bool?;

    private static int? ReadInt(object attr, string name) =>
        attr.GetType().GetProperty(name)?.GetValue(attr) as int?;
}
