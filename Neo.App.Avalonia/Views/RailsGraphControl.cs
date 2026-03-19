using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Neo.App
{
    public sealed class RailsGraphControl : Control
    {
        public static readonly StyledProperty<HistoryNode?> RootProperty =
            AvaloniaProperty.Register<RailsGraphControl, HistoryNode?>(nameof(Root));

        public static readonly StyledProperty<HistoryNode?> CurrentProperty =
            AvaloniaProperty.Register<RailsGraphControl, HistoryNode?>(nameof(Current));

        public static readonly StyledProperty<HistoryNode?> SelectedProperty =
            AvaloniaProperty.Register<RailsGraphControl, HistoryNode?>(nameof(Selected), defaultBindingMode: global::Avalonia.Data.BindingMode.TwoWay);

        private readonly Dictionary<Guid, NodeRenderInfo> _renderNodes = new();
        private HistoryNode? _hovered;
        private bool _isPanning;
        private Point _panStartMouse;
        private Vector _panStartOffset;
        private Vector _panOffset = new(24, 24);

        static RailsGraphControl()
        {
            AffectsRender<RailsGraphControl>(RootProperty, CurrentProperty, SelectedProperty);
        }

        public RailsGraphControl()
        {
            Focusable = true;
            ClipToBounds = true;
        }

        public HistoryNode? Root
        {
            get => GetValue(RootProperty);
            set => SetValue(RootProperty, value);
        }

        public HistoryNode? Current
        {
            get => GetValue(CurrentProperty);
            set => SetValue(CurrentProperty, value);
        }

        public HistoryNode? Selected
        {
            get => GetValue(SelectedProperty);
            set => SetValue(SelectedProperty, value);
        }

        public event EventHandler<HistoryNode>? NodeActivated;

        public override void Render(DrawingContext dc)
        {
            base.Render(dc);

            // Draw background
            dc.FillRectangle(Brushes.White, new Rect(Bounds.Size));

            var root = Root;
            if (root == null) return;

            RebuildLayout(root);

            // Draw rails (connections)
            var railPen = new Pen(new SolidColorBrush(Color.FromRgb(185, 185, 185)), 1);

            foreach (var info in _renderNodes.Values)
            {
                if (info.Node.Children.Count == 0) continue;

                foreach (var child in info.Node.Children)
                {
                    if (!_renderNodes.TryGetValue(child.Id, out var childInfo)) continue;
                    DrawRail(dc, railPen, info.Center, childInfo.Center);
                }
            }

            // Draw nodes
            var nodeBrush = Brushes.Black;
            var currentRingPen = new Pen(Brushes.White, 2);
            var selectedRingPen = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 212)), 2);
            var hoveredRingPen = new Pen(new SolidColorBrush(Color.FromRgb(100, 100, 100)), 1.5);

            foreach (var info in _renderNodes.Values)
            {
                var center = info.Center;
                var r = info.Radius;

                dc.DrawEllipse(nodeBrush, null, center, r, r);

                bool isCurrent = Current != null && ReferenceEquals(info.Node, Current);
                bool isSelected = Selected != null && ReferenceEquals(info.Node, Selected);
                bool isHovered = _hovered != null && ReferenceEquals(info.Node, _hovered);

                if (isCurrent)
                    dc.DrawEllipse(null, currentRingPen, center, r + 2, r + 2);

                if (isSelected)
                    dc.DrawEllipse(null, selectedRingPen, center, r + 4, r + 4);
                else if (isHovered)
                    dc.DrawEllipse(null, hoveredRingPen, center, r + 3, r + 3);
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var pos = e.GetPosition(this);

            if (_isPanning)
            {
                var delta = pos - _panStartMouse;
                _panOffset = _panStartOffset + delta;
                InvalidateVisual();
                return;
            }

            var node = HitTestNode(pos);
            if (!ReferenceEquals(_hovered, node))
            {
                _hovered = node;
                InvalidateVisual();
            }
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (_hovered != null)
            {
                _hovered = null;
                InvalidateVisual();
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var point = e.GetCurrentPoint(this);
            var pos = point.Position;

            if (point.Properties.IsRightButtonPressed)
            {
                _isPanning = true;
                _panStartMouse = pos;
                _panStartOffset = _panOffset;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            if (point.Properties.IsLeftButtonPressed)
            {
                Focus();
                var node = HitTestNode(pos);
                if (node != null)
                {
                    Selected = node;

                    if (e.ClickCount >= 2)
                    {
                        NodeActivated?.Invoke(this, node);
                        e.Handled = true;
                    }
                }
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_isPanning)
            {
                _isPanning = false;
                e.Pointer.Capture(null);
                e.Handled = true;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Enter && Selected != null)
            {
                NodeActivated?.Invoke(this, Selected);
                e.Handled = true;
            }
        }

        private void RebuildLayout(HistoryNode root)
        {
            _renderNodes.Clear();

            var nodes = EnumerateNodes(root).OrderBy(n => n.CommitIndex).ToList();
            if (nodes.Count == 0) return;

            var laneById = new Dictionary<Guid, int> { [root.Id] = 0 };
            int nextLane = 1;

            foreach (var node in nodes)
            {
                if (!laneById.TryGetValue(node.Id, out int parentLane)) continue;
                if (node.Children.Count == 0) continue;

                var mainChild = node.Children[0];
                foreach (var child in node.Children)
                {
                    if (laneById.ContainsKey(child.Id)) continue;
                    laneById[child.Id] = ReferenceEquals(child, mainChild) ? parentLane : nextLane++;
                }
            }

            const double xSpacing = 46;
            const double ySpacing = 28;
            const double radius = 4;

            foreach (var node in nodes)
            {
                laneById.TryGetValue(node.Id, out int lane);
                var center = new Point(_panOffset.X + node.CommitIndex * xSpacing, _panOffset.Y + lane * ySpacing);
                _renderNodes[node.Id] = new NodeRenderInfo(node, center, radius);
            }
        }

        private static IEnumerable<HistoryNode> EnumerateNodes(HistoryNode root)
        {
            var stack = new Stack<HistoryNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                yield return node;
                for (int i = node.Children.Count - 1; i >= 0; i--)
                    stack.Push(node.Children[i]);
            }
        }

        private HistoryNode? HitTestNode(Point p)
        {
            const double hitPad = 6;
            foreach (var info in _renderNodes.Values)
            {
                var dx = p.X - info.Center.X;
                var dy = p.Y - info.Center.Y;
                var r = info.Radius + hitPad;
                if ((dx * dx) + (dy * dy) <= (r * r))
                    return info.Node;
            }
            return null;
        }

        private static void DrawRail(DrawingContext dc, IPen pen, Point start, Point end)
        {
            if (Math.Abs(start.Y - end.Y) < 0.1)
            {
                dc.DrawLine(pen, start, end);
                return;
            }

            var dx = Math.Max(1, end.X - start.X);
            var c1 = new Point(start.X + (dx * 0.5), start.Y);
            var c2 = new Point(end.X - (dx * 0.5), end.Y);

            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(start, false);
                ctx.CubicBezierTo(c1, c2, end);
            }

            dc.DrawGeometry(null, pen, geom);
        }

        private sealed record NodeRenderInfo(HistoryNode Node, Point Center, double Radius);
    }
}
