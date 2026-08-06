using System.Text;

namespace Eslee.TrayFolder.Services;

public interface IAppLogger
{
    Task InfoAsync(string message);

    Task ErrorAsync(string message, Exception exception);
}

public sealed class FileAppLogger : IAppLogger, IDisposable
{
    private const long MaximumFileBytes = 5 * 1024 * 1024;
    private const int MaximumFiles = 20;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(14);

    private readonly AppPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public FileAppLogger(AppPaths paths, TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task InfoAsync(string message) => WriteAsync("INFO", message, null);

    public Task ErrorAsync(string message, Exception exception) => WriteAsync("ERROR", message, exception);

    public async Task ApplyRetentionAsync()
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            var now = _timeProvider.GetUtcNow();
            var files = new DirectoryInfo(_paths.LogsDirectory)
                .EnumerateFiles("tray-folder-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();

            for (var index = 0; index < files.Length; index++)
            {
                var age = now - new DateTimeOffset(files[index].LastWriteTimeUtc, TimeSpan.Zero);
                if (index >= MaximumFiles || age > MaximumAge)
                {
                    try
                    {
                        files[index].Delete();
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _disposed = true;
        _gate.Dispose();
    }

    private async Task WriteAsync(string level, string message, Exception? exception)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(_paths.LogsDirectory);
                var now = _timeProvider.GetLocalNow();
                var baseName = $"tray-folder-{now:yyyyMMdd}";
                var logPath = Path.Combine(_paths.LogsDirectory, baseName + ".log");
                if (File.Exists(logPath) && new FileInfo(logPath).Length >= MaximumFileBytes)
                {
                    logPath = FindRolledPath(baseName);
                }

                var builder = new StringBuilder()
                    .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                    .Append(" [").Append(level).Append("] ").AppendLine(message);
                if (exception is not null)
                {
                    builder.AppendLine(exception.ToString());
                }

                await File.AppendAllTextAsync(logPath, builder.ToString(), Encoding.UTF8).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception writeException) when (writeException is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Logging failure must never terminate the tray host.
        }
    }

    private string FindRolledPath(string baseName)
    {
        var suffix = 1;
        string path;
        do
        {
            path = Path.Combine(_paths.LogsDirectory, $"{baseName}-{suffix++:00}.log");
        }
        while (File.Exists(path) && new FileInfo(path).Length >= MaximumFileBytes);

        return path;
    }
}
