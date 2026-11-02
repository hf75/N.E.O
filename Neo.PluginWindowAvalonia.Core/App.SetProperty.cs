using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Neo.IPC;
using Neo.Shared;

namespace Neo.PluginWindowAvalonia
{
    public partial class App
    {
        // =======================================================
        // Live Property Editing (no recompile)
        // =======================================================
        private SetPropertyResultMessage SetPropertyOnControl(SetPropertyRequest req)
        {
            // Find the loaded UserControl content
            var root = MainWin.GetLoadedUserControl();
            if (root == null)
                return new SetPropertyResultMessage(false, "No control loaded.");

            // Find target control(s) in visual tree
            var target = FindControlInTree(root, req.Target);
            if (target == null)
                return new SetPropertyResultMessage(false, $"Control '{req.Target}' not found in visual tree.");

            // Find the AvaloniaProperty
            var avProp = FindAvaloniaProperty(target, req.PropertyName);
            if (avProp == null)
            {
                // Fallback: try CLR property via reflection
                var clrProp = target.GetType().GetProperty(req.PropertyName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (clrProp != null && clrProp.CanWrite)
                {
                    var oldVal = clrProp.GetValue(target)?.ToString();
                    var newVal = ParsePropertyValue(clrProp.PropertyType, req.Value);
                    clrProp.SetValue(target, newVal);
                    return new SetPropertyResultMessage(true, $"Set {clrProp.Name} via reflection.",
                        oldVal, newVal?.ToString());
                }
                return new SetPropertyResultMessage(false, $"Property '{req.PropertyName}' not found on {target.GetType().Name}.");
            }

            // Parse and set value
            var oldValue = target.GetValue(avProp)?.ToString();
            var parsed = ParsePropertyValue(avProp.PropertyType, req.Value);
            target.SetValue(avProp, parsed);
            var newValue = target.GetValue(avProp)?.ToString();

            return new SetPropertyResultMessage(true,
                $"Set {target.GetType().Name}.{avProp.Name} = {req.Value}",
                oldValue, newValue);
        }

        private static AvaloniaProperty? FindAvaloniaProperty(Avalonia.Controls.Control control, string propertyName)
        {
            // Search registered Avalonia properties on the control type
            var props = AvaloniaPropertyRegistry.Instance.GetRegistered(control.GetType());
            return props.FirstOrDefault(p => p.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A value from an app link for a property: as a number or text where it is one, else as written, like a colour.</summary>
        internal static object? ToPropertyValue(string json, Type type)
        {
            var plain = Nullable.GetUnderlyingType(type) ?? type;
            if (plain == typeof(string) || plain == typeof(object) || plain.IsPrimitive || plain == typeof(decimal) || plain.IsEnum)
                return Neo.App.LinkValue.Convert(json, type);

            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? ParsePropertyValue(plain, document.RootElement.GetString()!)
                : Neo.App.LinkValue.Convert(json, type);
        }

        private static object? ParsePropertyValue(Type targetType, string value)
        {
            // IBrush / Color
            if (targetType == typeof(Avalonia.Media.IBrush) || targetType == typeof(Avalonia.Media.Brush)
                || targetType == typeof(Avalonia.Media.ISolidColorBrush))
            {
                if (Avalonia.Media.Color.TryParse(value, out var color))
                    return new Avalonia.Media.SolidColorBrush(color);
                // Try named brush via reflection (e.g. "Red" → Brushes.Red)
                var brushProp = typeof(Avalonia.Media.Brushes).GetProperty(value,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase);
                if (brushProp != null) return brushProp.GetValue(null);
                return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(value));
            }

            // Thickness
            if (targetType == typeof(Avalonia.Thickness))
            {
                var nums = value.Split(',').Select(s => double.Parse(s.Trim())).ToArray();
                return nums.Length switch
                {
                    1 => new Avalonia.Thickness(nums[0]),
                    2 => new Avalonia.Thickness(nums[0], nums[1]),
                    4 => new Avalonia.Thickness(nums[0], nums[1], nums[2], nums[3]),
                    _ => new Avalonia.Thickness(nums[0])
                };
            }

            // CornerRadius
            if (targetType == typeof(Avalonia.CornerRadius))
            {
                var nums = value.Split(',').Select(s => double.Parse(s.Trim())).ToArray();
                return nums.Length switch
                {
                    1 => new Avalonia.CornerRadius(nums[0]),
                    4 => new Avalonia.CornerRadius(nums[0], nums[1], nums[2], nums[3]),
                    _ => new Avalonia.CornerRadius(nums[0])
                };
            }

            // FontWeight
            if (targetType == typeof(Avalonia.Media.FontWeight))
            {
                if (Enum.TryParse<Avalonia.Media.FontWeight>(value, true, out var fw))
                    return fw;
            }

            // Enum
            if (targetType.IsEnum)
            {
                if (Enum.TryParse(targetType, value, true, out var enumVal))
                    return enumVal;
            }

            // Nullable<T>
            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
                return ParsePropertyValue(underlying, value);

            // Primitives
            if (targetType == typeof(double)) return double.Parse(value);
            if (targetType == typeof(float)) return float.Parse(value);
            if (targetType == typeof(int)) return int.Parse(value);
            if (targetType == typeof(bool)) return bool.Parse(value);
            if (targetType == typeof(string)) return value;

            // Last resort: TypeConverter
            var converter = System.ComponentModel.TypeDescriptor.GetConverter(targetType);
            if (converter.CanConvertFrom(typeof(string)))
                return converter.ConvertFromString(value);

            return value;
        }
    }
}
