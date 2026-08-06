namespace Eslee.TrayFolder.Services;

public enum SingleInstanceRole
{
    Primary,
    Secondary,
}

public static class SingleInstanceDecision
{
    public static SingleInstanceRole FromMutexCreation(bool createdNew) =>
        createdNew ? SingleInstanceRole.Primary : SingleInstanceRole.Secondary;
}

public sealed class SingleInstanceManager : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly bool _ownsMutex;
    private RegisteredWaitHandle? _registeredWait;
    private bool _disposed;

    public SingleInstanceManager(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        var safeId = applicationId.Replace('\\', '.').Replace('/', '.');
        _activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            $"Local\\{safeId}.activate");
        _mutex = new Mutex(
            initiallyOwned: true,
            $"Local\\{safeId}.mutex",
            out _ownsMutex);
        Role = SingleInstanceDecision.FromMutexCreation(_ownsMutex);
    }

    public SingleInstanceRole Role { get; }

    public void Listen(Action activationRequested)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(activationRequested);
        if (Role != SingleInstanceRole.Primary)
        {
            throw new InvalidOperationException("주 인스턴스만 활성화 요청을 기다릴 수 있습니다.");
        }

        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (state, _) => ((Action)state!).Invoke(),
            activationRequested,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void SignalPrimary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _activationEvent.Set();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registeredWait?.Unregister(null);
        _registeredWait = null;
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _mutex.Dispose();
        _activationEvent.Dispose();
    }
}
