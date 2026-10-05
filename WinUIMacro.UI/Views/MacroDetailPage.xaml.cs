// 管理宏详情页的节点列表绑定、选择和编辑交互；拖拽排序见 MacroDetailPage.NodeDrag.cs。
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI.Core;

namespace WinUIMacro.UI.Views;

public sealed partial class MacroDetailPage : Page, IDisposable
{
    private readonly TransitionCollection? _defaultItemContainerTransitions;
    private bool _scrollPending;

    public MacroDetailPage()
    {
        InitializeComponent();
        _defaultItemContainerTransitions = NodeGrid.ItemContainerTransitions;
        InitializeNodeDrag();
    }

    internal MacroWorkspaceViewModel ViewModel { get; private set; } = null!;
    internal MacroEditorViewModel Macro { get; private set; } = null!;

    internal void Activate(MacroWorkspaceViewModel viewModel, MacroEditorViewModel macro)
    {
        if (ViewModel is not null)
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (Macro is not null)
        {
            Macro.Nodes.CollectionChanged -= Nodes_CollectionChanged;
            if (!ReferenceEquals(Macro, macro))
                Macro.ClearCopiedNodes();
        }
        ViewModel = viewModel;
        Macro = macro;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Macro.Nodes.CollectionChanged += Nodes_CollectionChanged;
        UpdateItemContainerTransitions();
        Bindings.Update();
    }

    public void Dispose()
    {
        DisposeNodeDrag();
        if (ViewModel is not null)
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (Macro is not null)
        {
            Macro.Nodes.CollectionChanged -= Nodes_CollectionChanged;
            Macro.ClearCopiedNodes();
        }
        Bindings.StopTracking();
        NodeGrid.ItemsSource = null;
        ViewModel = null!;
        Macro = null!;
    }

    private void NodeGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HandleNodeDragKeyDown(e) || FocusManager.GetFocusedElement(XamlRoot) is TextBox)
            return;

        var controlDown = IsKeyDown(VirtualKey.Control);
        if (controlDown && e.Key == VirtualKey.A)
        {
            NodeGrid.SelectAll();
            e.Handled = true;
            return;
        }

        if (!ViewModel.CanEditSequence)
            return;

        var selectedNodes = GetSelectedNodes();
        if (controlDown && e.Key == VirtualKey.C)
        {
            Macro.CopyNodes(selectedNodes);
            e.Handled = true;
        }
        else if (controlDown && e.Key == VirtualKey.V)
        {
            NodeGrid.ItemContainerTransitions = CreateItemContainerTransitionsWithoutAddDelete();

            try
            {
                var pastedNodes = Macro.PasteNodesAfterSelection(selectedNodes);
                if (pastedNodes.Length > 0)
                {
                    SelectNodes(pastedNodes);
                    NodeGrid.ScrollIntoView(pastedNodes[^1]);
                }
            }
            finally
            {
                if (!DispatcherQueue.TryEnqueue(UpdateItemContainerTransitions))
                    UpdateItemContainerTransitions();
            }

            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && selectedNodes is [var node] && node.IsEditable)
        {
            BeginNodeEdit(node);
            e.Handled = true;
        }
        else if (e.Key is VirtualKey.Delete or VirtualKey.Back && selectedNodes.Length > 0)
        {
            var firstRemovedIndex = Macro.DeleteNodes(selectedNodes);
            SelectNodeAt(Math.Max(0, firstRemovedIndex - 1));
            e.Handled = true;
        }
    }

    private void NodeGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.IsRecording)
            e.Handled = true;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MacroWorkspaceViewModel.RecordingState))
            UpdateItemContainerTransitions();
    }

    private void UpdateItemContainerTransitions()
    {
        NodeGrid.ItemContainerTransitions = ViewModel.IsRecording
            ? CreateItemContainerTransitionsWithoutAddDelete()
            : _defaultItemContainerTransitions;
    }

    private static TransitionCollection CreateItemContainerTransitionsWithoutAddDelete() =>
        [new ContentThemeTransition(), new ReorderThemeTransition(), new EntranceThemeTransition()];

    private void Nodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!ViewModel.IsRecording || e.Action != NotifyCollectionChangedAction.Add)
            return;

        // 录制期间节点持续追加，异步滚动到末尾避免在集合回调中操作未生成的容器。
        if (_scrollPending)
            return;
        _scrollPending = true;
        if (
            !DispatcherQueue.TryEnqueue(() =>
            {
                _scrollPending = false;
                if (Macro is not null && Macro.Nodes.LastOrDefault() is { } lastNode)
                    NodeGrid.ScrollIntoView(lastNode, ScrollIntoViewAlignment.Default);
            })
        )
            _scrollPending = false;
    }

    private void SelectNodeAt(int index)
    {
        if (index < 0 || index >= Macro.Nodes.Count)
        {
            NodeGrid.SelectedItem = null;
            return;
        }

        var node = Macro.Nodes[index];
        NodeGrid.SelectedItem = node;
        NodeGrid.ScrollIntoView(node);
        NodeGrid.Focus(FocusState.Programmatic);
    }

    private MacroNodeViewModel[] GetSelectedNodes() =>
        NodeGrid.SelectedItems.OfType<MacroNodeViewModel>().OrderBy(Macro.Nodes.IndexOf).ToArray();

    private void SelectNodes(IEnumerable<MacroNodeViewModel> nodes)
    {
        NodeGrid.SelectedItems.Clear();
        foreach (var node in nodes)
            NodeGrid.SelectedItems.Add(node);
    }

    private void NodeGrid_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (FindAncestor<GridViewItem>(e.OriginalSource as DependencyObject) is null)
        {
            NodeGrid.SelectedItems.Clear();
            NodeGrid.Focus(FocusState.Pointer);
        }
    }

    private void EditableNodeDisplay_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (
            !ViewModel.CanEditSequence
            || (sender as FrameworkElement)?.Tag is not MacroNodeViewModel node
            || IsKeyDown(VirtualKey.Control)
            || IsKeyDown(VirtualKey.Shift)
            || NodeGrid.SelectedItems.Count != 1
            || !NodeGrid.SelectedItems.Contains(node)
        )
            return;

        BeginNodeEdit(node);
        e.Handled = true;
    }

    private void BeginNodeEdit(MacroNodeViewModel node)
    {
        node.BeginEdit();
        SelectNodes([node]);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (NodeGrid.ContainerFromItem(node) is not DependencyObject container)
                return;
            var textBox = FindDescendant<TextBox>(container, node);
            if (textBox is null)
                return;
            textBox.Focus(FocusState.Programmatic);
            textBox.SelectAll();
        });
    }

    private void EditableNodeTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || sender is not TextBox textBox)
            return;
        CommitEdit(textBox);
        NodeGrid.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void EditableNodeTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
            CommitEdit(textBox);
    }

    private static void CommitEdit(TextBox textBox)
    {
        if (textBox.Tag is MacroNodeViewModel node)
            node.CommitEdit();
    }

    private void Delay_BeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs e)
    {
        // 延时只允许 ASCII 数字，避免提交阶段出现区域设置或格式歧义。
        if (
            sender.Tag is MacroNodeViewModel { IsDelay: true }
            && e.NewText.Any(character => !char.IsAsciiDigit(character))
        )
            e.Cancel = true;
    }

    private static T? FindDescendant<T>(DependencyObject root, MacroNodeViewModel node)
        where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (
                child is T match
                && ReferenceEquals(match.Tag, node)
                && match.Visibility == Visibility.Visible
            )
                return match;
            var descendant = FindDescendant<T>(child, node);
            if (descendant is not null)
                return descendant;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
                return match;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
}
