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
                Assert.IsTrue(server.IsClientConnected("eslee.autopower"));

                Assert.IsTrue(await server.SendTrayModeAsync("eslee.autopower", TrayMode.Hosted, CancellationToken.None));
                var modeLine = await reader.ReadLineAsync().WaitAsync(TestTimeout);
                Assert.IsNotNull(modeLine);
                StringAssert.Contains(modeLine, "\"set-tray-mode\"");
                StringAssert.Contains(modeLine, "\"hosted\"");

                var commandTask = server.SendCommandAsync(
                    "eslee.autopower", TrayHostCommand.Activate, TestTimeout, CancellationToken.None);
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
                var menuTask = server.GetMenuAsync("eslee.autopower", TestTimeout, CancellationToken.None);
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
                var actionTask = server.SendMenuActionAsync(
                    "eslee.autopower", "quick-shutdown-1h", TestTimeout, CancellationToken.None);
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
                var menu = await server.GetMenuAsync(
                    "eslee.autopower", TimeSpan.FromMilliseconds(300), CancellationToken.None);
                Assert.IsNull(menu);
            }
        }
    }

    [TestMethod]
    public async Task GetMenuWithoutClientReturnsNull()
    {
        using var server = new TrayHostServer(CreatePipeName());
        server.Start();

        Assert.IsNull(await server.GetMenuAsync(
            "eslee.autopower", TimeSpan.FromSeconds(1), CancellationToken.None));
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
                Assert.IsFalse(server.IsClientConnected("eslee.autopower"));
            }
        }
    }

    [TestMethod]
    public async Task SendCommandWithoutClientReturnsFailure()
    {
        using var server = new TrayHostServer(CreatePipeName());
        server.Start();

        var result = await server.SendCommandAsync(
            "eslee.autopower", TrayHostCommand.Activate, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(server.IsClientConnected("eslee.autopower"));
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
        Assert.IsFalse(server.IsClientConnected("eslee.autopower"));

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

    [TestMethod]
    public async Task RoutesCommandsToTheRegisteredAppAmongMultipleClients()
    {
        var pipeName = CreatePipeName();
        using var server = new TrayHostServer(pipeName);
        var registeredSignal = new SemaphoreSlim(0);
        server.ClientRegistered += (_, _) => registeredSignal.Release();
        server.Start();

        var clientA = CreateClient(pipeName);
        var clientB = CreateClient(pipeName);
        await using (clientA.ConfigureAwait(false))
        await using (clientB.ConfigureAwait(false))
        {
            await clientA.ConnectAsync(5000);
            using var readerA = CreateReader(clientA);
            var writerA = CreateWriter(clientA);
            await using (writerA.ConfigureAwait(false))
            {
                await writerA.WriteLineAsync(
                    """{"type":"register","protocolVersion":1,"appId":"app.a","processId":21}""");
                Assert.IsTrue(await registeredSignal.WaitAsync(TestTimeout));

                await clientB.ConnectAsync(5000);
                using var readerB = CreateReader(clientB);
                var writerB = CreateWriter(clientB);
                await using (writerB.ConfigureAwait(false))
                {
                    await writerB.WriteLineAsync(
                        """{"type":"register","protocolVersion":1,"appId":"app.b","processId":22}""");
                    Assert.IsTrue(await registeredSignal.WaitAsync(TestTimeout));
                    Assert.IsTrue(server.IsClientConnected("app.a"));
                    Assert.IsTrue(server.IsClientConnected("app.b"));

                    // app.b로 보낸 명령은 app.b 연결로만 전달됩니다.
                    var commandTask = server.SendCommandAsync(
                        "app.b", TrayHostCommand.Activate, TestTimeout, CancellationToken.None);
                    var commandLine = await readerB.ReadLineAsync().WaitAsync(TestTimeout);
                    Assert.IsNotNull(commandLine);
                    var command = TrayPipeProtocol.TryDeserialize(commandLine);
                    Assert.IsNotNull(command);
                    await writerB.WriteLineAsync(TrayPipeProtocol.Serialize(new TrayPipeMessage
                    {
                        Type = TrayPipeProtocol.CommandResultType,
                        Id = command.Id,
                        Succeeded = true,
                    }));
                    var result = await commandTask.WaitAsync(TestTimeout);
                    Assert.IsTrue(result.Succeeded);

                    // app.a에는 아무 메시지도 도착하지 않았어야 합니다.
                    var modeToA = server.SendTrayModeAsync("app.a", TrayMode.Hosted, CancellationToken.None);
                    var lineToA = await readerA.ReadLineAsync().WaitAsync(TestTimeout);
                    Assert.IsTrue(await modeToA.WaitAsync(TestTimeout));
                    Assert.IsNotNull(lineToA);
                    StringAssert.Contains(lineToA, "set-tray-mode");
                }
            }
        }
    }

    [TestMethod]
    public async Task FiveClientsRacingAtStartupAllRegister()
    {
        // 실제 시나리오 재현: 앱 5개가 먼저 실행되어 재시도 중이고, 그 뒤 호스트가 시작됩니다.
        var pipeName = CreatePipeName();
        var appIds = new[] { "app.a", "app.b", "app.c", "app.d", "app.e" };
        using var server = new TrayHostServer(pipeName);
        var registeredSignal = new SemaphoreSlim(0);
        server.ClientRegistered += (_, _) => registeredSignal.Release();

        var clients = new List<NamedPipeClientStream>();
        var writers = new List<StreamWriter>();
        try
        {
            var connectTasks = appIds.Select(async appId =>
            {
                var deadline = DateTime.UtcNow + TestTimeout;
                while (true)
                {
                    var client = CreateClient(pipeName);
                    try
                    {
                        await client.ConnectAsync(500);
                        lock (clients)
                        {
                            clients.Add(client);
                        }

                        var writer = CreateWriter(client);
                        lock (writers)
                        {
                            writers.Add(writer);
                        }

                        await writer.WriteLineAsync(
                            $$"""{"type":"register","protocolVersion":1,"appId":"{{appId}}","processId":77}""");
                        return;
                    }
                    catch (Exception exception) when (
                        exception is TimeoutException or IOException &&
                        DateTime.UtcNow < deadline)
                    {
                        await client.DisposeAsync();
                        await Task.Delay(100);
                    }
                }
            }).ToList();

            // 클라이언트들이 폴링을 시작한 뒤에 서버를 시작합니다.
            await Task.Delay(200);
            server.Start();
            await Task.WhenAll(connectTasks).WaitAsync(TestTimeout);

            for (var i = 0; i < appIds.Length; i++)
            {
                Assert.IsTrue(await registeredSignal.WaitAsync(TestTimeout), $"등록 이벤트가 부족합니다 (index {i}).");
            }

            foreach (var appId in appIds)
            {
                Assert.IsTrue(server.IsClientConnected(appId), $"{appId}가 연결 상태가 아닙니다.");
            }
        }
        finally
        {
            foreach (var writer in writers)
            {
                await writer.DisposeAsync();
            }

            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
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
