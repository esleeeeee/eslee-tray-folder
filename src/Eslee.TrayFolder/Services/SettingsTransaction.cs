using Eslee.TrayFolder.Models;
using Eslee.TrayIntegration;

namespace Eslee.TrayFolder.Services;

public static class SettingsTransaction
{
    public static async Task CommitAsync(TrayFolderConfig config,
        IReadOnlyList<(TrayAppConfig App, string Path, TrayMode Mode)> changes,
        Func<TrayFolderConfig, CancellationToken, Task> save, CancellationToken cancellationToken)
    {
        var candidate = System.Text.Json.JsonSerializer.Deserialize<TrayFolderConfig>(
            System.Text.Json.JsonSerializer.Serialize(config))!;
        foreach (var (app, path, mode) in changes)
        {
            var target = candidate.Apps.Single(item => string.Equals(item.AppId, app.AppId, StringComparison.OrdinalIgnoreCase));
            target.ExecutablePath = path;
            target.TrayMode = TrayPipeProtocol.FormatTrayMode(mode);
        }
        await save(candidate, cancellationToken).ConfigureAwait(true);
        foreach (var (app, path, mode) in changes)
        {
            app.ExecutablePath = path;
            app.TrayMode = TrayPipeProtocol.FormatTrayMode(mode);
        }
    }
}
