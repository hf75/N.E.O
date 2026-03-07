using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Neo.AssemblyForge.Slot;

/// <summary>
/// Builds a human-readable description of data stored in a DynamicSlot's SharedData dictionary.
/// This description is appended to the user prompt so the slot AI knows what data is available.
/// </summary>
public static class SlotDataDescriber
{
    private const int MaxCollectionCount = 10_000;
    private const int MaxSampleItems = 1;
    private const int MaxProperties = 15;
    private const int MaxStringLength = 100;

    public static string Describe(IDictionary<string, object> data)
    {
        if (data == null || data.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("AVAILABLE DATA: Your UserControl's DataContext is set to a Dictionary<string, object> with these entries:");
        sb.AppendLine("Access pattern: var data = (System.Collections.Generic.Dictionary<string, object>)DataContext;");
        sb.AppendLine();

        foreach (var kv in data)
        {
            if (kv.Value == null)
            {
                sb.AppendLine($"  \"{kv.Key}\": null");
                continue;
            }

            var val = kv.Value;
            var type = val.GetType();

            if (val is string s)
            {
                var display = s.Length > MaxStringLength ? s[..MaxStringLength] + "..." : s;
                sb.AppendLine($"  \"{kv.Key}\" (string): \"{display}\"");
            }
            else if (IsNumericOrBool(type))
            {
                sb.AppendLine($"  \"{kv.Key}\" ({FriendlyName(type)}): {val}");
            }
            else if (val is IEnumerable enumerable)
            {
                DescribeCollection(sb, kv.Key, enumerable, type);
            }
            else
            {
                DescribeObject(sb, kv.Key, val, type);
            }
        }

        sb.AppendLine();
        sb.AppendLine("Use this data to fulfill the user's request. Cast values from the dictionary with the appropriate types.");
        return sb.ToString();
    }

    private static void DescribeCollection(StringBuilder sb, string key, IEnumerable enumerable, Type collectionType)
    {
        var items = new List<object>();
        int count = 0;
        foreach (var item in enumerable)
        {
            if (item != null && items.Count < MaxSampleItems)
                items.Add(item);
            count++;
            if (count >= MaxCollectionCount) break;
        }

        var capped = count >= MaxCollectionCount ? "+" : "";
        var elemType = GetElementType(collectionType);
        var elemName = elemType != null ? FriendlyName(elemType) : "object";

        sb.AppendLine($"  \"{key}\" (collection of {elemName}, {count}{capped} items):");

        if (elemType != null && !IsSimpleType(elemType) && items.Count > 0)
        {
            var props = elemType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead).Take(MaxProperties).ToArray();

            sb.Append("    Properties: ");
            sb.AppendLine(string.Join(", ", props.Select(p => $"{p.Name} ({FriendlyName(p.PropertyType)})")));

            foreach (var sample in items)
            {
                sb.Append("    Sample: { ");
                var parts = new List<string>();
                foreach (var p in props.Take(6))
                {
                    try
                    {
                        var pv = p.GetValue(sample);
                        parts.Add($"{p.Name}={FormatValue(pv)}");
                    }
                    catch { /* skip unreadable */ }
                }
                sb.Append(string.Join(", ", parts));
                sb.AppendLine(" }");
            }
        }
        else if (IsSimpleType(elemType ?? typeof(object)) && items.Count > 0)
        {
            sb.AppendLine($"    Sample values: {string.Join(", ", items.Take(5).Select(FormatValue))}");
        }
    }

    private static void DescribeObject(StringBuilder sb, string key, object val, Type type)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead).Take(MaxProperties).ToArray();

        sb.AppendLine($"  \"{key}\" ({FriendlyName(type)}):");
        if (props.Length > 0)
        {
            sb.Append("    Properties: ");
            sb.AppendLine(string.Join(", ", props.Select(p => $"{p.Name} ({FriendlyName(p.PropertyType)})")));
        }
    }

    private static Type? GetElementType(Type collectionType)
    {
        if (collectionType.IsArray)
            return collectionType.GetElementType();

        foreach (var iface in collectionType.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return iface.GetGenericArguments()[0];
        }
        return null;
    }

    private static string FriendlyName(Type type)
    {
        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(double)) return "double";
        if (type == typeof(float)) return "float";
        if (type == typeof(decimal)) return "decimal";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(DateTime)) return "DateTime";
        if (type == typeof(DateTimeOffset)) return "DateTimeOffset";
        if (type == typeof(Guid)) return "Guid";
        if (type == typeof(byte)) return "byte";
        if (type == typeof(short)) return "short";
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return FriendlyName(underlying) + "?";
        return type.Name;
    }

    private static bool IsSimpleType(Type type)
        => type.IsPrimitive || type == typeof(string) || type == typeof(decimal)
           || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(Guid);

    private static bool IsNumericOrBool(Type type)
        => type == typeof(int) || type == typeof(long) || type == typeof(double)
           || type == typeof(float) || type == typeof(decimal) || type == typeof(bool)
           || type == typeof(byte) || type == typeof(short);

    private static string FormatValue(object? val)
    {
        if (val == null) return "null";
        if (val is string s) return $"\"{(s.Length > 50 ? s[..50] + "..." : s)}\"";
        if (val is DateTime dt) return dt.ToString("yyyy-MM-dd");
        return val.ToString() ?? "null";
    }
}
