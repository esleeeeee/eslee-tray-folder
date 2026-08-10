using System.Net.Http;
using System.Text.Json;

namespace Eslee.TrayFolder.Services;

public enum UpdateStatus
{
    UpToDate,
    UpdateAvailable,
    Failed,
}

public sealed record UpdateCheckResult(
    UpdateStatus Status,
    string? LatestVersion = null,
    string? ReleaseUrl = null);

/// <summary>
/// GitHub의 최신 정식 릴리스와 현재 버전을 비교합니다. releases/latest API는
/// draft와 prerelease를 제외한 최신 릴리스만 돌려줍니다. 네트워크 실패는
/// Failed 결과로만 보고되며 앱 동작에 영향을 주지 않습니다.
/// </summary>
public sealed class UpdateCheckService
{
    public const string ReleasesPageUrl = "https://github.com/esleeeeee/eslee-tray-folder/releases";

    private static readonly Uri LatestReleaseUri =
        new("https://api.github.com/repos/esleeeeee/eslee-tray-folder/releases/latest");

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("eslee-tray-folder");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.GetAsync(LatestReleaseUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(UpdateStatus.Failed);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var tag = document.RootElement.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString()
                : null;
            var releaseUrl = document.RootElement.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString()
                : null;
            if (!TryParseVersionTag(tag, out var latest))
            {
                return new UpdateCheckResult(UpdateStatus.Failed);
            }

            return IsNewer(latest, currentVersion)
                ? new UpdateCheckResult(UpdateStatus.UpdateAvailable, FormatVersion(latest), releaseUrl ?? ReleasesPageUrl)
                : new UpdateCheckResult(UpdateStatus.UpToDate, FormatVersion(latest), releaseUrl ?? ReleasesPageUrl);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or JsonException
            or IOException)
        {
            return new UpdateCheckResult(UpdateStatus.Failed);
        }
    }

    /// <summary>"v1.2.3" 또는 "1.2.3" 형태의 태그를 버전으로 해석합니다.</summary>
    public static bool TryParseVersionTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        if (!Version.TryParse(text, out var parsed))
        {
            return false;
        }

        version = Normalize(parsed);
        return true;
    }

    /// <summary>
    /// 릴리스 버전이 현재 버전보다 높을 때만 true입니다. 개발 빌드처럼 현재 버전이
    /// 릴리스보다 높거나 같은 경우에는 업데이트를 권하지 않습니다.
    /// </summary>
    public static bool IsNewer(Version latest, Version current)
    {
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(current);
        return Normalize(latest) > Normalize(current);
    }

    public static string FormatVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var normalized = Normalize(version);
        return $"v{normalized.Major}.{normalized.Minor}.{normalized.Build}";
    }

    private static Version Normalize(Version version) => new(
        Math.Max(version.Major, 0),
        Math.Max(version.Minor, 0),
        Math.Max(version.Build, 0));
}
