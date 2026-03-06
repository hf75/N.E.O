using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using ToolTip = System.Windows.Controls.ToolTip;

namespace Neo.App
{
    public sealed class HistoryNodeEventArgs : EventArgs
    {
        public HistoryNodeEventArgs(HistoryNode node)
        {
            Node = node ?? throw new ArgumentNullException(nameof(node));
        }

        public HistoryNode Node { get; }
    }

    public sealed class RailsGraphControl : FrameworkElement
    {
        public static readonly DependencyProperty RootProperty =
            DependencyProperty.Register(
                nameof(Root),
                typeof(HistoryNode),
                typeof(RailsGraphControl),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnGraphPropertyChanged));

        public static readonly DependencyProperty CurrentProperty =
            DependencyProperty.Register(
                nameof(Current),
                typeof(HistoryNode),
                typeof(RailsGraphControl),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnGraphPropertyChanged));

        public static readonly DependencyProperty SelectedProperty =
            DependencyProperty.Register(
                nameof(Selected),
                typeof(HistoryNode),
                typeof(RailsGraphControl),
                new FrameworkPropertyMetadata(
                    null,
                    FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        private readonly Dictionary<Guid, NodeRenderInfo> _renderNodes = new();
        private readonly ToolTip _toolTip;
        private HistoryNode? _hovered;

        private bool _isPanning;
        private Point _panStartMouse;
        private Vector _panStartOffset;
        private Vector _panOffset = new(24, 24);

        public RailsGraphControl()
        {
            Focusable = true;
            SnapsToDevicePixels = true;

            _toolTip = new ToolTip
            {
                PlacementTarget = this,
                Placement = PlacementMode.MousePoint,
                StaysOpen = true,
            };

            ToolTipService.SetInitialShowDelay(this, 0);
            ToolTipService.SetShowDuration(this, int.MaxValue);
            ToolTipService.SetPlacement(this, PlacementMode.MousePoint);
            ToolTip = _toolTip;
        }

        public HistoryNode? Root
        {
            get => (HistoryNode?)GetValue(RootProperty);
            set => SetValue(RootProperty, value);
        }

        public HistoryNode? Current
        {
            get => (HistoryNode?)GetValue(CurrentProperty);
            set => SetValue(CurrentProperty, value);
        }

        public HistoryNode? Selected
        {
            get => (HistoryNode?)GetValue(SelectedProperty);
            set => SetValue(SelectedProperty, value);
        }

        public event EventHandler<HistoryNodeEventArgs>? NodeActivated;

        protected override Size MeasureOverride(Size availableSize) => availableSize;

        protected override Size ArrangeOverride(Size finalSize) => finalSize;

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            var root = Root;
            if (root == null)
                return;

            RebuildLayout(root);

            var railPen = new Pen(new SolidColorBrush(Color.FromRgb(185, 185, 185)), 1)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            railPen.Freeze();

            foreach (var info in _renderNodes.Values)
            {
                var node = info.Node;
                if (node.Children.Count == 0)
                    continue;

                foreach (var child in node.Children)
                {
                    if (!_renderNodes.TryGetValue(child.Id, out var childInfo))
                        continue;

                    DrawRail(dc, railPen, info.Center, childInfo.Center);
                }
            }

            var nodeBrush = Brushes.Black;
            var currentRingPen = new Pen(Brushes.White, 2) { LineJoin = PenLineJoin.Round };
            currentRingPen.Freeze();
            var selectedRingPen = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 212)), 2) { LineJoin = PenLineJoin.Round };
            selectedRingPen.Freeze();

            foreach (var info in _renderNodes.Values)
            {
                var center = info.Center;
                var r = info.Radius;

                dc.DrawEllipse(nodeBrush, null, center, r, r);

                bool isCurrent = Current != null && ReferenceEquals(info.Node, Current);
                bool isSelected = Selected != null && ReferenceEquals(info.Node, Selected);

                if (isCurrent)
                    dc.DrawEllipse(null, currentRingPen, center, r + 2, r + 2);

                if (isSelected)
                    dc.DrawEllipse(null, selectedRingPen, center, r + 4, r + 4);
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            SetHovered(null);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            var pos = e.GetPosition(this);

            if (_isPanning)
            {
                var delta = pos - _panStartMouse;
                _panOffset = _panStartOffset + (Vector)delta;
                InvalidateVisual();
                return;
            }

            SetHovered(HitTestNode(pos));
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);

            _isPanning = true;
            _panStartMouse = e.GetPosition(this);
            _panStartOffset = _panOffset;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);

            if (!_isPanning) return;

            _isPanning = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            Focus();

            var node = HitTestNode(e.GetPosition(this));
            if (node == null)
                return;

            Selected = node;

            if (e.ClickCount >= 2)
            {
                NodeActivated?.Invoke(this, new HistoryNodeEventArgs(node));
                e.Handled = true;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Key == Key.Enter && Selected != null)
            {
                NodeActivated?.Invoke(this, new HistoryNodeEventArgs(Selected));
                e.Handled = true;
            }
        }

        private static void OnGraphPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RailsGraphControl ctrl)
            {
                ctrl._hovered = null;
                ctrl._toolTip.IsOpen = false;
            }
        }

        private void RebuildLayout(HistoryNode root)
        {
            _renderNodes.Clear();

            var nodes = EnumerateNodes(root).OrderBy(n => n.CommitIndex).ToList();
            if (nodes.Count == 0)
                return;

            var laneById = new Dictionary<Guid, int> { [root.Id] = 0 };
            int nextLane = 1;

            foreach (var node in nodes)
            {
                if (!laneById.TryGetValue(node.Id, out int parentLane))
                    continue;

                if (node.Children.Count == 0)
                    continue;

                // Always keep the first (oldest) child on the parent's lane
                // so the graph layout stays stable when new branches are added.
                HistoryNode mainChild = node.Children[0];

                foreach (var child in node.Children)
                {
                    if (laneById.ContainsKey(child.Id))
                        continue;

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

        private void SetHovered(HistoryNode? node)
        {
            if (ReferenceEquals(_hovered, node))
                return;

            _hovered = node;

            if (_hovered == null)
            {
                _toolTip.IsOpen = false;
                return;
            }

            _toolTip.Content = BuildToolTipText(_hovered);
            _toolTip.IsOpen = true;
        }

        private static string BuildToolTipText(HistoryNode node)
        {
            var title = string.IsNullOrWhiteSpace(node.Title) ? "(untitled)" : node.Title.Trim();
            var desc = string.IsNullOrWhiteSpace(node.Description) ? string.Empty : node.Description.Trim();
            return string.IsNullOrEmpty(desc)
                ? $"{title}\n{node.Timestamp:G}"
                : $"{title}\n{node.Timestamp:G}\n\n{desc}";
        }

        private static void DrawRail(DrawingContext dc, Pen pen, Point start, Point end)
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
                ctx.BeginFigure(start, isFilled: false, isClosed: false);
                ctx.BezierTo(c1, c2, end, isStroked: true, isSmoothJoin: true);
            }
            geom.Freeze();

            dc.DrawGeometry(null, pen, geom);
        }

        private sealed record NodeRenderInfo(HistoryNode Node, Point Center, double Radius);
    }
}
