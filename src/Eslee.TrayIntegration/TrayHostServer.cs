using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace Eslee.TrayIntegration;

/// <summary>
/// Tray Folder가 실행하는 프로토콜 v1 Named Pipe 호스트입니다. 여러 앱이 같은 파이프
/// 이름으로 동시에 연결할 수 있으며, 각 연결은 register의 appId로 구분됩니다.
/// 같은 appId가 이미 연결돼 있으면 새 연결은 거부됩니다(기존 세션 우선).
/// 이벤트는 백그라운드 스레드에서 발생하므로 구독자가 UI 스레드로 넘겨야 합니다.
/// </summary>
public sealed class TrayHostServer : IDisposable
{
    private const int MaxClients = 8;

    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FaultRetryDelay = TimeSpan.FromSeconds(1);

    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, PendingCommand> _pendingCommands = new();
    private readonly ConcurrentDictionary<int, PendingMenuRequest> _pendingMenuRequests = new();
    private readonly ConcurrentDictionary<Task, byte> _sessionTasks = new();
    private Task? _acceptLoop;
    private int _nextCommandId;
    private bool _disposed;

    public TrayHostServer(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
    }

    public event EventHandler<TrayAppRegistration>? ClientRegistered;

    /// <summary>연결이 끊어진 앱의 appId를 전달합니다.</summary>
    public event EventHandler<string>? ClientDisconnected;

    public event EventHandler<Exception>? Faulted;

    public bool IsClientConnected(string appId) =>
        !string.IsNullOrWhiteSpace(appId) && _sessions.ContainsKey(appId);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _acceptLoop ??= Task.Run(() => AcceptLoopAsync(_lifetime.Token));
    }

    public Task<bool> SendTrayModeAsync(string appId, TrayMode mode, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var message = new TrayPipeMessage
        {
            Type = TrayPipeProtocol.SetTrayModeType,
            Mode = TrayPipeProtocol.FormatTrayMode(mode),
        };
        return SendLineAsync(appId, TrayPipeProtocol.Serialize(message), cancellationToken);
    }

    public Task<TrayCommandResult> SendCommandAsync(
        string appId,
        TrayHostCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return SendCommandCoreAsync(
            appId,
            id => new TrayPipeMessage
            {
                Type = TrayPipeProtocol.CommandType,
                Id = id,
                Command = TrayPipeProtocol.FormatCommand(command),
            },
            timeout,
            cancellationToken);
    }

    /// <summary>메뉴 항목 클릭을 앱에 전달합니다. 항목의 action id를 그대로 보냅니다.</summary>
    public Task<TrayCommandResult> SendMenuActionAsync(
        string appId,
        string actionId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        return SendCommandCoreAsync(
            appId,
            id => new TrayPipeMessage
            {
                Type = TrayPipeProtocol.CommandType,
                Id = id,
                Command = TrayPipeProtocol.MenuActionCommandValue,
                ActionId = actionId,
            },
            timeout,
            cancellationToken);
    }

    /// <summary>
    /// 연결된 앱에 현재 트레이 메뉴를 요청합니다. 연결이 없거나 앱이 제한 시간 안에
    /// 응답하지 않으면(메뉴 미지원 구버전 포함) null을 돌려줍니다.
    /// </summary>
    public async Task<IReadOnlyList<TrayMenuItem>?> GetMenuAsync(
        string appId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var id = Interlocked.Increment(ref _nextCommandId);
        var pending = new TaskCompletionSource<IReadOnlyList<TrayMenuItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingMenuRequests[id] = new PendingMenuRequest(appId, pending);
        try
        {
            var message = new TrayPipeMessage
            {
                Type = TrayPipeProtocol.GetMenuType,
                Id = id,
            };
            var sent = await SendLineAsync(appId, TrayPipeProtocol.Serialize(message), cancellationToken)
                .ConfigureAwait(false);
            if (!sent)
            {
                return null;
            }

            try
            {
                return await pending.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 응답을 기다리는 동안 연결이 끊어진 경우입니다.
                return null;
            }
        }
        finally
        {
            _pendingMenuRequests.TryRemove(id, out _);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        FailAllPending("호스트를 종료했습니다.");
        try
        {
            _acceptLoop?.Wait(TimeSpan.FromSeconds(3));
            Task.WaitAll(_sessionTasks.Keys.ToArray(), TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _lifetime.Dispose();
    }

    private async Task<TrayCommandResult> SendCommandCoreAsync(
        string appId,
        Func<int, TrayPipeMessage> messageFactory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextCommandId);
        var pending = new TaskCompletionSource<TrayCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[id] = new PendingCommand(appId, pending);
        try
        {
            var sent = await SendLineAsync(appId, TrayPipeProtocol.Serialize(messageFactory(id)), cancellationToken)
                .ConfigureAwait(false);
            if (!sent)
            {
                return new TrayCommandResult(false, "연결된 앱이 없어 명령을 보내지 못했습니다.");
            }

            try
            {
                return await pending.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new TrayCommandResult(false, "앱이 제한 시간 안에 응답하지 않았습니다.");
            }
        }
        finally
        {
            _pendingCommands.TryRemove(id, out _);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // 버퍼 크기를 지정하지 않으면 0 할당량 파이프가 되어, 상대편이 읽기를
                // 걸어두기 전까지 쓰기가 완료되지 않아 교착이 생길 수 있습니다.
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    MaxClients,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 4096,
                    outBufferSize: 4096);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                // 연결된 스트림의 소유권을 세션 태스크로 넘기고 즉시 다음 연결을 수락합니다.
                var connected = server;
                server = null;
                Task? sessionTask = null;
                sessionTask = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await using (connected.ConfigureAwait(false))
                            {
                                await ServeClientAsync(connected, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception exception) when (
                            exception is IOException or ObjectDisposedException or UnauthorizedAccessException)
                        {
                            Faulted?.Invoke(this, exception);
                        }
                        finally
                        {
                            if (sessionTask is not null)
                            {
                                _sessionTasks.TryRemove(sessionTask, out _);
                            }
                        }
                    },
                    CancellationToken.None);
                _sessionTasks[sessionTask] = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Faulted?.Invoke(this, exception);
                try
                {
                    await Task.Delay(FaultRetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            server,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        var writer = new StreamWriter(server, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
        {
            AutoFlush = true,
        };
        await using (writer.ConfigureAwait(false))
        {
            var registration = await ReadRegistrationAsync(reader, cancellationToken).ConfigureAwait(false);
            if (registration is null)
            {
                return;
            }

            var session = new ClientSession(registration.AppId, writer);
            if (!_sessions.TryAdd(registration.AppId, session))
            {
                // 같은 appId가 이미 연결돼 있으면 새 연결을 거부합니다.
                return;
            }

            try
            {
                ClientRegistered?.Invoke(this, registration);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    var message = TrayPipeProtocol.TryDeserialize(line);
                    if (message is null)
                    {
                        continue;
                    }

                    if (string.Equals(message.Type, TrayPipeProtocol.CommandResultType, StringComparison.Ordinal) &&
                        message.Id is int commandId &&
                        _pendingCommands.TryRemove(commandId, out var pending))
                    {
                        pending.Completion.TrySetResult(
                            new TrayCommandResult(message.Succeeded ?? false, message.ErrorMessage));
                    }
                    else if (string.Equals(message.Type, TrayPipeProtocol.MenuType, StringComparison.Ordinal) &&
                        message.Id is int menuId &&
                        _pendingMenuRequests.TryRemove(menuId, out var pendingMenu))
                    {
                        pendingMenu.Completion.TrySetResult(TrayPipeProtocol.ToMenuItems(message));
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            finally
            {
                _sessions.TryRemove(new KeyValuePair<string, ClientSession>(registration.AppId, session));
                FailPendingFor(registration.AppId, "앱과의 연결이 끊어졌습니다.");
                ClientDisconnected?.Invoke(this, registration.AppId);
                session.WriteLock.Dispose();
            }
        }
    }

    private async Task<TrayAppRegistration?> ReadRegistrationAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RegistrationTimeout);
        string? line;
        try
        {
            line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (line is null)
        {
            return null;
        }

        var message = TrayPipeProtocol.TryDeserialize(line);
        return message is null ? null : TrayPipeProtocol.TryCreateRegistration(message);
    }

    private async Task<bool> SendLineAsync(string appId, string line, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(appId, out var session))
        {
            return false;
        }

        try
        {
            await session.WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            await session.Writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            try
            {
                session.WriteLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void FailPendingFor(string appId, string reason)
    {
        foreach (var entry in _pendingCommands)
        {
            if (string.Equals(entry.Value.AppId, appId, StringComparison.OrdinalIgnoreCase) &&
                _pendingCommands.TryRemove(entry.Key, out var pending))
            {
                pending.Completion.TrySetResult(new TrayCommandResult(false, reason));
            }
        }

        foreach (var entry in _pendingMenuRequests)
        {
            if (string.Equals(entry.Value.AppId, appId, StringComparison.OrdinalIgnoreCase) &&
                _pendingMenuRequests.TryRemove(entry.Key, out var pendingMenu))
            {
                pendingMenu.Completion.TrySetCanceled();
            }
        }
    }

    private void FailAllPending(string reason)
    {
        foreach (var entry in _pendingCommands)
        {
            if (_pendingCommands.TryRemove(entry.Key, out var pending))
            {
                pending.Completion.TrySetResult(new TrayCommandResult(false, reason));
            }
        }

        foreach (var entry in _pendingMenuRequests)
        {
            if (_pendingMenuRequests.TryRemove(entry.Key, out var pendingMenu))
            {
                pendingMenu.Completion.TrySetCanceled();
            }
        }
    }

    private sealed class ClientSession
    {
        public ClientSession(string appId, StreamWriter writer)
        {
            AppId = appId;
            Writer = writer;
            WriteLock = new SemaphoreSlim(1, 1);
        }

        public string AppId { get; }

        public StreamWriter Writer { get; }

        public SemaphoreSlim WriteLock { get; }
    }

    private sealed record PendingCommand(string AppId, TaskCompletionSource<TrayCommandResult> Completion);

    private sealed record PendingMenuRequest(string AppId, TaskCompletionSource<IReadOnlyList<TrayMenuItem>> Completion);
}
