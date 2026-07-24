// 管理引擎宿主进程、UI 子进程、托盘服务和退出流程。
using System.Diagnostics;
using System.Security.Cryptography;
using WinUIMacro.Contracts.Ipc;
using WinUIMacro.Engine;
using WinUIMacro.Engine.Win32.Shell;

namespace WinUIMacro;

internal sealed class HostApplication(IpcEndpointNames names, bool engineIsElevated)
    : IAsyncDisposable
{
    private readonly EngineRuntime _engine = new();
    private readonly TaskCompletionSource _exit = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly SemaphoreSlim _exitLock = new(1, 1);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly object _uiLaunchLock = new();
    private readonly string _uiDirectory = Path.Combine(AppContext.BaseDirectory, "App");
    private readonly string _uiPath = Path.Combine(
        AppContext.BaseDirectory,
        "App",
        "WinUIMacro.UI.exe"
    );
    private readonly string _uiPipeName = $"{names.UiPipe}.{Guid.NewGuid():N}";
    private EngineWorkspace? _workspace;
    private EnginePipeServer? _uiPipe;
    private ControlPipeServer? _controlPipe;
    private Process? _uiProcess;
    private Task _uiLaunchTask = Task.CompletedTask;
    private bool _disposed;

    internal async Task RunAsync()
    {
        // 先启动引擎并加载工作区，再开放 UI/控制管道，避免 UI 看到半初始化状态。
        _engine.OpenUiRequested += OnOpenUiRequested;
        _engine.ExitRequested += OnExitRequested;
        await _engine.StartAsync();

        _workspace = new EngineWorkspace(_engine);
        await _workspace.LoadAsync();
        _uiPipe = new EnginePipeServer(_uiPipeName, _engine, _workspace);
        _controlPipe = new ControlPipeServer(names.ControlPipe, engineIsElevated, RequestOpenUi);
        _uiPipe.Start();
        _controlPipe.Start();
        RequestOpenUi();

        _ = ObserveInfrastructureAsync(_uiPipe.Completion, "UI Pipe");
        _ = ObserveInfrastructureAsync(_controlPipe.Completion, "Control Pipe");
        var completed = await Task.WhenAny(_exit.Task, _engine.Completion);
        if (!ReferenceEquals(completed, _exit.Task))
            await completed;
    }

    public async ValueTask DisposeAsync()
    {
        Task uiLaunchTask;
        lock (_uiLaunchLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            uiLaunchTask = _uiLaunchTask;
        }
        _shutdownCancellation.Cancel();
        _engine.OpenUiRequested -= OnOpenUiRequested;
        _engine.ExitRequested -= OnExitRequested;
        await uiLaunchTask;
        if (_controlPipe is not null)
            await _controlPipe.DisposeAsync();
        if (_uiPipe is not null)
            await _uiPipe.DisposeAsync();
        _uiProcess?.Dispose();
        await _engine.StopAsync();
        _shutdownCancellation.Dispose();
        _exitLock.Dispose();
    }

    private void RequestOpenUi()
    {
        lock (_uiLaunchLock)
        {
            if (_disposed || !_uiLaunchTask.IsCompleted)
                return;
            _uiLaunchTask = OpenUiAsync(_shutdownCancellation.Token);
        }
    }

    private async Task OpenUiAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_uiPipe is null || !await _uiPipe.TryActivateUiAsync())
                await LaunchUiAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _workspace?.RecordError(exception.Message);
            _ = Task.Run(() =>
                Program.ShowMessage(
                    $"{exception.Message}\n\nEngine 仍在后台运行，可以从托盘重新打开主窗口。",
                    "无法打开 WinUIMacro UI"
                )
            );
        }
    }

    private async Task LaunchUiAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_uiPath))
            throw new FileNotFoundException("找不到 WinUIMacro.UI.exe。", _uiPath);
        var uiPipe = _uiPipe ?? throw new InvalidOperationException("UI 管道尚未启动。");
        // 获取启动权后再次检查，覆盖旧会话关闭和新会话接入之间的竞争窗口。
        if (await uiPipe.TryActivateUiAsync())
            return;
        using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        connectionTimeout.CancelAfter(Protocol.UiConnectionTimeout);
        var uiConnected = uiPipe.WaitForUiConnectedAsync(connectionTimeout.Token);
        try
        {
            var registrationPipeName = $"{_uiPipeName}.Registration.{Guid.NewGuid():N}";
            var registrationToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var registration = UiRegistrationServer.AcceptAsync(
                registrationPipeName,
                registrationToken,
                _uiPath,
                connectionTimeout.Token,
                // 普通桌面仍只接受 Medium UI；Sandbox 等提升桌面允许与提升 Engine 同级的 UI。
                rejectElevatedClient: !engineIsElevated
            );
            var arguments = string.Join(
                ' ',
                Protocol.EnginePipeArgument,
                QuoteArgument(_uiPipeName),
                Protocol.UiRegistrationPipeArgument,
                QuoteArgument(registrationPipeName),
                Protocol.UiRegistrationTokenArgument,
                QuoteArgument(registrationToken)
            );
            await ExplorerShellLauncher.LaunchAsync(
                _uiPath,
                arguments,
                _uiDirectory,
                connectionTimeout.Token
            );

            var process = Process.GetProcessById(await registration);
            try
            {
                // 立即打开并保留进程句柄，避免 UI 生命周期内 PID 被复用。
                _ = process.Handle;
                uiPipe.RegisterExpectedClient(process.Id);
                _uiProcess?.Dispose();
                _uiProcess = process;
            }
            catch
            {
                process.Dispose();
                throw;
            }
            await uiConnected;
        }
        catch (OperationCanceledException)
            when (connectionTimeout.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
            )
        {
            throw new TimeoutException("WinUIMacro UI 未能及时连接 Engine。");
        }
        catch
        {
            connectionTimeout.Cancel();
            throw;
        }
    }

    private async Task ObserveInfrastructureAsync(Task completion, string name)
    {
        try
        {
            await completion;
            if (!_disposed)
                _workspace?.RecordError($"{name} 意外停止，Engine 将继续运行。");
        }
        catch (Exception exception)
        {
            if (!_disposed)
                _workspace?.RecordError($"{name} 已停止：{exception.Message}");
        }
    }

    private static string QuoteArgument(string value)
    {
        if (value.Contains('"'))
            throw new InvalidOperationException("UI 启动参数包含不受支持的引号。");
        return $"\"{value}\"";
    }

    private async Task RequestExitAsync()
    {
        await _exitLock.WaitAsync();
        try
        {
            // 退出前先让 UI 保存/撤销未完成编辑；只有 UI 同意后才结束宿主。
            if (_exit.Task.IsCompleted)
                return;
            if (_uiPipe is not null && !await _uiPipe.PrepareUiShutdownAsync())
                return;
            _exit.TrySetResult();
        }
        catch (Exception exception)
        {
            _workspace?.RecordError(exception.Message);
            Program.ShowMessage(exception.Message, "无法退出 WinUIMacro");
        }
        finally
        {
            _exitLock.Release();
        }
    }

    private void OnOpenUiRequested() => RequestOpenUi();

    private void OnExitRequested() => _ = RequestExitAsync();
}
