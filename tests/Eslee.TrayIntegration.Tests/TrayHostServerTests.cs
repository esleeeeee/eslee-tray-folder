using System.IO.Pipes;
using System.Text;
using Eslee.TrayIntegration;

namespace Eslee.TrayIntegration.Tests;

[TestClass]
public sealed class TrayHostServerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task RegistersClientSendsModeAndRoundTripsActivateCommand()
    {
        var pipeName = CreatePipeName();
        using var server = new TrayHostServer(pipeName);
        var registered = new TaskCompletionSource<TrayAppRegistration>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientRegistered += (_, registration) => registered.TrySetResult(registration);
        server.Start();

        var client = CreateClient(pipeName);
        await using (client.ConfigureAwait(false))
        {
            await client.ConnectAsync(5000);
            using var reader = CreateReader(client);
            var writer = CreateWriter(client);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteLineAsync(
                    """{"type":"register","protocolVersion":1,"appId":"eslee.autopower","displayName":"eslee Auto Power","processId":4321,"mode":"standalone"}""");

                var registration = await registered.Task.WaitAsync(TestTimeout);
                Assert.AreEqual("eslee.autopower", registration.AppId);
                Assert.AreEqual(4321, registration.ProcessId);
                Assert.IsTrue(server.IsClientConnected);

                Assert.IsTrue(await server.SendTrayModeAsync(TrayMode.Hosted, CancellationToken.None));
                var modeLine = await reader.ReadLineAsync().WaitAsync(TestTimeout);
                Assert.IsNotNull(modeLine);
                StringAssert.Contains(modeLine, "\"set-tray-mode\"");
                StringAssert.Contains(modeLine, "\"hosted\"");

                var commandTask = server.SendCommandAsync(
                    TrayHostCommand.Activate, TestTimeout, CancellationToken.None);
                var commandLine = await reader.ReadLineAsync().WaitAsync(TestTimeout);
                Assert.IsNotNull(commandLine);
                var command = TrayPipeProtocol.TryDeserialize(commandLine);
                Assert.IsNotNull(command);
                Assert.AreEqual(TrayPipeProtocol.CommandType, command.Type);
                Assert.AreEqual(TrayPipeProtocol.ActivateCommandValue, command.Command);
                Assert.IsNotNull(command.Id);

                await writer.WriteLineAsync(TrayPipeProtocol.Serialize(new TrayPipeMessage
                {
                    Type = TrayPipeProtocol.CommandResultType,
                    Id = command.Id,
                    Succeeded = true,
                }));
                var result = await commandTask.WaitAsync(TestTimeout);
                Assert.IsTrue(result.Succeeded);
            }
        }
    }

    [TestMethod]
    public async Task GetMenuRoundTripsItemsAndMenuActionCommand()
    {
        var pipeName = CreatePipeName();
        using var server = new TrayHostServer(pipeName);
        var registered = new TaskCompletionSource<TrayAppRegistration>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientRegistered += (_, registration) => registered.TrySetResult(registration);
        server.Start();

        var client = CreateClient(pipeName);
        await using (client.ConfigureAwait(false))
        {
            await client.ConnectAsync(5000);
            using var reader = CreateReader(client);
            var writer = CreateWriter(client);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteLineAsync(
                    """{"type":"register","protocolVersion":1,"appId":"eslee.autopower","processId":77}""");
                await registered.Task.WaitAsync(TestTimeout);

                // 메뉴 요청: 호스트가 get-menu를 보내고 클라이언트 응답의 items를 돌려받습니다.
                var menuTask = server.GetMenuAsync(TestTimeout, CancellationToken.None);
                var menuRequestLine = await reader.ReadLineAsync().WaitAsync(TestTimeout);
                Assert.IsNotNull(menuRequestLine);
                var menuRequest = TrayPipeProtocol.TryDeserialize(menuRequestLine);
                Assert.IsNotNull(menuRequest);
                Assert.AreEqual(TrayPipeProtocol.GetMenuType, menuRequest.Type);
                Assert.IsNotNull(menuRequest.Id);

                await writer.WriteLineAsync(TrayPipeProtocol.Serialize(new TrayPipeMessage
                {
                    Type = TrayPipeProtocol.MenuType,
                    Id = menuRequest.Id,
                    Items = TrayPipeProtocol.ToMenuPayloads(
                    [
                        TrayMenuItem.Action("quick-shutdown-1h", "1시간 후 완전 종료"),
                        TrayMenuItem.Separator,
                        TrayMenuItem.Action("next-schedule", "다음 예약: 없음", enabled: false),
                    ]),
                }));
                var menu = await menuTask.WaitAsync(TestTimeout);
                Assert.IsNotNull(menu);
                Assert.HasCount(3, menu);
                Assert.AreEqual("quick-shutdown-1h", menu[0].Id);
                Assert.IsTrue(menu[0].Enabled);
                Assert.IsTrue(menu[1].IsSeparator);
                Assert.IsFalse(menu[2].Enabled);

                // 메뉴 액션: action id가 command로 전달되고 결과가 돌아옵니다.
                var actionTask = server.SendMenuActionAsync("quick-shutdown-1h", TestTimeout, CancellationToken.None);
                var actionLine = await reader.ReadLineAsync().WaitAsync(TestTimeout);
                Assert.IsNotNull(actionLine);
                var action = TrayPipeProtocol.TryDeserialize(actionLine);
                Assert.IsNotNull(action);
                Assert.AreEqual(TrayPipeProtocol.CommandType, action.Type);
                Assert.AreEqual(TrayPipeProtocol.MenuActionCommandValue, action.Command);
                Assert.AreEqual("quick-shutdown-1h", action.ActionId);
                Assert.IsNotNull(action.Id);

                await writer.WriteLineAsync(TrayPipeProtocol.Serialize(new TrayPipeMessage
                {
                    Type = TrayPipeProtocol.CommandResultType,
                    Id = action.Id,
                    Succeeded = true,
                }));
                var actionResult = await actionTask.WaitAsync(TestTimeout);
                Assert.IsTrue(actionResult.Succeeded);
            }
        }
    }

    [TestMethod]
    public async Task GetMenuReturnsNullWhenClientDoesNotAnswer()
    {
        var pipeName = CreatePipeName();
        using var server = new TrayHostServer(pipeName);
        var registered = new TaskCompletionSource<TrayAppRegistration>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientRegistered += (_, registration) => registered.TrySetResult(registration);
        server.Start();

        var client = CreateClient(pipeName);
        await using (client.ConfigureAwait(false))
        {
            await client.ConnectAsync(5000);
            var writer = CreateWriter(client);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteLineAsync(
                    """{"type":"register","protocolVersion":1,"appId":"eslee.autopower","processId":78}""");
                await registered.Task.WaitAsync(TestTimeout);

                // 메뉴를 지원하지 않는 구버전 앱은 get-menu를 무시합니다.
                var menu = await server.GetMenuAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None);
                Assert.IsNull(menu);
            }
        }
    }

    [TestMethod]
    public async Task GetMenuWithoutClientReturnsNull()
    {
        using var server = new TrayHostServer(CreatePipeName());
        server.Start();

        Assert.IsNull(await server.GetMenuAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [TestMethod]
    public async Task RejectsRegistrationWithWrongProtocolVersion()
    {
        var pipeName = CreatePipeName();
        using var server = new TrayHostServer(pipeName);
        var registeredRaised = false;
        server.ClientRegistered += (_, _) => registeredRaised = true;
        server.Start();

        var client = CreateClient(pipeName);
        await using (client.ConfigureAwait(false))
        {
            await client.ConnectAsync(5000);
            using var reader = CreateReader(client);
            var writer = CreateWriter(client);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteLineAsync(
                    """{"type":"register","protocolVersion":2,"appId":"eslee.autopower","processId":10}""");

                string? line = null;
                try
                {
                    line = await reader.ReadLineAsync().WaitAsync(TestTimeout);
                }
                catch (IOException)
                {
                    // 서버가 연결을 닫으면 파이프에 따라 EOF 대신 IOException이 옵니다.
                }

                Assert.IsNull(line);
                Assert.IsFalse(registeredRaised);
                Assert.IsFalse(server.IsClientConnected);
            }
        }
    }

    [TestMethod]
    public async Task SendCommandWithoutClientReturnsFailure()
    {
        using var server = new TrayHostServer(CreatePipeName());
        server.Start();

        var result = await server.SendCommandAsync(
            TrayHostCommand.Activate, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(server.IsClientConnected);
    }

    [TestMethod]
    public async Task RaisesDisconnectedAndAcceptsNewClientAfterwards()
    {
        var pipeName = CreatePipeName();
        using var server = new TrayHostServer(pipeName);
        var registeredSignal = new SemaphoreSlim(0);
        var disconnectedSignal = new SemaphoreSlim(0);
        var registeredCount = 0;
        server.ClientRegistered += (_, _) =>
        {
            Interlocked.Increment(ref registeredCount);
            registeredSignal.Release();
        };
        server.ClientDisconnected += (_, _) => disconnectedSignal.Release();
        server.Start();

        var firstClient = CreateClient(pipeName);
        await using (firstClient.ConfigureAwait(false))
        {
            await firstClient.ConnectAsync(5000);
            var writer = CreateWriter(firstClient);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteLineAsync(
                    """{"type":"register","protocolVersion":1,"appId":"eslee.autopower","processId":11}""");
                Assert.IsTrue(await registeredSignal.WaitAsync(TestTimeout));
            }
        }

        Assert.IsTrue(await disconnectedSignal.WaitAsync(TestTimeout));
        Assert.IsFalse(server.IsClientConnected);

        var secondClient = CreateClient(pipeName);
        await using (secondClient.ConfigureAwait(false))
        {
            await secondClient.ConnectAsync(5000);
            var writer = CreateWriter(secondClient);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteLineAsync(
                    """{"type":"register","protocolVersion":1,"appId":"eslee.autopower","processId":12}""");
                Assert.IsTrue(await registeredSignal.WaitAsync(TestTimeout));
            }
        }

        Assert.AreEqual(2, registeredCount);
    }

    private static string CreatePipeName() =>
        "eslee.trayfolder.server-test." + Guid.NewGuid().ToString("N");

    private static NamedPipeClientStream CreateClient(string pipeName) =>
        new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    private static StreamReader CreateReader(Stream stream) => new(
        stream,
        Encoding.UTF8,
        detectEncodingFromByteOrderMarks: false,
        bufferSize: 1024,
        leaveOpen: true);

    private static StreamWriter CreateWriter(Stream stream) => new(
        stream,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        bufferSize: 1024,
        leaveOpen: true)
    {
        AutoFlush = true,
    };
}
