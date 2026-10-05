// 节点拖拽排序：基于指针捕获实时重排集合，不依赖系统 OLE 拖放，管理员权限下同样可用。
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace WinUIMacro.UI.Views;

public sealed partial class MacroDetailPage
{
    private const double DragStartThreshold = 6;
    private const double DraggedItemOpacity = 0.55;
    private const double AutoScrollEdge = 48;
    private const double AutoScrollMaxStep = 20;

    private DispatcherQueueTimer? _autoScrollTimer;
    private ScrollViewer? _nodeScrollViewer;
    private MacroNodeViewModel? _pressedNode;
    private uint _pressedPointerId;
    private Point _pressedPoint;
    private Point _lastDragPoint;
    private NodeDragSession? _drag;

    private void InitializeNodeDrag()
    {
        // 节点容器会处理指针事件，必须接收已处理事件才能识别从节点上开始的拖拽。
        NodeGrid.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(NodeGrid_PointerPressed),
            true
        );
        NodeGrid.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(NodeGrid_PointerMoved),
            true
        );
        NodeGrid.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(NodeGrid_PointerReleased),
            true
        );
        NodeGrid.PointerCaptureLost += NodeGrid_PointerCaptureLost;
        NodeGrid.PointerCanceled += NodeGrid_PointerCanceled;
        NodeGrid.ContainerContentChanging += NodeGrid_ContainerContentChanging;
    }

    private void DisposeNodeDrag()
    {
        FinishNodeDrag(restore: true);
        if (_autoScrollTimer is not null)
        {
            _autoScrollTimer.Stop();
            _autoScrollTimer.Tick -= AutoScrollTimer_Tick;
            _autoScrollTimer = null;
        }
        _nodeScrollViewer = null;
    }

    private bool HandleNodeDragKeyDown(KeyRoutedEventArgs e)
    {
        if (_drag is null)
            return false;
        // 拖拽期间屏蔽删除、粘贴等编辑快捷键，只允许 Esc 取消并恢复原顺序。
        if (e.Key == VirtualKey.Escape)
            FinishNodeDrag(restore: true);
        e.Handled = true;
        return true;
    }

    private void NodeGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _pressedNode = null;
        if (
            _drag is not null
            || !ViewModel.CanEditSequence
            || e.Pointer.PointerDeviceType == PointerDeviceType.Touch
        )
            return;

        var point = e.GetCurrentPoint(NodeGrid);
        var source = e.OriginalSource as DependencyObject;
        if (
            !point.Properties.IsLeftButtonPressed
            || FindAncestor<TextBox>(source) is not null
            || FindAncestor<GridViewItem>(source) is not { } container
            || NodeGrid.ItemFromContainer(container) is not MacroNodeViewModel node
        )
            return;

        _pressedNode = node;
        _pressedPointerId = e.Pointer.PointerId;
        _pressedPoint = point.Position;
    }

    private void NodeGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is { } drag)
        {
            if (e.Pointer.PointerId != drag.Pointer.PointerId)
                return;
            _lastDragPoint = e.GetCurrentPoint(NodeGrid).Position;
            UpdateNodeDragTarget();
            UpdateAutoScroll();
            e.Handled = true;
            return;
        }

        if (_pressedNode is not { } pressedNode || e.Pointer.PointerId != _pressedPointerId)
            return;
        var point = e.GetCurrentPoint(NodeGrid);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _pressedNode = null;
            return;
        }
        var deltaX = point.Position.X - _pressedPoint.X;
        var deltaY = point.Position.Y - _pressedPoint.Y;
        if (deltaX * deltaX + deltaY * deltaY < DragStartThreshold * DragStartThreshold)
            return;

        _pressedNode = null;
        if (BeginNodeDrag(pressedNode, e.Pointer, point.Position))
            e.Handled = true;
    }

    private void NodeGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _pressedNode = null;
        if (_drag is not { } drag || e.Pointer.PointerId != drag.Pointer.PointerId)
            return;
        FinishNodeDrag(restore: false);
        e.Handled = true;
    }

    private void NodeGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // 子容器失去捕获的事件也会冒泡到这里，只处理列表自身的捕获丢失。
        if (
            ReferenceEquals(e.OriginalSource, NodeGrid)
            && _drag?.Pointer.PointerId == e.Pointer.PointerId
        )
            FinishNodeDrag(restore: false);
    }

    private void NodeGrid_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_drag?.Pointer.PointerId == e.Pointer.PointerId)
            FinishNodeDrag(restore: true);
    }

    private void NodeGrid_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args
    )
    {
        // 虚拟化会回收容器，重新关联数据时同步拖拽中的半透明状态。
        var dragged =
            !args.InRecycleQueue
            && args.Item is MacroNodeViewModel node
            && _drag?.NodeSet.Contains(node) == true;
        args.ItemContainer.Opacity = dragged ? DraggedItemOpacity : 1;
    }

    private bool BeginNodeDrag(MacroNodeViewModel pressedNode, Pointer pointer, Point position)
    {
        if (
            !ViewModel.CanEditSequence
            || NodeGrid.ItemsPanelRoot is not ItemsWrapGrid panel
            || !TryMeasureNodeGrid(panel, out var layout)
        )
            return false;

        // 按下的节点已在选区中时拖动整个选区，否则只拖动该节点。
        var selectedNodes = GetSelectedNodes();
        MacroNodeViewModel[] nodes = selectedNodes.Contains(pressedNode)
            ? selectedNodes
            : [pressedNode];
        if (!NodeGrid.CapturePointer(pointer))
            return false;

        var originalOrder = Macro.Nodes.ToArray();
        var drag = new NodeDragSession(nodes, originalOrder, Macro.IsDirty, pointer, layout);
        _drag = drag;
        _lastDragPoint = position;
        NodeGrid.ItemContainerTransitions = CreateItemContainerTransitionsWithoutAddDelete();

        // 把拖拽节点收拢为以按下节点为锚点的连续组，其余节点随重排动画自动填补空位。
        var pressedIndex = Array.IndexOf(originalOrder, pressedNode);
        var nodesBeforePressed = Array.IndexOf(nodes, pressedNode);
        drag.Start = Math.Clamp(
            pressedIndex - nodesBeforePressed,
            0,
            originalOrder.Length - nodes.Length
        );
        var gatheredOrder = originalOrder.Where(node => !drag.NodeSet.Contains(node)).ToList();
        gatheredOrder.InsertRange(drag.Start, nodes);
        Macro.ApplyNodeOrder(gatheredOrder);
        // 集合移动可能让列表丢失被移动项的选中状态，收拢后重新选中拖拽组。
        SelectNodes(nodes);

        SetDraggedContainerOpacity(nodes, DraggedItemOpacity);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
        return true;
    }

    private void UpdateNodeDragTarget()
    {
        if (_drag is not { } drag || NodeGrid.ItemsPanelRoot is not ItemsWrapGrid panel)
            return;
        if (Macro.Nodes.Count != drag.OriginalOrder.Length)
        {
            FinishNodeDrag(restore: false);
            return;
        }

        // 节点尺寸固定，直接按网格几何计算目标格子，避免命中测试受重排动画中的偏移干扰。
        var layout = drag.Layout;
        var point = NodeGrid.TransformToVisual(panel).TransformPoint(_lastDragPoint);
        var column = Math.Clamp(
            (int)Math.Floor((point.X - layout.OriginX) / layout.CellWidth),
            0,
            layout.Columns - 1
        );
        var row = Math.Max(0, (int)Math.Floor((point.Y - layout.OriginY) / layout.CellHeight));
        var hoveredIndex = (long)row * layout.Columns + column;
        var count = Macro.Nodes.Count;
        var groupLength = drag.Nodes.Length;

        int newStart;
        if (hoveredIndex >= count)
            newStart = count - groupLength;
        else if (hoveredIndex < drag.Start)
            newStart = (int)hoveredIndex;
        else if (hoveredIndex >= drag.Start + groupLength)
            newStart = (int)hoveredIndex - groupLength + 1;
        else
            return;

        if (newStart == drag.Start)
            return;
        Macro.MoveNodeRange(drag.Start, groupLength, newStart);
        drag.Start = newStart;
    }

    private void FinishNodeDrag(bool restore)
    {
        if (_drag is not { } drag)
            return;
        // 先清空会话，避免释放捕获时触发的 PointerCaptureLost 重入。
        _drag = null;
        _autoScrollTimer?.Stop();
        ProtectedCursor = null;
        NodeGrid.ReleasePointerCapture(drag.Pointer);
        SetDraggedContainerOpacity(drag.Nodes, 1);

        if (Macro is null)
            return;
        var sameNodes = Macro.Nodes.Count == drag.OriginalOrder.Length;
        if (restore && sameNodes)
            Macro.ApplyNodeOrder(drag.OriginalOrder);
        // 拖回原位等价于未修改，恢复拖拽前的脏状态。
        if (!drag.WasDirty && sameNodes && Macro.Nodes.SequenceEqual(drag.OriginalOrder))
            Macro.IsDirty = false;

        SelectNodes(drag.Nodes.Where(Macro.Nodes.Contains));
        NodeGrid.Focus(FocusState.Programmatic);
        if (!DispatcherQueue.TryEnqueue(UpdateItemContainerTransitions))
            UpdateItemContainerTransitions();
    }

    private void SetDraggedContainerOpacity(IEnumerable<MacroNodeViewModel> nodes, double opacity)
    {
        foreach (var node in nodes)
            if (NodeGrid.ContainerFromItem(node) is UIElement container)
                container.Opacity = opacity;
    }

    private bool TryMeasureNodeGrid(ItemsWrapGrid panel, out NodeGridLayout layout)
    {
        layout = default;
        var firstIndex = panel.FirstVisibleIndex;
        if (firstIndex < 0 || NodeGrid.ContainerFromIndex(firstIndex) is not FrameworkElement first)
            return false;
        var firstSlot = LayoutInformation.GetLayoutSlot(first);
        if (firstSlot.Width <= 0 || firstSlot.Height <= 0)
            return false;

        // 用两个位于不同行的已布局容器反推每行列数，不依赖面板内部的列数算法。
        var columns = 0;
        foreach (var index in new[] { panel.LastVisibleIndex, panel.LastCacheIndex })
        {
            if (
                index <= firstIndex
                || NodeGrid.ContainerFromIndex(index) is not FrameworkElement other
            )
                continue;
            var slot = LayoutInformation.GetLayoutSlot(other);
            var rows = (int)Math.Round((slot.Y - firstSlot.Y) / firstSlot.Height);
            if (rows <= 0)
                continue;
            var columnOffset = (int)Math.Round((slot.X - firstSlot.X) / firstSlot.Width);
            columns = (index - firstIndex - columnOffset) / rows;
            break;
        }
        // 所有已布局节点位于同一行时，该行能容纳全部节点。
        if (columns <= 0)
            columns = Math.Max(1, Macro.Nodes.Count);

        layout = new NodeGridLayout(
            firstSlot.X - firstIndex % columns * firstSlot.Width,
            firstSlot.Y - firstIndex / columns * firstSlot.Height,
            firstSlot.Width,
            firstSlot.Height,
            columns
        );
        return true;
    }

    private void UpdateAutoScroll()
    {
        if (GetAutoScrollStep() == 0)
        {
            _autoScrollTimer?.Stop();
            return;
        }
        if (_autoScrollTimer is null)
        {
            _autoScrollTimer = DispatcherQueue.CreateTimer();
            _autoScrollTimer.Interval = TimeSpan.FromMilliseconds(16);
            _autoScrollTimer.IsRepeating = true;
            _autoScrollTimer.Tick += AutoScrollTimer_Tick;
        }
        if (!_autoScrollTimer.IsRunning)
            _autoScrollTimer.Start();
    }

    private double GetAutoScrollStep()
    {
        // 指针越靠近或越过上下边缘，滚动越快。
        var y = _lastDragPoint.Y;
        var height = NodeGrid.ActualHeight;
        if (y < AutoScrollEdge)
            return -AutoScrollMaxStep * Math.Min(1, (AutoScrollEdge - y) / AutoScrollEdge);
        if (y > height - AutoScrollEdge)
            return AutoScrollMaxStep
                * Math.Min(1, (y - (height - AutoScrollEdge)) / AutoScrollEdge);
        return 0;
    }

    private void AutoScrollTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        var step = GetAutoScrollStep();
        _nodeScrollViewer ??= FindDescendantOfType<ScrollViewer>(NodeGrid);
        if (_drag is null || step == 0 || _nodeScrollViewer is not { } scrollViewer)
        {
            sender.Stop();
            return;
        }
        var offset = Math.Clamp(
            scrollViewer.VerticalOffset + step,
            0,
            scrollViewer.ScrollableHeight
        );
        if (offset == scrollViewer.VerticalOffset)
            return;
        scrollViewer.ChangeView(null, offset, null, disableAnimation: true);
        UpdateNodeDragTarget();
    }

    private static T? FindDescendantOfType<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                return match;
            if (FindDescendantOfType<T>(child) is { } descendant)
                return descendant;
        }
        return null;
    }

    private readonly record struct NodeGridLayout(
        double OriginX,
        double OriginY,
        double CellWidth,
        double CellHeight,
        int Columns
    );

    private sealed class NodeDragSession(
        MacroNodeViewModel[] nodes,
        MacroNodeViewModel[] originalOrder,
        bool wasDirty,
        Pointer pointer,
        NodeGridLayout layout
    )
    {
        public MacroNodeViewModel[] Nodes { get; } = nodes;
        public HashSet<MacroNodeViewModel> NodeSet { get; } = [.. nodes];
        public MacroNodeViewModel[] OriginalOrder { get; } = originalOrder;
        public bool WasDirty { get; } = wasDirty;
        public Pointer Pointer { get; } = pointer;
        public NodeGridLayout Layout { get; } = layout;
        public int Start { get; set; }
    }
}
