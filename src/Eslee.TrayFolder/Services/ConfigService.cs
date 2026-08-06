using System.Text;
using System.Text.Json;
using Eslee.TrayFolder.Models;

namespace Eslee.TrayFolder.Services;

public sealed record ConfigLoadResult(TrayFolderConfig Config, string? RecoveredBackupPath);

public sealed class ConfigService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly AppPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConfigService(AppPaths paths, TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ConfigLoadResult> LoadOrCreateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            if (!File.Exists(_paths.ConfigFile))
            {
                var defaultConfig = TrayFolderConfig.CreateDefault();
                await SaveCoreAsync(defaultConfig, cancellationToken).ConfigureAwait(false);
                return new ConfigLoadResult(defaultConfig, null);
            }

            try
            {
                var json = await File.ReadAllTextAsync(_paths.ConfigFile, cancellationToken).ConfigureAwait(false);
                var config = JsonSerializer.Deserialize<TrayFolderConfig>(json, JsonOptions)
                    ?? throw new JsonException("설정 내용이 비어 있습니다.");
                Normalize(config);
                return new ConfigLoadResult(config, null);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                var backupPath = CreateCorruptBackupPath();
                File.Move(_paths.ConfigFile, backupPath);
                var defaultConfig = TrayFolderConfig.CreateDefault();
                await SaveCoreAsync(defaultConfig, cancellationToken).ConfigureAwait(false);
                return new ConfigLoadResult(defaultConfig, backupPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(TrayFolderConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Normalize(config);
            await SaveCoreAsync(config, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task SaveCoreAsync(TrayFolderConfig config, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var temporaryPath = _paths.ConfigFile + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            json + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, _paths.ConfigFile, overwrite: true);
    }

    private string CreateCorruptBackupPath()
    {
        var timestamp = _timeProvider.GetLocalNow().ToString("yyyyMMdd-HHmmssfff");
        var candidate = Path.Combine(_paths.DataDirectory, $"config.corrupt-{timestamp}.json");
        var suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(_paths.DataDirectory, $"config.corrupt-{timestamp}-{suffix++}.json");
        }

        return candidate;
    }

    private static void Normalize(TrayFolderConfig config)
    {
        if (config.Version <= 0)
        {
            config.Version = 1;
        }

        config.Apps ??= [];
        foreach (var app in config.Apps)
        {
            app.AppId ??= string.Empty;
            app.DisplayName ??= string.Empty;
            app.ExecutablePath ??= string.Empty;
            app.TrayMode = string.IsNullOrWhiteSpace(app.TrayMode) ? "standalone" : app.TrayMode;
        }
    }
}
