using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

using Neo.IPC;

namespace Neo.PluginWindowAvalonia
{
    public partial class MainWindow : Window
    {
        private const string DesignNamePrefix = "__neo_";
        private const string DesignTagPrefix = "__neo:id=";

        private bool _designerModeEnabled;
        private Control? _selectedElement;
        private string? _selectedDesignId;
        private bool _designerHooked;

        public void SetDesignerMode(bool enabled)
        {
            _designerModeEnabled = enabled;

            if (enabled && !_designerHooked)
            {
                dynamicContent.AddHandler(InputElement.PointerPressedEvent, DynamicContent_PointerPressed, RoutingStrategies.Tunnel);
                _designerHooked = true;
            }

            if (!enabled)
                ClearSelection();
        }

        private void DynamicContent_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!_designerModeEnabled)
                return;

            if (TryGetDesignIdElement(e.Source as Visual, out var element, out var designId))
            {
                e.Handled = true;
                SelectElement(element, designId);
                _ = SendSelectionAsync(element, designId);
            }
        }

        private void SelectElement(Control element, string designId)
        {
            _selectedElement = element;
            _selectedDesignId = designId;
            UpdateSelectionOverlay();
        }

        private void ClearSelection()
        {
            _selectedElement = null;
            _selectedDesignId = null;
            SelectionBorder.IsVisible = false;
        }

        private void UpdateSelectionOverlay()
        {
            if (!_designerModeEnabled || _selectedElement == null)
            {
                SelectionBorder.IsVisible = false;
                return;
            }

            try
            {
                var p = _selectedElement.TranslatePoint(new Point(0, 0), SelectionOverlay);
                if (p == null)
                {
                    SelectionBorder.IsVisible = false;
                    return;
                }

                Canvas.SetLeft(SelectionBorder, p.Value.X);
                Canvas.SetTop(SelectionBorder, p.Value.Y);
                SelectionBorder.Width = Math.Max(0, _selectedElement.Bounds.Width);
                SelectionBorder.Height = Math.Max(0, _selectedElement.Bounds.Height);
                SelectionBorder.IsVisible = true;
            }
            catch
            {
                SelectionBorder.IsVisible = false;
            }
        }

        private static bool TryGetDesignIdElement(Visual? start, out Control element, out string designId)
        {
            element = null!;
            designId = string.Empty;

            Visual? current = start;
            while (current != null)
            {
                if (current is Control c)
                {
                    if (!string.IsNullOrWhiteSpace(c.Name) &&
                        c.Name.StartsWith(DesignNamePrefix, StringComparison.Ordinal))
                    {
                        element = c;
                        designId = c.Name;
                        return true;
                    }

                    if (c.Tag is string tag &&
                        tag.StartsWith(DesignTagPrefix, StringComparison.Ordinal))
                    {
                        element = c;
                        designId = tag;
                        return true;
                    }
                }

                current = current.GetVisualParent();
            }

            return false;
        }

        private Task SendSelectionAsync(Control element, string designId)
        {
            if (Application.Current is not App app)
                return Task.CompletedTask;

            var props = ExtractDesignerProperties(element);
            var typeName = element.GetType().FullName ?? element.GetType().Name;
            return app.NotifyParentDesignerSelection(new DesignerSelectionMessage(designId, typeName, props));
        }

        private static Dictionary<string, string> ExtractDesignerProperties(Control element)
        {
            var props = new Dictionary<string, string>(StringComparer.Ordinal);

            AddProperty(props, element, "Text", v => v as string);
            AddProperty(props, element, "Content", v => v as string);

            AddProperty(props, element, "FontFamily", v => v?.ToString());
            AddProperty(props, element, "FontSize", v => v is double d ? d.ToString(CultureInfo.InvariantCulture) : null);
            AddProperty(props, element, "FontWeight", v => v?.ToString());
            AddProperty(props, element, "FontStyle", v => v?.ToString());

            AddProperty(props, element, "Foreground", v => v is IBrush b ? BrushToHex(b) : null);
            AddProperty(props, element, "Background", v => v is IBrush b ? BrushToHex(b) : null);

            AddProperty(props, element, "Margin", v => v is Thickness t ? ThicknessToString(t) : null);
            AddProperty(props, element, "Padding", v => v is Thickness t ? ThicknessToString(t) : null);

            return props;
        }

        private static void AddProperty(
            IDictionary<string, string> props,
            Control element,
            string propertyName,
            Func<object?, string?> converter)
        {
            try
            {
                var pi = element.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                if (pi == null || !pi.CanRead)
                    return;

                string? value = null;
                try
                {
                    value = converter(pi.GetValue(element));
                }
                catch
                {
                    // ignore conversion failures
                }

                props[propertyName] = value ?? string.Empty;
            }
            catch
            {
                // ignore reflection failures
            }
        }

        private static string? BrushToHex(IBrush brush)
        {
            if (brush is SolidColorBrush scb)
            {
                var c = scb.Color;
                return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            return null;
        }

        private static string ThicknessToString(Thickness t)
            => string.Join(",",
                t.Left.ToString(CultureInfo.InvariantCulture),
                t.Top.ToString(CultureInfo.InvariantCulture),
                t.Right.ToString(CultureInfo.InvariantCulture),
                t.Bottom.ToString(CultureInfo.InvariantCulture));
    }
}
