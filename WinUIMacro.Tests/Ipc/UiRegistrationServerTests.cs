// 验证 Explorer 启动的 UI 通过一次性注册管道向 Host 登记真实 PID。
using System.IO.Pipes;
using FluentAssertions;
using WinUIMacro.Contracts.Ipc;

namespace WinUIMacro.Tests.Ipc;

[TestClass]
public sealed class UiRegistrationServerTests
{
    private static readonly string CurrentExecutablePath =
        Environment.ProcessPath
        ?? throw new InvalidOperationException("无法读取测试进程可执行文件路径。");

    [TestMethod]
    public async Task AcceptAsync_ValidTokenAndExecutable_ReturnsPipeClientProcessId()
    {
        var pipeName = CreatePipeName();
        var token = Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accept = AcceptAsync(pipeName, token, CurrentExecutablePath, timeout.Token);

        var response = await RegisterAsync(pipeName, token, timeout.Token);

        response.Accepted.Should().BeTrue();
        (await accept).Should().Be(Environment.ProcessId);
    }

    [TestMethod]
    public async Task AcceptAsync_NoClientBeforeTimeout_Cancels()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var accepting = () =>
            AcceptAsync(CreatePipeName(), "token", CurrentExecutablePath, timeout.Token);

        await accepting.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public async Task AcceptAsync_WrongTokenThenValidToken_AcceptsValidClient()
    {
        var pipeName = CreatePipeName();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accept = AcceptAsync(pipeName, "expected", CurrentExecutablePath, timeout.Token);

        var rejected = () => RegisterAsync(pipeName, "different", timeout.Token);
        await rejected.Should().ThrowAsync<InvalidOperationException>();
        var response = await RegisterAsync(pipeName, "expected", timeout.Token);

        response.Accepted.Should().BeTrue();
        (await accept).Should().Be(Environment.ProcessId);
    }

    [TestMethod]
    public async Task AcceptAsync_WrongProtocolThenValidRequest_AcceptsValidClient()
    {
        var pipeName = CreatePipeName();
        var token = Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accept = AcceptAsync(pipeName, token, CurrentExecutablePath, timeout.Token);

        var rejected = await SendRawRegistrationAsync(
            pipeName,
            token,
            Protocol.Version + 1,
            timeout.Token
        );
        var response = await RegisterAsync(pipeName, token, timeout.Token);

        rejected.Error.Should().Contain("协议版本不匹配");
        response.Accepted.Should().BeTrue();
        (await accept).Should().Be(Environment.ProcessId);
    }

    [TestMethod]
    public async Task AcceptAsync_MultipleRejectedConnections_ContinuesWaiting()
    {
        var pipeName = CreatePipeName();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accept = AcceptAsync(pipeName, "expected", CurrentExecutablePath, timeout.Token);

        foreach (var token in new[] { "wrong-one", "wrong-two" })
        {
            var rejected = () => RegisterAsync(pipeName, token, timeout.Token);
            await rejected.Should().ThrowAsync<InvalidOperationException>();
        }

        var response = await RegisterAsync(pipeName, "expected", timeout.Token);

        response.Accepted.Should().BeTrue();
        (await accept).Should().Be(Environment.ProcessId);
    }

    [TestMethod]
    public async Task AcceptAsync_UnexpectedExecutablePath_RejectsClientUntilTimeout()
    {
        var pipeName = CreatePipeName();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var expectedPath = Path.Combine(Path.GetTempPath(), "WinUIMacro.UI.exe");
        var accept = AcceptAsync(pipeName, "expected", expectedPath, timeout.Token);

        var registration = () => RegisterAsync(pipeName, "expected", timeout.Token);
        await registration.Should().ThrowAsync<Exception>();
        Func<Task> accepting = async () => await accept;

        await accepting.Should().ThrowAsync<OperationCanceledException>();
    }

    private static Task<int> AcceptAsync(
        string pipeName,
        string token,
        string expectedExecutablePath,
        CancellationToken cancellationToken
    ) =>
        global::WinUIMacro.UiRegistrationServer.AcceptAsync(
            pipeName,
            token,
            expectedExecutablePath,
            cancellationToken,
            // GitHub 的 Windows 托管运行器以提升权限执行测试；这些用例只验证注册协议。
            // 生产调用在普通 Engine 下拒绝提升 UI，在提升 Engine 下允许同级 UI。
            rejectElevatedClient: false
        );

    private static string CreatePipeName() => $"WinUIMacro.Tests.UiRegistration.{Guid.NewGuid():N}";

    private static async Task<UiRegistrationResponse> RegisterAsync(
        string pipeName,
        string token,
        CancellationToken cancellationToken
    )
    {
        await using var stream = CreateClient(pipeName);
        await stream.ConnectAsync(cancellationToken);
        await using var connection = new PipeConnection(stream);
        var registration = connection.SendRequestAsync<
            UiRegistrationRequest,
            UiRegistrationResponse
        >(
            IpcMessageType.UiRegistration,
            new UiRegistrationRequest(token),
            cancellationToken: cancellationToken
        );
        var response = await connection.ReadAsync(cancellationToken);
        if (response is null)
            connection.FailPendingRequests(new EndOfStreamException("注册管道已断开。"));
        else
            connection.TryHandleResponse(response).Should().BeTrue();
        return await registration;
    }

    private static async Task<MessageEnvelope> SendRawRegistrationAsync(
        string pipeName,
        string token,
        int protocolVersion,
        CancellationToken cancellationToken
    )
    {
        await using var stream = CreateClient(pipeName);
        await stream.ConnectAsync(cancellationToken);
        await using var connection = new PipeConnection(stream);
        await connection.SendAsync(
            new MessageEnvelope(
                protocolVersion,
                MessageKind.Request,
                IpcMessageType.UiRegistration,
                Guid.NewGuid(),
                PipeConnection.ToPayload(new UiRegistrationRequest(token)),
                null
            ),
            cancellationToken
        );
        return await connection.ReadAsync(cancellationToken)
            ?? throw new EndOfStreamException("注册管道未返回拒绝响应。");
    }

    private static NamedPipeClientStream CreateClient(string pipeName) =>
        new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
}
