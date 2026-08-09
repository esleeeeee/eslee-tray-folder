using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace Eslee.TrayIntegration;

/// <summary>
/// Tray Folder가 실행하는 프로토콜 v1 Named Pipe 호스트입니다. 한 번에 하나의
/// 클라이언트만 처리하며, 연결이 끊어지면 다시 수락 대기로 돌아갑니다.
/// 이벤트는 백그라운드 스레드에서 발생하므로 구독자가 UI 스레드로 넘겨야 합니다.
/// </summary>
public sealed class TrayHostServer : IDisposable
{
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FaultRetryDelay = TimeSpan.FromSeconds(1);

    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<TrayCommandResult>> _pendingCommands = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<IReadOnlyList<TrayMenuItem>>> _pendingMenuRequests = new();
    private Task? _acceptLoop;
    private StreamWriter? _writer;
    private int _nextCommandId;
    private bool _disposed;

    public TrayHostServer(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
    }

    public event EventHandler<TrayAppRegistration>? ClientRegistered;

    public event EventHandler? ClientDisconnected;

    public event EventHandler<Exception>? Faulted;

    public bool IsClientConnected => Volatile.Read(ref _writer) is not null;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _acceptLoop ??= Task.Run(() => AcceptLoopAsync(_lifetime.Token));
    }

    public Task<bool> SendTrayModeAsync(TrayMode mode, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var message = new TrayPipeMessage
        {
            Type = TrayPipeProtocol.SetTrayModeType,
            Mode = TrayPipeProtocol.FormatTrayMode(mode),
        };
        return SendLineAsync(TrayPipeProtocol.Serialize(message), cancellationToken);
    }

    public async Task<TrayCommandResult> SendCommandAsync(
        TrayHostCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = Interlocked.Increment(ref _nextCommandId);
        var pending = new TaskCompletionSource<TrayCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[id] = pending;
        try
        {
            var message = new TrayPipeMessage
            {
                Type = TrayPipeProtocol.CommandType,
                Id = id,
                Command = TrayPipeProtocol.FormatCommand(command),
            };
            var sent = await SendLineAsync(TrayPipeProtocol.Serialize(message), cancellationToken)
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

    /// <summary>
    /// 연결된 앱에 현재 트레이 메뉴를 요청합니다. 연결이 없거나 앱이 제한 시간 안에
    /// 응답하지 않으면(메뉴 미지원 구버전 포함) null을 돌려줍니다.
    /// </summary>
    public async Task<IReadOnlyList<TrayMenuItem>?> GetMenuAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = Interlocked.Increment(ref _nextCommandId);
        var pending = new TaskCompletionSource<IReadOnlyList<TrayMenuItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingMenuRequests[id] = pending;
        try
        {
            var message = new TrayPipeMessage
            {
                Type = TrayPipeProtocol.GetMenuType,
                Id = id,
            };
            var sent = await SendLineAsync(TrayPipeProtocol.Serialize(message), cancellationToken)
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

    /// <summary>메뉴 항목 클릭을 앱에 전달합니다. 항목의 action id를 그대로 보냅니다.</summary>
    public async Task<TrayCommandResult> SendMenuActionAsync(
        string actionId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        var id = Interlocked.Increment(ref _nextCommandId);
        var pending = new TaskCompletionSource<TrayCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[id] = pending;
        try
        {
            var message = new TrayPipeMessage
            {
                Type = TrayPipeProtocol.CommandType,
                Id = id,
                Command = TrayPipeProtocol.MenuActionCommandValue,
                ActionId = actionId,
            };
            var sent = await SendLineAsync(TrayPipeProtocol.Serialize(message), cancellationToken)
                .ConfigureAwait(false);
            if (!sent)
            {
                return new TrayCommandResult(false, "연결된 앱이 없어 메뉴 명령을 보내지 못했습니다.");
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
        }
        catch (AggregateException)
        {
        }

        _lifetime.Dispose();
        _writeLock.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 버퍼 크기를 지정하지 않으면 0 할당량 파이프가 되어, 상대편이 읽기를
                // 걸어두기 전까지 쓰기가 완료되지 않아 교착이 생길 수 있습니다.
                var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 4096,
                    outBufferSize: 4096);
                await using (server.ConfigureAwait(false))
                {
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await ServeClientAsync(server, cancellationToken).ConfigureAwait(false);
                }
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

            Volatile.Write(ref _writer, writer);
            ClientRegistered?.Invoke(this, registration);
            try
            {
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
                        pending.TrySetResult(new TrayCommandResult(message.Succeeded ?? false, message.ErrorMessage));
                    }
                    else if (string.Equals(message.Type, TrayPipeProtocol.MenuType, StringComparison.Ordinal) &&
                        message.Id is int menuId &&
                        _pendingMenuRequests.TryRemove(menuId, out var pendingMenu))
                    {
                        pendingMenu.TrySetResult(TrayPipeProtocol.ToMenuItems(message));
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
                Volatile.Write(ref _writer, null);
                FailAllPending("앱과의 연결이 끊어졌습니다.");
                ClientDisconnected?.Invoke(this, EventArgs.Empty);
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

    private async Task<bool> SendLineAsync(string line, CancellationToken cancellationToken)
    {
        var writer = Volatile.Read(ref _writer);
        if (writer is null)
        {
            return false;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void FailAllPending(string reason)
    {
        foreach (var entry in _pendingCommands)
        {
            if (_pendingCommands.TryRemove(entry.Key, out var pending))
            {
                pending.TrySetResult(new TrayCommandResult(false, reason));
            }
        }

        foreach (var entry in _pendingMenuRequests)
        {
            if (_pendingMenuRequests.TryRemove(entry.Key, out var pendingMenu))
            {
                pendingMenu.TrySetCanceled();
            }
        }
    }
}
