using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Neo.IPC;

namespace Neo.PluginWindowWPF.MCP
{
    public partial class MainWindow : Window
    {
        private const string DesignNamePrefix = "__neo_";
        private const string DesignTagPrefix = "__neo:id=";

        private bool _designerModeEnabled;
        private FrameworkElement? _selectedElement;
        private string? _selectedDesignId;
        private bool _designerHooked;

        public void SetDesignerMode(bool enabled)
        {
            _designerModeEnabled = enabled;

            if (enabled && !_designerHooked)
            {
                dynamicContent.PreviewMouseLeftButtonDown += DynamicContent_PreviewMouseLeftButtonDown;
                dynamicContent.LayoutUpdated += (_, _) =>
                {
                    if (_designerModeEnabled && _selectedElement != null)
                        UpdateSelectionOverlay();
                };
                _designerHooked = true;
            }

            if (!enabled)
                ClearSelection();
        }

        private void DynamicContent_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_designerModeEnabled)
                return;

            if (TryGetDesignIdElement(e.OriginalSource as DependencyObject, out var element, out var designId))
            {
                e.Handled = true;
                SelectElement(element, designId);
                _ = SendSelectionAsync(element, designId);
            }
        }

        private void SelectElement(FrameworkElement element, string designId)
        {
            _selectedElement = element;
            _selectedDesignId = designId;
            UpdateSelectionOverlay();
        }

        private void ClearSelection()
        {
            _selectedElement = null;
            _selectedDesignId = null;
            SelectionBorder.Visibility = Visibility.Collapsed;
        }

        private void UpdateSelectionOverlay()
        {
            if (!_designerModeEnabled || _selectedElement == null)
            {
                SelectionBorder.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                if (!SelectionOverlay.IsLoaded)
                    return;

                var transform = _selectedElement.TransformToVisual(SelectionOverlay);
                var rect = transform.TransformBounds(new Rect(new Point(0, 0), _selectedElement.RenderSize));

                Canvas.SetLeft(SelectionBorder, rect.Left);
                Canvas.SetTop(SelectionBorder, rect.Top);
                SelectionBorder.Width = Math.Max(0, rect.Width);
                SelectionBorder.Height = Math.Max(0, rect.Height);
                SelectionBorder.Visibility = Visibility.Visible;
            }
            catch
            {
                SelectionBorder.Visibility = Visibility.Collapsed;
            }
        }

        private static bool TryGetDesignIdElement(DependencyObject? start, out FrameworkElement element, out string designId)
        {
            element = null!;
            designId = string.Empty;

            DependencyObject? current = start;
            while (current != null)
            {
                if (current is FrameworkElement fe)
                {
                    if (!string.IsNullOrWhiteSpace(fe.Name) &&
                        fe.Name.StartsWith(DesignNamePrefix, StringComparison.Ordinal))
                    {
                        element = fe;
                        designId = fe.Name;
                        return true;
                    }

                    if (fe.Tag is string tag &&
                        tag.StartsWith(DesignTagPrefix, StringComparison.Ordinal))
                    {
                        element = fe;
                        designId = tag;
                        return true;
                    }
                }

                current = GetParent(current);
            }

            return false;
        }

        private static DependencyObject? GetParent(DependencyObject current)
        {
            if (current is FrameworkContentElement fce)
                return fce.Parent;
            return VisualTreeHelper.GetParent(current);
        }

        private Task SendSelectionAsync(FrameworkElement element, string designId)
        {
            if (Application.Current is not App app)
                return Task.CompletedTask;

            var props = ExtractDesignerProperties(element);
            var typeName = element.GetType().FullName ?? element.GetType().Name;
            return app.NotifyParentDesignerSelection(new DesignerSelectionMessage(designId, typeName, props));
        }

        private static Dictionary<string, string> ExtractDesignerProperties(FrameworkElement element)
        {
            var props = new Dictionary<string, string>(StringComparer.Ordinal);

            AddProperty(props, element, "Text", v => v as string);
            AddProperty(props, element, "Content", v => v as string);
            AddProperty(props, element, "FontFamily", v => v is FontFamily ff ? ff.Source : null);
            AddProperty(props, element, "FontSize", v => v is double d ? d.ToString(CultureInfo.InvariantCulture) : null);
            AddProperty(props, element, "FontWeight", v => v?.ToString());
            AddProperty(props, element, "FontStyle", v => v?.ToString());
            AddProperty(props, element, "Foreground", v => v is Brush b ? BrushToHex(b) : null);
            AddProperty(props, element, "Background", v => v is Brush b ? BrushToHex(b) : null);
            AddProperty(props, element, "Margin", v => v is Thickness t ? ThicknessToString(t) : null);
            AddProperty(props, element, "Padding", v => v is Thickness t ? ThicknessToString(t) : null);

            return props;
        }

        private static void AddProperty(
            IDictionary<string, string> props,
            FrameworkElement element,
            string propertyName,
            Func<object?, string?> converter)
        {
            try
            {
                var pi = element.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                if (pi == null || !pi.CanRead) return;

                string? value = null;
                try { value = converter(pi.GetValue(element)); } catch { }

                props[propertyName] = value ?? string.Empty;
            }
            catch { }
        }

        private static string? BrushToHex(Brush brush)
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
