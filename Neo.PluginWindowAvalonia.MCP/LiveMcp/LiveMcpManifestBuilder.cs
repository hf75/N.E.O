using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Neo.IPC;

namespace Neo.PluginWindowAvalonia.MCP.LiveMcp;

/// <summary>
/// Reflects over a loaded <see cref="Avalonia.Controls.UserControl"/> and produces
/// an <see cref="AppManifestMessage"/> describing the methods, observable properties,
/// and triggerable controls the user code has annotated with the
/// <c>Neo.App.McpCallable</c> / <c>McpObservable</c> / <c>McpTriggerable</c> attributes.
///
/// Done once at plugin-load (manifest-frozen-on-load per VISION.md Spielregel #2).
/// </summary>
internal static class LiveMcpManifestBuilder
{
    // Attribute type lookup: matched by FullName so we don't need a hard reference at
    // compile time (the UserControl carries its own [McpCallable]-decorated members
    // and the attributes come from Neo.App.Api which is referenced in user code).
    private const string CallableAttrName    = "Neo.App.McpCallableAttribute";
    private const string ObservableAttrName  = "Neo.App.McpObservableAttribute";
    private const string TriggerableAttrName = "Neo.App.McpTriggerableAttribute";

    public static AppManifestMessage Build(string appId, Control userControl)
    {
        var type = userControl.GetType();
        return new AppManifestMessage(
            AppId: appId,
            ClassFullName: type.FullName ?? type.Name,
            Callables: BuildCallables(type),
            Observables: BuildObservables(type),
            Triggerables: BuildTriggerables(type, userControl));
    }

    private static List<McpCallableEntry> BuildCallables(Type type)
    {
        var result = new List<McpCallableEntry>();
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        foreach (var m in methods)
        {
            var attr = ReadAttribute(m, CallableAttrName);
            if (attr == null) continue;

            var description = ReadStringProperty(attr, "Description") ?? "";
            var offUiThread = ReadBoolProperty(attr, "OffUiThread") ?? false;
            var timeoutSeconds = ReadInt32Property(attr, "TimeoutSeconds") ?? 30;

            var pars = m.GetParameters()
                .Select(p => new McpParamEntry(p.Name ?? "_", p.ParameterType.FullName ?? p.ParameterType.Name))
                .ToList();

            result.Add(new McpCallableEntry(
                Name: m.Name,
                Description: description,
                Parameters: pars,
                ReturnTypeName: m.ReturnType.FullName ?? m.ReturnType.Name,
                OffUiThread: offUiThread,
                TimeoutSeconds: timeoutSeconds));
        }
        return result;
    }

    private static List<McpObservableEntry> BuildObservables(Type type)
    {
        var result = new List<McpObservableEntry>();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        foreach (var p in props)
        {
            var attr = ReadAttribute(p, ObservableAttrName);
            if (attr == null) continue;
            if (!p.CanRead) continue;

            var description = ReadStringProperty(attr, "Description") ?? "";
            var watchable = ReadBoolProperty(attr, "Watchable") ?? false;

            result.Add(new McpObservableEntry(
                Name: p.Name,
                Description: description,
                TypeName: p.PropertyType.FullName ?? p.PropertyType.Name,
                Watchable: watchable));
        }
        return result;
    }

    private static List<McpTriggerableEntry> BuildTriggerables(Type type, object instance)
    {
        var result = new List<McpTriggerableEntry>();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        foreach (var p in props)
        {
            var attr = ReadAttribute(p, TriggerableAttrName);
            if (attr == null) continue;
            if (!p.CanRead) continue;

            var description = ReadStringProperty(attr, "Description") ?? "";

            // Resolve actual control name (Avalonia x:Name) at manifest-build time
            // so Phase 3's simulate_input/raise_event have a stable target. Best-effort:
            // if the property getter throws or returns null at this stage, we fall back
            // to the property name and ControlType "Unknown".
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
            catch { /* manifest-build is best-effort; runtime resolution happens later */ }

            result.Add(new McpTriggerableEntry(
                Name: p.Name,
                Description: description,
                ControlName: controlName,
                ControlType: controlType));
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Attribute helpers — match by type FullName so Neo.App.Api doesn't need a
    // hard reference here. The user-loaded assembly carries its own copy of the
    // attribute types via its Neo.App.Api reference.
    // ─────────────────────────────────────────────────────────────────────────

    private static object? ReadAttribute(MemberInfo member, string fullName)
    {
        foreach (var a in member.GetCustomAttributes(inherit: false))
            if (a.GetType().FullName == fullName) return a;
        return null;
    }

    private static string? ReadStringProperty(object attr, string name) =>
        attr.GetType().GetProperty(name)?.GetValue(attr) as string;

    private static bool? ReadBoolProperty(object attr, string name) =>
        attr.GetType().GetProperty(name)?.GetValue(attr) as bool?;

    private static int? ReadInt32Property(object attr, string name) =>
        attr.GetType().GetProperty(name)?.GetValue(attr) as int?;
}
