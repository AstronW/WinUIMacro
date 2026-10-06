using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using WinUIMacro.Contracts.Ipc;
using WinUIMacro.UI.Services;

namespace WinUIMacro.UI.ViewModels;

internal partial class MacroWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly EnginePipeClient _client;
    private readonly DispatcherQueueTimer _messageTimer;
    private bool _disposed;

    internal MacroWorkspaceViewModel(EnginePipeClient client, DispatcherQueue dispatcherQueue)
    {
        _client = client;
        _dispatcherQueue = dispatcherQueue;
        _messageTimer = dispatcherQueue.CreateTimer();
        _messageTimer.Interval = TimeSpan.FromSeconds(4);
        _messageTimer.IsRepeating = false;
        _messageTimer.Tick += MessageTimer_Tick;
        _client.NodesRecorded += Engine_NodesRecorded;
        _client.RecordingStateChanged += Engine_RecordingStateChanged;
        _client.PlaybackFailed += Engine_PlaybackFailed;
        _client.ProcessingFailed += Engine_ProcessingFailed;
        Macros.CollectionChanged += Macros_CollectionChanged;
        BuildBindingKeys();
    }

    public ObservableCollection<MacroEditorViewModel> Macros { get; } = [];

    public bool HasNoMacros => Macros.Count == 0;

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity MessageSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial bool IsMessageOpen { get; set; }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _messageTimer.Stop();
        _messageTimer.Tick -= MessageTimer_Tick;
        _client.NodesRecorded -= Engine_NodesRecorded;
        _client.RecordingStateChanged -= Engine_RecordingStateChanged;
        _client.PlaybackFailed -= Engine_PlaybackFailed;
        _client.ProcessingFailed -= Engine_ProcessingFailed;
        Macros.CollectionChanged -= Macros_CollectionChanged;
    }

    private void Macros_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasNoMacros));

    private void Engine_PlaybackFailed(RuntimeFailureEvent args) =>
        Dispatch(() => ShowError($"回放“{args.MacroName}”失败：{args.Message}"));

    private void Engine_ProcessingFailed(RuntimeFailureEvent args) =>
        Dispatch(() => ShowError($"输入处理失败：{args.Message}"));

    private void ShowInfo(string message, bool autoClose = true) =>
        ShowMessage(message, InfoBarSeverity.Informational, autoClose);

    private void ShowSuccess(string message) =>
        ShowMessage(message, InfoBarSeverity.Success, autoClose: true);

    private void ShowWarning(string message) =>
        ShowMessage(message, InfoBarSeverity.Warning, autoClose: false);

    internal void ShowError(string message) =>
        ShowMessage(message, InfoBarSeverity.Error, autoClose: false);

    private void ShowRuntimeUpdateResult(
        string successMessage,
        string warningPrefix,
        string? runtimeError
    )
    {
        if (string.IsNullOrEmpty(runtimeError))
            ShowSuccess(successMessage);
        else
            ShowWarning($"{warningPrefix}：{runtimeError}");
    }

    private void ShowMessage(string message, InfoBarSeverity severity, bool autoClose)
    {
        _messageTimer.Stop();
        Message = message;
        MessageSeverity = severity;
        IsMessageOpen = true;
        if (autoClose)
            _messageTimer.Start();
    }

    private void MessageTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        IsMessageOpen = false;
    }

    private void Dispatch(Action action)
    {
        if (_disposed)
            return;

        // IPC 事件来自读线程，可绑定状态必须切回 WinUI 调度队列。
        if (_dispatcherQueue.HasThreadAccess)
            action();
        else
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed)
                    action();
            });
    }
}
