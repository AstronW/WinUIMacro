// 创建 UI 进程、连接引擎会话并处理应用级生命周期。
using Microsoft.UI.Dispatching;
using WinUIMacro.Contracts.Ipc;
using WinUIMacro.Engine.Win32.Window;
using WinUIMacro.UI.Services;

namespace WinUIMacro.UI;

public sealed partial class App : Application
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private EnginePipeClient? _client;
    private MacroWorkspaceViewModel? _viewModel;
    private MainWindow? _window;
    private int _exiting;

    public App()
    {
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var arguments = Environment.GetCommandLineArgs();
            var enginePipeName = GetRequiredArgument(arguments, Protocol.EnginePipeArgument);
            var registrationPipeName = GetRequiredArgument(
                arguments,
                Protocol.UiRegistrationPipeArgument
            );
            var registrationToken = GetRequiredArgument(
                arguments,
                Protocol.UiRegistrationTokenArgument
            );
            using var connectTimeout = new CancellationTokenSource(Protocol.UiConnectionTimeout);
            // Shell 启动无法直接返回 PID；先让 Host 从注册管道读取真实客户端 PID。
            await UiRegistrationClient.RegisterAsync(
                registrationPipeName,
                registrationToken,
                connectTimeout.Token
            );
            _client = new EnginePipeClient(enginePipeName);
            // 完成 PID 注册后再握手，确保 Engine 只接受本次 Shell 启动的 UI。
            var hello = await _client.ConnectAsync(connectTimeout.Token);
            if (!hello.Accepted)
            {
                await _client.DisposeAsync();
                _client = null;
                Exit();
                return;
            }

            // 连接成功后才订阅事件；关闭时由 ExitUiAsync 对称解除订阅。
            _client.ActivateUiRequested += OnActivateUiRequested;
            _client.Disconnected += OnDisconnected;
            _client.HostShutdownApproved += OnHostShutdownApproved;
            _client.PrepareUiShutdownRequested += OnPrepareUiShutdownRequested;
            _viewModel = new MacroWorkspaceViewModel(_client, _dispatcherQueue);
            await _viewModel.InitializeAsync();

            var window = new MainWindow(_viewModel);
            window.Closed += OnWindowClosed;
            _window = window;
            window.Activate();
        }
        catch (Exception exception)
        {
            ShowMessage(
                exception is OperationCanceledException
                    ? "无法连接 WinUIMacro Engine。请从 WinUIMacro.exe 启动应用。"
                    : exception.Message,
                "WinUIMacro.UI 启动失败"
            );
            await ExitUiAsync();
        }
    }

    private Task<bool> OnPrepareUiShutdownRequested(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        if (
            !_dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    completion.TrySetResult(_window is null || await _window.PrepareForExitAsync());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            })
        )
            completion.TrySetResult(false);
        return completion.Task.WaitAsync(cancellationToken);
    }

    private bool OnActivateUiRequested()
    {
        var window = _window;
        return window is not null && _dispatcherQueue.TryEnqueue(window.Activate);
    }

    private void OnDisconnected(Exception? exception) =>
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (Volatile.Read(ref _exiting) != 0)
                return;
            ShowMessage("与 WinUIMacro Engine 的连接已断开，UI 将退出。", "WinUIMacro");
            _window?.CloseForShutdown();
            _ = ExitUiAsync();
        });

    private void OnHostShutdownApproved() =>
        _dispatcherQueue.TryEnqueue(() => _window?.CloseForShutdown());

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is Window window)
            window.Closed -= OnWindowClosed;
        if (sender is MainWindow mainWindow)
            mainWindow.Dispose();
        _window = null;
        _ = ExitUiAsync();
    }

    private async Task ExitUiAsync()
    {
        if (Interlocked.Exchange(ref _exiting, 1) != 0)
            return;
        if (_client is not null)
        {
            _client.ActivateUiRequested -= OnActivateUiRequested;
            _client.Disconnected -= OnDisconnected;
            _client.HostShutdownApproved -= OnHostShutdownApproved;
            _client.PrepareUiShutdownRequested -= OnPrepareUiShutdownRequested;
        }
        _viewModel?.Dispose();
        _viewModel = null;
        if (_client is not null)
            await _client.DisposeAsync();
        _client = null;
        Exit();
    }

    private static void ShowMessage(string message, string title) =>
        NativeMessageBox.ShowError(message, title);

    private static string GetRequiredArgument(string[] arguments, string option)
    {
        var optionIndex = Array.IndexOf(arguments, option);
        if (
            optionIndex < 0
            || optionIndex == arguments.Length - 1
            || string.IsNullOrWhiteSpace(arguments[optionIndex + 1])
        )
            throw new InvalidOperationException(
                $"缺少启动参数 {option}。请从 WinUIMacro.exe 启动应用。"
            );
        return arguments[optionIndex + 1];
    }
}
