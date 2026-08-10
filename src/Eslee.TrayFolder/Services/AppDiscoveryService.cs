using System.Diagnostics;
using Microsoft.Win32;

namespace Eslee.TrayFolder.Services;

public sealed record AppDiscoveryResult(string? ExecutablePath, string Source, string? Detail = null);

/// <summary>
/// 카탈로그 스펙(<see cref="AppDiscoverySpec"/>) 기반으로 앱 실행 파일을 찾습니다.
/// 우선순위: 실행 중인 프로세스 경로 → 언인스톨 레지스트리 → 일반 설치 위치.
/// AutoPower는 기존과 같은 제품 정보 검증을, 다른 앱은 파일 이름 일치 검증을 사용합니다.
/// </summary>
public sealed class AppDiscoveryService
{
    private readonly ExecutablePathValidator _validator;

    public AppDiscoveryService(ExecutablePathValidator validator)
    {
        _validator = validator;
    }

    public Task<AppDiscoveryResult> DiscoverAsync(AppDiscoverySpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Task.Run(() => Discover(spec, cancellationToken), cancellationToken);
    }

    /// <summary>후보 경로가 이 앱의 실행 파일로 유효한지 검사합니다.</summary>
    public string? TryValidateCandidate(AppDiscoverySpec spec, string? candidatePath)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return null;
        }

        if (string.Equals(spec.AppId, "eslee.autopower", StringComparison.OrdinalIgnoreCase))
        {
            var strict = _validator.ValidateAutoPower(candidatePath);
            return strict.IsValid ? strict.NormalizedPath : null;
        }

        var generic = _validator.ValidateGeneric(candidatePath, spec.DisplayName);
        if (!generic.IsValid || generic.NormalizedPath is null)
        {
            return null;
        }

        return MatchesExpectedFileName(spec, generic.NormalizedPath) ? generic.NormalizedPath : null;
    }

    /// <summary>경로의 파일 이름이 스펙의 실행 파일 후보와 일치하는지 확인합니다.</summary>
    public static bool MatchesExpectedFileName(AppDiscoverySpec spec, string path)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var fileName = Path.GetFileName(path);
        return spec.ExecutableFileNames.Any(candidate =>
            string.Equals(candidate, fileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>루트 폴더 목록 아래에서 확인할 일반 설치 위치 후보를 나열합니다.</summary>
    public static IEnumerable<string> EnumerateCommonPaths(AppDiscoverySpec spec, IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(roots);
        foreach (var root in roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var directoryName in spec.CommonDirectoryNames)
            {
                foreach (var fileName in spec.ExecutableFileNames)
                {
                    yield return Path.Combine(root, directoryName, fileName);
                }
            }
        }
    }

    private AppDiscoveryResult Discover(AppDiscoverySpec spec, CancellationToken cancellationToken)
    {
        // 실행 중인 앱이 있으면 그 프로세스의 실제 exe 경로를 최우선으로 사용합니다.
        foreach (var path in EnumerateRunningProcessPaths(spec))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryValidateCandidate(spec, path) is string running)
            {
                return new AppDiscoveryResult(running, "running-process");
            }
        }

        foreach (var path in EnumerateUninstallRegistryPaths(spec))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryValidateCandidate(spec, path) is string installed)
            {
                return new AppDiscoveryResult(installed, "uninstall-registry");
            }
        }

        foreach (var path in EnumerateCommonPaths(spec, GetDefaultRoots()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryValidateCandidate(spec, path) is string common)
            {
                return new AppDiscoveryResult(common, "common-location");
            }
        }

        return new AppDiscoveryResult(
            null,
            "not-found",
            $"{spec.DisplayName}을(를) 자동으로 찾지 못했습니다. 설정에서 실행 파일({spec.ExecutableFileNames[0]})을 직접 지정해 주세요.");
    }

    private static IEnumerable<string> EnumerateRunningProcessPaths(AppDiscoverySpec spec)
    {
        foreach (var fileName in spec.ExecutableFileNames)
        {
            var processName = Path.GetFileNameWithoutExtension(fileName);
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    string? path = null;
                    try
                    {
                        path = process.MainModule?.FileName;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException
                        or System.ComponentModel.Win32Exception
                        or NotSupportedException)
                    {
                    }

                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        yield return path;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateUninstallRegistryPaths(AppDiscoverySpec spec)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(subKeyName);
                    if (!MatchesRegistryDisplayName(spec, entry?.GetValue("DisplayName") as string))
                    {
                        continue;
                    }

                    var installLocation = entry?.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(installLocation))
                    {
                        foreach (var fileName in spec.ExecutableFileNames)
                        {
                            yield return Path.Combine(installLocation.Trim('"'), fileName);
                        }
                    }

                    var displayIcon = entry?.GetValue("DisplayIcon") as string;
                    if (!string.IsNullOrWhiteSpace(displayIcon))
                    {
                        yield return displayIcon.Trim().Trim('"').Split(',')[0];
                    }
                }
            }
        }
    }

    private static bool MatchesRegistryDisplayName(AppDiscoverySpec spec, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        var strippedName = displayName.Replace(" ", string.Empty, StringComparison.Ordinal);
        return spec.RegistryNameHints.Any(hint => strippedName.Contains(
            hint.Replace(" ", string.Empty, StringComparison.Ordinal),
            StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> GetDefaultRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(localAppData, "Programs"),
            localAppData,
        ];
    }
}
