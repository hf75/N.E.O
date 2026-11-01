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
        /// <summary>
        /// Smart Edit changed the app inside the window: the host's copy of the source is out of date.
        /// </summary>
        public Task ReportSourceCodeAsync(string sourceCode)
            => SafeSendAsync(new IpcEnvelope(IpcTypes.SourceCode, "", sourceCode));

        // =======================================================
        // Code Extraction (Reverse-Engineering)
        // =======================================================
        public string ExtractCodeFromVisualTree(Avalonia.Controls.Control root)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// Rebuilt from the live UI: layout and current property values, with NO event handlers, fields or methods.");
            sb.AppendLine("// The app's logic is not here. Take values from this; do not compile it over an app that has behaviour.");
            sb.AppendLine("using Avalonia;");
            sb.AppendLine("using Avalonia.Controls;");
            sb.AppendLine("using Avalonia.Layout;");
            sb.AppendLine("using Avalonia.Media;");
            sb.AppendLine("using Avalonia.Controls.Primitives;");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine();
            sb.AppendLine("namespace DynamicApp;");
            sb.AppendLine();
            sb.AppendLine("public class DynamicUserControl : UserControl");
            sb.AppendLine("{");
            sb.AppendLine("    public DynamicUserControl()");
            sb.AppendLine("    {");

            // The root IS the DynamicUserControl — extract its Content
            if (root is Avalonia.Controls.UserControl uc && uc.Content is Avalonia.Controls.Control content)
            {
                sb.Append("        Content = ");
                EmitControl(content, sb, 2);
                sb.AppendLine(";");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private void EmitControl(Avalonia.Controls.Control ctrl, StringBuilder sb, int indent)
        {
            var pad = new string(' ', indent * 4);
            var typeName = ctrl.GetType().Name;

            // Skip internal Avalonia template parts
            if (IsTemplatePart(ctrl)) return;

            sb.AppendLine($"new {typeName}");
            sb.AppendLine($"{pad}{{");

            // Name
            if (!string.IsNullOrEmpty(ctrl.Name) && !ctrl.Name.StartsWith("PART_"))
                sb.AppendLine($"{pad}    Name = \"{EscapeString(ctrl.Name)}\",");

            // Attached properties (Grid.Row, Grid.Column, DockPanel.Dock, Canvas.Left/Top)
            EmitAttachedProperties(ctrl, sb, pad + "    ");

            // Type-specific properties
            EmitTypeProperties(ctrl, sb, pad + "    ");

            // Common layout properties
            EmitLayoutProperties(ctrl, sb, pad + "    ");

            // Children (for panels/containers)
            var userChildren = GetUserChildren(ctrl);
            if (userChildren.Count > 0)
            {
                sb.AppendLine($"{pad}    Children =");
                sb.AppendLine($"{pad}    {{");
                for (int i = 0; i < userChildren.Count; i++)
                {
                    sb.Append($"{pad}        ");
                    EmitControl(userChildren[i], sb, indent + 2);
                    sb.AppendLine(",");
                }
                sb.AppendLine($"{pad}    }},");
            }

            // Border.Child
            if (ctrl is Avalonia.Controls.Border b && b.Child is Avalonia.Controls.Control borderChild)
            {
                if (!IsTemplatePart(borderChild))
                {
                    sb.Append($"{pad}    Child = ");
                    EmitControl(borderChild, sb, indent + 1);
                    sb.AppendLine(",");
                }
            }
            // Content property (for ContentControl like Button — but not Border which uses Child)
            else if (ctrl is Avalonia.Controls.ContentControl cc && cc.Content is Avalonia.Controls.Control contentChild
                && ctrl is not Avalonia.Controls.Border)
            {
                if (!IsTemplatePart(contentChild))
                {
                    sb.Append($"{pad}    Content = ");
                    EmitControl(contentChild, sb, indent + 1);
                    sb.AppendLine(",");
                }
            }

            sb.Append($"{pad}}}");
        }

        private void EmitTypeProperties(Avalonia.Controls.Control ctrl, StringBuilder sb, string pad)
        {
            switch (ctrl)
            {
                case Avalonia.Controls.TextBlock tb:
                    if (tb.Text != null) sb.AppendLine($"{pad}Text = \"{EscapeString(tb.Text)}\",");
                    if (tb.FontSize != 12) sb.AppendLine($"{pad}FontSize = {tb.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    if (tb.FontWeight != Avalonia.Media.FontWeight.Normal) sb.AppendLine($"{pad}FontWeight = FontWeight.{tb.FontWeight},");
                    EmitBrush(sb, pad, "Foreground", tb.Foreground);
                    if (tb.TextWrapping != Avalonia.Media.TextWrapping.NoWrap) sb.AppendLine($"{pad}TextWrapping = TextWrapping.{tb.TextWrapping},");
                    break;

                case Avalonia.Controls.CheckBox chk:
                    if (chk.Content is string chkText) sb.AppendLine($"{pad}Content = \"{EscapeString(chkText)}\",");
                    if (chk.IsChecked == true) sb.AppendLine($"{pad}IsChecked = true,");
                    break;

                case Avalonia.Controls.Button btn:
                    if (btn.Content is string btnText) sb.AppendLine($"{pad}Content = \"{EscapeString(btnText)}\",");
                    EmitBrush(sb, pad, "Background", btn.Background);
                    EmitBrush(sb, pad, "Foreground", btn.Foreground);
                    if (btn.FontWeight != Avalonia.Media.FontWeight.Normal) sb.AppendLine($"{pad}FontWeight = FontWeight.{btn.FontWeight},");
                    if (btn.Padding != default) EmitThickness(sb, pad, "Padding", btn.Padding);
                    break;

                case Avalonia.Controls.TextBox txb:
                    if (txb.Text != null) sb.AppendLine($"{pad}Text = \"{EscapeString(txb.Text)}\",");
                    if (txb.PlaceholderText != null) sb.AppendLine($"{pad}PlaceholderText = \"{EscapeString(txb.PlaceholderText)}\",");
                    if (txb.IsReadOnly) sb.AppendLine($"{pad}IsReadOnly = true,");
                    break;

                case Avalonia.Controls.Slider sl:
                    sb.AppendLine($"{pad}Minimum = {sl.Minimum.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    sb.AppendLine($"{pad}Maximum = {sl.Maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    sb.AppendLine($"{pad}Value = {sl.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    break;

                case Avalonia.Controls.ListBox lb:
                    if (lb.Height > 0 && !double.IsNaN(lb.Height))
                        sb.AppendLine($"{pad}Height = {lb.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    break;

                case Avalonia.Controls.ProgressBar pb:
                    sb.AppendLine($"{pad}Value = {pb.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    if (pb.IsIndeterminate) sb.AppendLine($"{pad}IsIndeterminate = true,");
                    break;

                case Avalonia.Controls.Border border:
                    EmitBrush(sb, pad, "Background", border.Background);
                    if (border.CornerRadius != default)
                        sb.AppendLine($"{pad}CornerRadius = new CornerRadius({FormatCornerRadius(border.CornerRadius)}),");
                    if (border.Padding != default) EmitThickness(sb, pad, "Padding", border.Padding);
                    if (border.BorderThickness != default) EmitThickness(sb, pad, "BorderThickness", border.BorderThickness);
                    EmitBrush(sb, pad, "BorderBrush", border.BorderBrush);
                    break;

                case Avalonia.Controls.StackPanel sp:
                    if (sp.Orientation == Avalonia.Layout.Orientation.Horizontal)
                        sb.AppendLine($"{pad}Orientation = Orientation.Horizontal,");
                    if (sp.Spacing > 0)
                        sb.AppendLine($"{pad}Spacing = {sp.Spacing.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    break;

                case Avalonia.Controls.Grid grid:
                    if (grid.RowDefinitions.Count > 0)
                    {
                        sb.AppendLine($"{pad}RowDefinitions =");
                        sb.AppendLine($"{pad}{{");
                        foreach (var rd in grid.RowDefinitions)
                        {
                            if (rd.Height.IsAuto) sb.AppendLine($"{pad}    new RowDefinition {{ Height = GridLength.Auto }},");
                            else if (rd.Height.IsStar) sb.AppendLine($"{pad}    new RowDefinition {{ Height = new GridLength({rd.Height.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, GridUnitType.Star) }},");
                            else sb.AppendLine($"{pad}    new RowDefinition {{ Height = new GridLength({rd.Height.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, GridUnitType.Pixel) }},");
                        }
                        sb.AppendLine($"{pad}}},");
                    }
                    if (grid.ColumnDefinitions.Count > 0)
                    {
                        sb.AppendLine($"{pad}ColumnDefinitions =");
                        sb.AppendLine($"{pad}{{");
                        foreach (var cd in grid.ColumnDefinitions)
                        {
                            if (cd.Width.IsAuto) sb.AppendLine($"{pad}    new ColumnDefinition {{ Width = GridLength.Auto }},");
                            else if (cd.Width.IsStar) sb.AppendLine($"{pad}    new ColumnDefinition {{ Width = new GridLength({cd.Width.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, GridUnitType.Star) }},");
                            else sb.AppendLine($"{pad}    new ColumnDefinition {{ Width = new GridLength({cd.Width.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, GridUnitType.Pixel) }},");
                        }
                        sb.AppendLine($"{pad}}},");
                    }
                    break;

                case Avalonia.Controls.ComboBox combo:
                    if (combo.SelectedIndex >= 0) sb.AppendLine($"{pad}SelectedIndex = {combo.SelectedIndex},");
                    if (combo.PlaceholderText != null) sb.AppendLine($"{pad}PlaceholderText = \"{EscapeString(combo.PlaceholderText)}\",");
                    break;

                case Avalonia.Controls.Image img:
                    // Source can't be reliably extracted, emit as comment
                    if (img.Source != null) sb.AppendLine($"{pad}// Source = ... (runtime image, not extractable)");
                    if (img.Stretch != Avalonia.Media.Stretch.Uniform) sb.AppendLine($"{pad}Stretch = Stretch.{img.Stretch},");
                    break;

                case Avalonia.Controls.ScrollViewer sv:
                    if (sv.HorizontalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled)
                        sb.AppendLine($"{pad}HorizontalScrollBarVisibility = ScrollBarVisibility.{sv.HorizontalScrollBarVisibility},");
                    if (sv.VerticalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Auto)
                        sb.AppendLine($"{pad}VerticalScrollBarVisibility = ScrollBarVisibility.{sv.VerticalScrollBarVisibility},");
                    break;

                case Avalonia.Controls.WrapPanel wp:
                    if (wp.Orientation == Avalonia.Layout.Orientation.Vertical)
                        sb.AppendLine($"{pad}Orientation = Orientation.Vertical,");
                    if (wp.ItemWidth > 0 && !double.IsNaN(wp.ItemWidth))
                        sb.AppendLine($"{pad}ItemWidth = {wp.ItemWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    if (wp.ItemHeight > 0 && !double.IsNaN(wp.ItemHeight))
                        sb.AppendLine($"{pad}ItemHeight = {wp.ItemHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    break;

                case Avalonia.Controls.DockPanel dp:
                    if (dp.LastChildFill == false)
                        sb.AppendLine($"{pad}LastChildFill = false,");
                    break;

                case Avalonia.Controls.Canvas canvas:
                    // Canvas-specific: children use Canvas.Left/Top attached properties
                    break;
            }
        }

        private static void EmitAttachedProperties(Avalonia.Controls.Control ctrl, StringBuilder sb, string pad)
        {
            // Grid attached properties
            var row = Avalonia.Controls.Grid.GetRow(ctrl);
            var col = Avalonia.Controls.Grid.GetColumn(ctrl);
            var rowSpan = Avalonia.Controls.Grid.GetRowSpan(ctrl);
            var colSpan = Avalonia.Controls.Grid.GetColumnSpan(ctrl);
            if (row > 0) sb.AppendLine($"{pad}[Grid.RowProperty] = {row},");
            if (col > 0) sb.AppendLine($"{pad}[Grid.ColumnProperty] = {col},");
            if (rowSpan > 1) sb.AppendLine($"{pad}[Grid.RowSpanProperty] = {rowSpan},");
            if (colSpan > 1) sb.AppendLine($"{pad}[Grid.ColumnSpanProperty] = {colSpan},");

            // DockPanel attached property
            var dock = Avalonia.Controls.DockPanel.GetDock(ctrl);
            if (dock != Avalonia.Controls.Dock.Left) // Left is default
                sb.AppendLine($"{pad}[DockPanel.DockProperty] = Dock.{dock},");

            // Canvas attached properties
            var canvasLeft = Avalonia.Controls.Canvas.GetLeft(ctrl);
            var canvasTop = Avalonia.Controls.Canvas.GetTop(ctrl);
            if (!double.IsNaN(canvasLeft) && canvasLeft != 0)
                sb.AppendLine($"{pad}[Canvas.LeftProperty] = {canvasLeft.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
            if (!double.IsNaN(canvasTop) && canvasTop != 0)
                sb.AppendLine($"{pad}[Canvas.TopProperty] = {canvasTop.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
        }

        private void EmitLayoutProperties(Avalonia.Controls.Control ctrl, StringBuilder sb, string pad)
        {
            if (ctrl.HorizontalAlignment != Avalonia.Layout.HorizontalAlignment.Stretch)
                sb.AppendLine($"{pad}HorizontalAlignment = HorizontalAlignment.{ctrl.HorizontalAlignment},");
            if (ctrl.VerticalAlignment != Avalonia.Layout.VerticalAlignment.Stretch)
                sb.AppendLine($"{pad}VerticalAlignment = VerticalAlignment.{ctrl.VerticalAlignment},");
            if (ctrl.Margin != default) EmitThickness(sb, pad, "Margin", ctrl.Margin);
            if (!double.IsNaN(ctrl.Width) && ctrl.Width > 0)
                sb.AppendLine($"{pad}Width = {ctrl.Width.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
            if (!double.IsNaN(ctrl.Height) && ctrl.Height > 0 && ctrl is not Avalonia.Controls.ListBox)
                sb.AppendLine($"{pad}Height = {ctrl.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
            if (!ctrl.IsVisible) sb.AppendLine($"{pad}IsVisible = false,");
            if (ctrl.Opacity < 1.0) sb.AppendLine($"{pad}Opacity = {ctrl.Opacity.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},");

            // Background for panels/templated controls
            if (ctrl is Avalonia.Controls.Panel panel && panel.Background != null && ctrl is not Avalonia.Controls.Border)
                EmitBrush(sb, pad, "Background", panel.Background);
            else if (ctrl is Avalonia.Controls.Primitives.TemplatedControl tc && tc.Background != null
                && ctrl is not Avalonia.Controls.Button && ctrl is not Avalonia.Controls.Border)
                EmitBrush(sb, pad, "Background", tc.Background);
        }

        private static void EmitBrush(StringBuilder sb, string pad, string propName, Avalonia.Media.IBrush? brush)
        {
            if (brush == null) return;
            if (brush is Avalonia.Media.SolidColorBrush scb)
            {
                var color = scb.Color;
                // Try to match named colors
                var named = MatchNamedBrush(color);
                if (named != null)
                    sb.AppendLine($"{pad}{propName} = Brushes.{named},");
                else
                    sb.AppendLine($"{pad}{propName} = new SolidColorBrush(Color.Parse(\"{color}\")),");
            }
            else if (brush is Avalonia.Media.LinearGradientBrush lgb)
            {
                sb.AppendLine($"{pad}{propName} = new LinearGradientBrush");
                sb.AppendLine($"{pad}{{");
                sb.AppendLine($"{pad}    StartPoint = new RelativePoint({lgb.StartPoint.Point.X}, {lgb.StartPoint.Point.Y}, RelativeUnit.Relative),");
                sb.AppendLine($"{pad}    EndPoint = new RelativePoint({lgb.EndPoint.Point.X}, {lgb.EndPoint.Point.Y}, RelativeUnit.Relative),");
                sb.AppendLine($"{pad}    GradientStops =");
                sb.AppendLine($"{pad}    {{");
                foreach (var stop in lgb.GradientStops)
                    sb.AppendLine($"{pad}        new GradientStop(Color.Parse(\"{stop.Color}\"), {stop.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture)}),");
                sb.AppendLine($"{pad}    }},");
                sb.AppendLine($"{pad}}},");
            }
        }

        private static void EmitThickness(StringBuilder sb, string pad, string propName, Avalonia.Thickness t)
        {
            if (t.Left == t.Right && t.Top == t.Bottom && t.Left == t.Top)
                sb.AppendLine($"{pad}{propName} = new Thickness({t.Left.ToString(System.Globalization.CultureInfo.InvariantCulture)}),");
            else if (t.Left == t.Right && t.Top == t.Bottom)
                sb.AppendLine($"{pad}{propName} = new Thickness({t.Left.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {t.Top.ToString(System.Globalization.CultureInfo.InvariantCulture)}),");
            else
                sb.AppendLine($"{pad}{propName} = new Thickness({t.Left.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {t.Top.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {t.Right.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {t.Bottom.ToString(System.Globalization.CultureInfo.InvariantCulture)}),");
        }

        private static string FormatCornerRadius(Avalonia.CornerRadius cr)
        {
            if (cr.TopLeft == cr.TopRight && cr.TopLeft == cr.BottomLeft && cr.TopLeft == cr.BottomRight)
                return cr.TopLeft.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return $"{cr.TopLeft.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {cr.TopRight.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {cr.BottomRight.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {cr.BottomLeft.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        private static string? MatchNamedBrush(Avalonia.Media.Color color)
        {
            // Check common named brushes
            var brushProps = typeof(Avalonia.Media.Brushes).GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            foreach (var prop in brushProps)
            {
                if (prop.GetValue(null) is Avalonia.Media.SolidColorBrush named && named.Color == color)
                    return prop.Name;
            }
            return null;
        }

        private static bool IsTemplatePart(Avalonia.Controls.Control ctrl)
        {
            // Skip Avalonia internal template parts
            if (ctrl.Name != null && ctrl.Name.StartsWith("PART_")) return true;
            if (ctrl.Name != null && ctrl.Name.StartsWith("PART_")) return true;
            var typeName = ctrl.GetType().Name;
            if (typeName is "ContentPresenter" or "ScrollContentPresenter" or "DataValidationErrors"
                or "AccessText" or "TextPresenter" or "ScrollBar")
                return true;
            // ScrollViewer and DockPanel only internal if unnamed
            if (typeName is "ScrollViewer" or "DockPanel" && string.IsNullOrEmpty(ctrl.Name))
                return true;
            return false;
        }

        private List<Avalonia.Controls.Control> GetUserChildren(Avalonia.Controls.Control ctrl)
        {
            // Only get direct children for panel types
            if (ctrl is Avalonia.Controls.Panel panel)
            {
                return panel.Children
                    .OfType<Avalonia.Controls.Control>()
                    .Where(c => !IsTemplatePart(c))
                    .ToList();
            }
            return new List<Avalonia.Controls.Control>();
        }

        private static string EscapeString(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }
}
