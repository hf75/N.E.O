using Neo.IPC;
using Neo.Shared;
using System.Windows.Threading;
using System.Windows;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Reflection;
using System.Globalization;

namespace Neo.PluginWindowWPF
{
    public partial class App
    {
        // =======================================================
        // Live Property Editing (WPF)
        // =======================================================
        private SetPropertyResultMessage SetPropertyOnControl(SetPropertyRequest req)
        {
            var root = MainWin.GetLoadedUserControl();
            if (root == null)
                return new SetPropertyResultMessage(false, "No control loaded.");

            var target = FindControlInTree(root, req.Target);
            if (target == null)
                return new SetPropertyResultMessage(false, $"Control '{req.Target}' not found in visual tree.");

            // Try DependencyProperty first
            var dp = FindDependencyProperty(target, req.PropertyName);
            if (dp != null)
            {
                var oldVal = target.GetValue(dp)?.ToString();
                var newVal = ParsePropertyValue(dp.PropertyType, req.Value);
                target.SetValue(dp, newVal);
                return new SetPropertyResultMessage(true,
                    $"Set {target.GetType().Name}.{dp.Name} = {req.Value}",
                    oldVal, target.GetValue(dp)?.ToString());
            }

            // Fallback: CLR property via reflection
            var clrProp = target.GetType().GetProperty(req.PropertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
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

        private static DependencyProperty? FindDependencyProperty(DependencyObject obj, string propertyName)
        {
            var fieldName = propertyName + "Property";
            var field = obj.GetType().GetField(fieldName,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            return field?.GetValue(null) as DependencyProperty;
        }

        private static object? ParsePropertyValue(Type targetType, string value)
        {
            if (targetType == typeof(Brush) || targetType == typeof(SolidColorBrush))
            {
                try
                {
                    var converter = new BrushConverter();
                    return converter.ConvertFromString(value);
                }
                catch { }
            }

            if (targetType == typeof(Thickness))
            {
                var nums = value.Split(',').Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
                return nums.Length switch
                {
                    1 => new Thickness(nums[0]),
                    2 => new Thickness(nums[0], nums[1], nums[0], nums[1]),
                    4 => new Thickness(nums[0], nums[1], nums[2], nums[3]),
                    _ => new Thickness(nums[0])
                };
            }

            if (targetType == typeof(CornerRadius))
            {
                var nums = value.Split(',').Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
                return nums.Length switch
                {
                    1 => new CornerRadius(nums[0]),
                    4 => new CornerRadius(nums[0], nums[1], nums[2], nums[3]),
                    _ => new CornerRadius(nums[0])
                };
            }

            if (targetType == typeof(FontWeight))
            {
                var converter = new FontWeightConverter();
                try { return converter.ConvertFromString(value); } catch { }
            }

            if (targetType.IsEnum)
            {
                if (Enum.TryParse(targetType, value, true, out var enumVal))
                    return enumVal;
            }

            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
                return ParsePropertyValue(underlying, value);

            if (targetType == typeof(double)) return double.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(float)) return float.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return bool.Parse(value);
            if (targetType == typeof(string)) return value;

            var tc = System.ComponentModel.TypeDescriptor.GetConverter(targetType);
            if (tc.CanConvertFrom(typeof(string)))
                return tc.ConvertFromString(value);

            return value;
        }
    }
}
