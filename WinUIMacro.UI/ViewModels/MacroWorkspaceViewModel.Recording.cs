using WinUIMacro.Contracts;
using WinUIMacro.Contracts.Ipc;

namespace WinUIMacro.UI.ViewModels;

internal partial class MacroWorkspaceViewModel
{
    private int _suppressRecordButtonClick;
    private Guid? _recordingSessionId;

    [ObservableProperty]
    public partial RecordingState RecordingState { get; set; }

    public bool IsRecording => RecordingState == RecordingState.Recording;

    public bool CanEditSequence => RecordingState == RecordingState.Idle && CanChangeWorkspace;

    public string RecordButtonLabel =>
        RecordingState switch
        {
            RecordingState.Starting => "取消录制",
            RecordingState.Recording => "停止录制",
            RecordingState.Stopping => "正在停止",
            _ => "开始录制",
        };

    public string RecordButtonGlyph => IsRecording ? "\uE71A" : "\uE768";

    partial void OnRecordingStateChanged(RecordingState value)
    {
        OnPropertyChanged(nameof(RecordButtonLabel));
        OnPropertyChanged(nameof(RecordButtonGlyph));
        OnPropertyChanged(nameof(CanEditSequence));
        NotifySelectedMacroCommandAvailability();
    }

    internal async Task ToggleRecordingAsync(DesktopRectangle stopBounds)
    {
        try
        {
            await (
                RecordingState switch
                {
                    RecordingState.Idle when SelectedMacro is { } macro => StartRecordingAsync(
                        macro.Id,
                        stopBounds
                    ),
                    RecordingState.Starting or RecordingState.Recording => StopRecordingAsync(),
                    _ => Task.CompletedTask,
                }
            );
        }
        catch (Exception exception)
        {
            ShowError($"录制操作失败：{exception.Message}");
        }
    }

    internal async Task UpdateRecordingBoundsAsync(DesktopRectangle bounds)
    {
        try
        {
            if (_recordingSessionId is { } sessionId)
                await _client.UpdateRecordingBoundsAsync(sessionId, bounds);
        }
        catch (Exception exception)
        {
            ShowError($"录制操作失败：{exception.Message}");
        }
    }

    internal bool TryConsumeRecordButtonClickSuppression() =>
        Interlocked.Exchange(ref _suppressRecordButtonClick, 0) != 0;

    private async Task StartRecordingAsync(Guid macroId, DesktopRectangle bounds)
    {
        _recordingSessionId = await _client.StartRecordingAsync(macroId, bounds);
    }

    internal async Task StopRecordingAsync()
    {
        await _client.StopRecordingAsync(_recordingSessionId);
        _recordingSessionId = null;
    }

    private void Engine_NodesRecorded(NodeRecordedBatchEvent args) =>
        Dispatch(() => ApplyRecordedNodes(Macros, _recordingSessionId, args));

    internal static void ApplyRecordedNodes(
        IEnumerable<MacroEditorViewModel> macros,
        Guid? recordingSessionId,
        NodeRecordedBatchEvent batch
    )
    {
        foreach (var node in batch.Nodes)
        {
            // 停止请求之后仍可能收到旧事件，用会话 UUID 丢弃它们。
            if (node.RecordingSessionId != recordingSessionId)
                continue;
            var macro = macros.FirstOrDefault(item => item.Id == node.MacroId);
            macro?.AddNode(node.Node);
        }
    }

    private void Engine_RecordingStateChanged(RecordingStateChangedEvent args)
    {
        Dispatch(() =>
        {
            if (args.State == RecordingState.Starting && _recordingSessionId is null)
                _recordingSessionId = args.RecordingSessionId;
            if (args.RecordingSessionId != _recordingSessionId)
                return;
            if (args.SuppressRecordButtonClick)
                Interlocked.Exchange(ref _suppressRecordButtonClick, 1);
            RecordingState = args.State;
            if (args.State == RecordingState.Starting && SelectedMacro is { } macro)
                ShowInfo($"开始录制“{macro.Name}”。");
            else if (args.State == RecordingState.Idle)
            {
                _recordingSessionId = null;
                ShowInfo("录制已停止，请保存宏。");
            }
        });
    }
}
