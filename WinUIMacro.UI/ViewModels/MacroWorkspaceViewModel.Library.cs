using System.ComponentModel;
using WinUIMacro.Contracts;
using WinUIMacro.Contracts.Ipc;

namespace WinUIMacro.UI.ViewModels;

internal partial class MacroWorkspaceViewModel
{
    private string _macroDirectory = string.Empty;
    private bool _isWorkspaceBusy;

    [ObservableProperty]
    public partial MacroEditorViewModel? SelectedMacro { get; set; }

    public bool CanChangeWorkspace => !_isWorkspaceBusy;
    public bool CanCopySelectedMacro => CanEditSequence && SelectedMacro is { IsDirty: false };
    public bool CanSaveSelectedMacro => CanEditSequence && SelectedMacro is { IsDirty: true };

    internal Task InitializeAsync() => ReloadLibraryAsync();

    partial void OnSelectedMacroChanged(
        MacroEditorViewModel? oldValue,
        MacroEditorViewModel? newValue
    )
    {
        if (oldValue is not null)
            oldValue.PropertyChanged -= SelectedMacro_PropertyChanged;
        if (newValue is not null)
            newValue.PropertyChanged += SelectedMacro_PropertyChanged;
        NotifySelectedMacroCommandAvailability();
    }

    private void SelectedMacro_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MacroEditorViewModel.IsDirty))
            NotifySelectedMacroCommandAvailability();
    }

    private void NotifySelectedMacroCommandAvailability()
    {
        OnPropertyChanged(nameof(CanCopySelectedMacro));
        OnPropertyChanged(nameof(CanSaveSelectedMacro));
    }

    [RelayCommand]
    private async Task RefreshMacrosAsync()
    {
        if (!CanChangeWorkspace || Macros.Any(macro => macro.IsDirty))
        {
            if (Macros.Any(macro => macro.IsDirty))
                ShowWarning("请先保存或撤销未保存的宏，再刷新列表。");
            return;
        }

        SetWorkspaceBusy(true);
        try
        {
            await ReloadLibraryAsync(protectLocalChanges: true);
        }
        catch (Exception exception)
        {
            ShowError($"刷新宏列表失败：{exception.Message}");
        }
        finally
        {
            SetWorkspaceBusy(false);
        }
    }

    private async Task ReloadLibraryAsync(bool protectLocalChanges = false)
    {
        var selectedId = SelectedMacro?.Id;
        var loaded = await _client.GetWorkspaceAsync();
        if (protectLocalChanges && Macros.Any(macro => macro.IsDirty))
        {
            ShowWarning("刷新期间宏发生了修改，已保留当前工作区，请重新刷新。");
            return;
        }
        ApplyWorkspace(loaded, selectedId);
        if (loaded.Errors.Count == 0)
            ShowSuccess($"已加载 {Macros.Count} 个宏。");
        else
            ShowWarning(string.Join(Environment.NewLine, loaded.Errors));
    }

    internal async Task<bool> EnsureDetailsLoadedAsync(MacroEditorViewModel macro)
    {
        if (macro.IsDetailsLoaded)
            return true;
        try
        {
            macro.LoadDetails(await _client.GetMacroAsync(macro.Id));
            return true;
        }
        catch (Exception exception)
        {
            ShowError($"加载宏详情失败：{exception.Message}");
            return false;
        }
    }

    [RelayCommand]
    private void AddMacro()
    {
        var name = CreateUniqueName("未命名宏");
        var macro = new MacroEditorViewModel(
            new MacroDefinition(Guid.NewGuid(), name, MacroPlaybackMode.Once, []),
            false
        );
        Macros.Add(macro);
        SelectedMacro = macro;
        ShowInfo("已创建宏草稿，保存后可绑定触发键。");
    }

    [RelayCommand]
    private async Task SaveMacroAsync()
    {
        if (SelectedMacro is not { } macro)
            return;
        try
        {
            EnsureUniqueName(macro);
            var result = await _client.SaveMacroAsync(macro.ToDefinition());
            macro.MarkSaved(result.Macro);
            RefreshBindingDisplay();
            ShowRuntimeUpdateResult(
                $"已保存“{result.Macro.Name}”。",
                "宏已保存，但运行时配置更新失败",
                result.RuntimeError
            );
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    [RelayCommand]
    private async Task CopyMacroAsync()
    {
        if (!CanChangeWorkspace || SelectedMacro is not { } source)
            return;

        SetWorkspaceBusy(true);
        try
        {
            if (!await EnsureDetailsLoadedAsync(source))
                return;

            var copyName = CreateCopyName(Macros, source.Name.Trim());
            var result = await _client.SaveMacroAsync(
                source.ToDefinition() with
                {
                    Id = Guid.NewGuid(),
                    Name = copyName,
                }
            );
            Macros.Add(new MacroEditorViewModel(result.Macro, isPersisted: true));
            RefreshBindingDisplay();
            ShowRuntimeUpdateResult(
                $"已复制为“{result.Macro.Name}”。",
                "宏已复制，但运行时配置更新失败",
                result.RuntimeError
            );
        }
        catch (Exception exception)
        {
            ShowError($"复制宏失败：{exception.Message}");
        }
        finally
        {
            SetWorkspaceBusy(false);
        }
    }

    internal async Task<bool> DeleteSelectedAsync()
    {
        if (SelectedMacro is not { } macro)
            return false;
        try
        {
            var result = await _client.DeleteMacroAsync(macro.Id);
            _bindings = _bindings.Where(binding => binding.MacroId != macro.Id).ToArray();
            var index = Macros.IndexOf(macro);
            Macros.Remove(macro);
            SelectedMacro =
                Macros.Count == 0 ? null : Macros[Math.Clamp(index, 0, Macros.Count - 1)];
            RefreshBindingDisplay();
            ShowRuntimeUpdateResult(
                $"已删除“{macro.Name}”。",
                "宏已删除，但运行时配置更新失败",
                result.RuntimeError
            );
            return true;
        }
        catch (Exception exception)
        {
            try
            {
                var loaded = await _client.GetWorkspaceAsync();
                var macroStillExists = loaded.Macros.Any(summary => summary.Id == macro.Id);
                var macroReadFailed = loaded.Errors.Any(error =>
                    error.StartsWith($"{macro.Name}.json：", StringComparison.OrdinalIgnoreCase)
                );
                ApplyWorkspace(
                    loaded,
                    macro.Id,
                    macroStillExists || macroReadFailed ? macro : null
                );
                if (!macroStillExists && !macroReadFailed)
                {
                    ShowWarning($"删除响应失败，但刷新后确认宏已删除：{exception.Message}");
                    return true;
                }
                ShowError($"删除宏失败：{exception.Message}");
            }
            catch (Exception refreshException)
            {
                RefreshBindingDisplay();
                ShowError(
                    $"删除宏失败：{exception.Message}{Environment.NewLine}刷新工作区失败：{refreshException.Message}"
                );
            }
            return false;
        }
    }

    internal void DiscardChanges(MacroEditorViewModel macro)
    {
        if (macro.IsNew)
        {
            var index = Macros.IndexOf(macro);
            if (index >= 0)
                Macros.RemoveAt(index);
            SelectedMacro =
                Macros.Count == 0 ? null : Macros[Math.Clamp(index, 0, Macros.Count - 1)];
            return;
        }

        macro.RevertToSavedSnapshot();
        ShowInfo($"已撤销“{macro.DisplayName}”的未保存修改。");
    }

    [RelayCommand]
    private void AddDelay() => SelectedMacro?.AddNode(new MacroNode(MacroNodeType.Delay, "100"));

    [RelayCommand]
    private void AddNote() => SelectedMacro?.AddNode(new MacroNode(MacroNodeType.Note, "备注"));

    private void ApplyWorkspace(
        WorkspaceSnapshot loaded,
        Guid? selectedId,
        MacroEditorViewModel? preservedMacro = null
    )
    {
        _bindings = loaded.Bindings;
        _macroDirectory = loaded.MacroDirectory;
        ApplyMacroSummaries(Macros, loaded.Macros, preservedMacro);
        SelectedMacro =
            Macros.FirstOrDefault(macro => macro.Id == selectedId) ?? Macros.FirstOrDefault();
        RefreshBindingDisplay();
    }

    internal static void ApplyMacroSummaries(
        ICollection<MacroEditorViewModel> target,
        IReadOnlyList<MacroSummary> summaries,
        MacroEditorViewModel? preservedMacro = null
    )
    {
        target.Clear();
        var preservedAdded = false;
        foreach (var summary in summaries)
        {
            if (preservedMacro is not null && preservedMacro.Id == summary.Id)
            {
                target.Add(preservedMacro);
                preservedAdded = true;
            }
            else
            {
                target.Add(new MacroEditorViewModel(summary));
            }
        }

        // 文件暂时不可读时快照没有摘要，但删除失败后仍需保留当前编辑对象。
        if (preservedMacro is not null && !preservedAdded)
            target.Add(preservedMacro);
    }

    private void SetWorkspaceBusy(bool value)
    {
        if (!SetProperty(ref _isWorkspaceBusy, value))
            return;
        OnPropertyChanged(nameof(CanChangeWorkspace));
        OnPropertyChanged(nameof(CanEditSequence));
        NotifySelectedMacroCommandAvailability();
    }

    private void SortMacros()
    {
        var sorted = Macros
            .OrderByDescending(macro => macro.IsBound)
            .ThenByDescending(macro => macro.LastEditedAt)
            .ThenBy(macro => macro.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        for (var targetIndex = 0; targetIndex < sorted.Length; targetIndex++)
        {
            var currentIndex = Macros.IndexOf(sorted[targetIndex]);
            if (currentIndex != targetIndex)
                Macros.Move(currentIndex, targetIndex);
        }
    }

    private void EnsureUniqueName(MacroEditorViewModel target)
    {
        if (
            Macros.Any(macro =>
                !ReferenceEquals(macro, target)
                && string.Equals(
                    macro.Name.Trim(),
                    target.Name.Trim(),
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
            throw new InvalidOperationException($"宏名“{target.Name.Trim()}”已经存在。");
    }

    private string CreateUniqueName(string baseName)
    {
        var name = baseName;
        for (
            var index = 2;
            Macros.Any(macro =>
                string.Equals(macro.Name, name, StringComparison.OrdinalIgnoreCase)
            );
            index++
        )
            name = $"{baseName} {index}";
        return name;
    }

    internal static string CreateCopyName(IEnumerable<MacroEditorViewModel> macros, string baseName)
    {
        for (var index = 1; ; index++)
        {
            var name = $"{baseName}-{index}";
            if (
                !macros.Any(macro =>
                    string.Equals(macro.Name, name, StringComparison.OrdinalIgnoreCase)
                )
            )
                return name;
        }
    }
}
