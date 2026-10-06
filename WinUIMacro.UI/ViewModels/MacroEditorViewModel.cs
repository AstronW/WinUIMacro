// 管理单个宏的编辑状态、节点集合、保存快照和脏状态。
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using WinUIMacro.Contracts;

namespace WinUIMacro.UI.ViewModels;

internal partial class MacroEditorViewModel : ObservableObject
{
    private readonly string _initialName;
    private readonly Dictionary<MacroNodeViewModel, long> _nodeDelays = [];
    private MacroNode[] _copiedNodes = [];
    private MacroDefinition? _savedSnapshot;
    private long _totalDelayMilliseconds;
    private bool _isApplyingDefinition;
    private bool _isPersisted;

    public MacroEditorViewModel(MacroDefinition definition, bool isPersisted)
    {
        _isApplyingDefinition = true;
        Id = definition.Id;
        _initialName = definition.Name;
        Name = definition.Name;
        ModeIndex = (int)definition.Mode;
        LastEditedAt = definition.LastEditedAt;
        _isPersisted = isPersisted;
        _savedSnapshot = isPersisted ? definition : null;
        IsDetailsLoaded = true;
        Nodes.CollectionChanged += Nodes_CollectionChanged;
        foreach (var node in definition.Nodes)
            Nodes.Add(new MacroNodeViewModel(node.Type, node.Value));
        _isApplyingDefinition = false;
        IsDirty = !isPersisted;
    }

    public MacroEditorViewModel(MacroSummary summary)
    {
        _isApplyingDefinition = true;
        Id = summary.Id;
        _initialName = summary.Name;
        Name = summary.Name;
        LastEditedAt = summary.LastEditedAt;
        _isPersisted = true;
        Nodes.CollectionChanged += Nodes_CollectionChanged;
        _isApplyingDefinition = false;
    }

    public Guid Id { get; }
    public ObservableCollection<MacroNodeViewModel> Nodes { get; } = [];
    public DateTimeOffset LastEditedAt { get; private set; }
    public bool IsPersisted => _isPersisted;
    public bool IsNew => !_isPersisted;
    public bool IsDetailsLoaded { get; private set; }
    public bool IsEmptyUnmodifiedDraft =>
        IsNew
        && Nodes.Count == 0
        && string.Equals(Name, _initialName, StringComparison.Ordinal)
        && ModeIndex == (int)MacroPlaybackMode.Once;
    public bool HasNoNodes => Nodes.Count == 0;
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "未命名宏" : Name;
    public string TotalDurationDisplay =>
        MacroValueFormatter.FormatDuration(_totalDelayMilliseconds);

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial int ModeIndex { get; set; }

    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    public partial string BindingSummary { get; set; } = "未绑定";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoundBackgroundOpacity))]
    public partial bool IsBound { get; set; }

    public double BoundBackgroundOpacity => IsBound ? 0.18 : 0;

    partial void OnNameChanged(string value)
    {
        if (!_isApplyingDefinition)
            IsDirty = true;
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnModeIndexChanged(int value)
    {
        if (!_isApplyingDefinition)
            IsDirty = true;
    }

    public MacroDefinition ToDefinition()
    {
        if (!IsDetailsLoaded)
            throw new InvalidOperationException("保存宏前必须先加载完整详情。");
        CommitActiveEdits();
        return new MacroDefinition(
            Id,
            Name,
            (MacroPlaybackMode)ModeIndex,
            [.. Nodes.Select(node => node.ToModel())],
            LastEditedAt
        );
    }

    public void CommitActiveEdits()
    {
        // 离开详情页或保存前提交编辑缓冲，保证脏状态和持久化值都反映当前输入。
        foreach (var node in Nodes.Where(node => node.IsEditing))
            node.CommitEdit();
    }

    public void MarkSaved(MacroDefinition definition)
    {
        _isApplyingDefinition = true;
        _savedSnapshot = definition;
        _isPersisted = true;
        LastEditedAt = definition.LastEditedAt;
        if (!string.Equals(Name, definition.Name, StringComparison.Ordinal))
            Name = definition.Name;
        _isApplyingDefinition = false;
        IsDirty = false;
        OnPropertyChanged(nameof(IsPersisted));
        OnPropertyChanged(nameof(IsNew));
    }

    public void LoadDetails(MacroDefinition definition)
    {
        if (definition.Id != Id)
            throw new InvalidDataException("加载的宏详情 UUID 与摘要不一致。");
        if (IsDirty)
            throw new InvalidOperationException("不能用磁盘详情覆盖未保存的宏修改。");

        _isApplyingDefinition = true;
        Name = definition.Name;
        ModeIndex = (int)definition.Mode;
        LastEditedAt = definition.LastEditedAt;
        Nodes.Clear();
        foreach (var node in definition.Nodes)
            Nodes.Add(new MacroNodeViewModel(node.Type, node.Value));
        _savedSnapshot = definition;
        _isPersisted = true;
        IsDetailsLoaded = true;
        _isApplyingDefinition = false;
        IsDirty = false;
        OnPropertyChanged(nameof(IsDetailsLoaded));
    }

    public void AddNode(MacroNode node) => Nodes.Add(new MacroNodeViewModel(node.Type, node.Value));

    public void CopyNodes(IEnumerable<MacroNodeViewModel> nodes)
    {
        var copiedNodes = nodes
            .Distinct()
            .Select(node => (Node: node, Index: Nodes.IndexOf(node)))
            .Where(item => item.Index >= 0)
            .OrderBy(item => item.Index)
            .Select(item => item.Node.ToModel())
            .ToArray();
        if (copiedNodes.Length > 0)
            _copiedNodes = copiedNodes;
    }

    public void ClearCopiedNodes() => _copiedNodes = [];

    public MacroNodeViewModel[] PasteNodesAfterSelection(
        IEnumerable<MacroNodeViewModel> selectedNodes
    )
    {
        if (_copiedNodes.Length == 0)
            return [];

        var selectedIndexes = selectedNodes
            .Distinct()
            .Select(Nodes.IndexOf)
            .Where(index => index >= 0)
            .ToArray();
        var insertIndex = selectedIndexes.Length == 0 ? Nodes.Count : selectedIndexes.Max() + 1;
        var pastedNodes = _copiedNodes
            .Select(node => new MacroNodeViewModel(node.Type, node.Value))
            .ToArray();
        foreach (var node in pastedNodes)
            Nodes.Insert(insertIndex++, node);
        return pastedNodes;
    }

    public int DeleteNodes(IEnumerable<MacroNodeViewModel> nodes)
    {
        var indexes = nodes
            .Distinct()
            .Select(Nodes.IndexOf)
            .Where(index => index >= 0)
            .OrderDescending()
            .ToArray();
        foreach (var index in indexes)
            Nodes.RemoveAt(index);
        return indexes.Length == 0 ? -1 : indexes[^1];
    }

    public void MoveNodes(IReadOnlyList<MacroNodeViewModel> nodes, int targetIndex)
    {
        var indexedNodes = nodes
            .Distinct()
            .Select(node => (Node: node, Index: Nodes.IndexOf(node)))
            .Where(item => item.Index >= 0)
            .OrderBy(item => item.Index)
            .ToArray();
        if (indexedNodes.Length == 0)
            return;

        targetIndex = Math.Clamp(targetIndex, 0, Nodes.Count);
        var insertionIndex = targetIndex - indexedNodes.Count(item => item.Index < targetIndex);
        foreach (var item in indexedNodes.OrderByDescending(item => item.Index))
            Nodes.RemoveAt(item.Index);
        foreach (var item in indexedNodes)
            Nodes.Insert(insertionIndex++, item.Node);
    }

    public void RevertToSavedSnapshot()
    {
        if (_savedSnapshot is null)
            return;
        _isApplyingDefinition = true;
        Name = _savedSnapshot.Name;
        ModeIndex = (int)_savedSnapshot.Mode;
        Nodes.Clear();
        foreach (var node in _savedSnapshot.Nodes)
            Nodes.Add(new MacroNodeViewModel(node.Type, node.Value));
        _isApplyingDefinition = false;
        IsDirty = false;
    }

    private void Nodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        var previousTotal = _totalDelayMilliseconds;
        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var node in _nodeDelays.Keys)
                node.PropertyChanged -= Node_PropertyChanged;
            _nodeDelays.Clear();
            _totalDelayMilliseconds = 0;
        }
        else
        {
            // 节点增删只增量更新延时缓存，避免每次修改都重新扫描整个序列。
            if (args.OldItems is not null)
                foreach (MacroNodeViewModel node in args.OldItems)
                {
                    node.PropertyChanged -= Node_PropertyChanged;
                    _nodeDelays.Remove(node, out var delay);
                    _totalDelayMilliseconds -= delay;
                }
            if (args.NewItems is not null)
                foreach (MacroNodeViewModel node in args.NewItems)
                {
                    node.PropertyChanged += Node_PropertyChanged;
                    var delay = node.DelayMilliseconds;
                    _nodeDelays[node] = delay;
                    _totalDelayMilliseconds += delay;
                }
        }
        if (!_isApplyingDefinition)
            IsDirty = true;
        if (_totalDelayMilliseconds != previousTotal)
            OnPropertyChanged(nameof(TotalDurationDisplay));
        OnPropertyChanged(nameof(HasNoNodes));
    }

    private void Node_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (
            args.PropertyName != nameof(MacroNodeViewModel.Value)
            || sender is not MacroNodeViewModel node
        )
            return;

        IsDirty = true;
        var previousDelay = _nodeDelays[node];
        var currentDelay = node.DelayMilliseconds;
        // 延时文本变化时只修正差值，让总时长显示保持常数时间更新。
        if (currentDelay != previousDelay)
        {
            _nodeDelays[node] = currentDelay;
            _totalDelayMilliseconds += currentDelay - previousDelay;
            OnPropertyChanged(nameof(TotalDurationDisplay));
        }
    }
}
