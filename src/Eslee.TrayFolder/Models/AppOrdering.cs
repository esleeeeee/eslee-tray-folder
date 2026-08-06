namespace Eslee.TrayFolder.Models;

public static class AppOrdering
{
    public static IReadOnlyList<TrayAppConfig> EnabledInDisplayOrder(IEnumerable<TrayAppConfig> apps) =>
        apps.Where(app => app.Enabled)
            .OrderBy(app => app.Order)
            .ThenBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.AppId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
