using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Neo.AssemblyForge.Slot;

/// <summary>
/// Converts SharedData values to framework-only types that any compiled fragment
/// can use without needing access to the parent assembly's custom types.
///
/// Complex objects → Dictionary&lt;string, object&gt;
/// Collections of complex objects → List&lt;Dictionary&lt;string, object&gt;&gt;
/// Simple types (string, int, double, DateTime, …) → passed through unchanged
/// </summary>
public static class SlotDataSerializer
{
    private const int MaxCollectionSize = 50_000;
    private const int MaxDepth = 3;

    public static Dictionary<string, object> Serialize(IDictionary<string, object> source)
    {
        if (source == null || source.Count == 0)
            return new Dictionary<string, object>();

        var result = new Dictionary<string, object>(source.Count);
        foreach (var kv in source)
            result[kv.Key] = SerializeValue(kv.Value, 0)!;
        return result;
    }

    private static object? SerializeValue(object? value, int depth)
    {
        if (value == null) return null;
        if (depth > MaxDepth) return value.ToString();

        var type = value.GetType();

        if (IsSimpleType(type))
            return value;

        if (value is IEnumerable enumerable && value is not string)
            return SerializeCollection(enumerable, depth);

        return SerializeObject(value, depth);
    }

    private static object SerializeCollection(IEnumerable enumerable, int depth)
    {
        var items = new List<object?>();
        bool allSimple = true;
        int count = 0;

        foreach (var item in enumerable)
        {
            items.Add(item);
            if (item != null && !IsSimpleType(item.GetType()))
                allSimple = false;
            count++;
            if (count >= MaxCollectionSize) break;
        }

        if (allSimple)
        {
            // Collection of primitives/strings — return as List<object>
            return items;
        }

        // Collection of complex types — serialize each item to a dictionary
        var dicts = new List<Dictionary<string, object>>(items.Count);
        foreach (var item in items)
        {
            if (item == null) continue;
            if (IsSimpleType(item.GetType()))
            {
                // Mixed collection — wrap simple value in a dictionary
                dicts.Add(new Dictionary<string, object> { ["Value"] = item });
            }
            else
            {
                dicts.Add(SerializeObject(item, depth + 1));
            }
        }
        return dicts;
    }

    private static Dictionary<string, object> SerializeObject(object obj, int depth)
    {
        var dict = new Dictionary<string, object>();
        var props = obj.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0);

        foreach (var prop in props)
        {
            try
            {
                var val = prop.GetValue(obj);
                dict[prop.Name] = SerializeValue(val, depth + 1)!;
            }
            catch
            {
                // Skip properties that throw on read
            }
        }
        return dict;
    }

    private static bool IsSimpleType(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive
            || t == typeof(string)
            || t == typeof(decimal)
            || t == typeof(DateTime)
            || t == typeof(DateTimeOffset)
            || t == typeof(TimeSpan)
            || t == typeof(Guid)
            || t.IsEnum;
    }
}
