using System.Diagnostics;
using Eslee.TrayFolder.Native;

namespace Eslee.TrayFolder.Services;

public sealed record AutoPowerStatus(bool IsRunning, string? Detail = null);

public sealed record AutoPowerOperationResult(bool Succeeded, string? UserMessage = null, Exception? Exception = null);

public sealed class AutoPowerProcessService
{
    private readonly ExecutablePathValidator _validator;
    private readonly WindowRestorer _windowRestorer;

    public AutoPowerProcessService(ExecutablePathValidator validator, WindowRestorer windowRestorer)
    {
        _validator = validator;
        _windowRestorer = windowRestorer;
    }

    public Task<AutoPowerStatus> GetStatusAsync(string? configuredPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var process = FindMatchingProcess(configuredPath);
            return process is null
                ? new AutoPowerStatus(false)
                : new AutoPowerStatus(true, $"PID {process.Id}");
        }, cancellationToken);

    /// <summary>
    /// AutoPower 외 앱을 위한 경로 기반 상태 확인입니다. 설정된 실행 파일 경로와
    /// 같은 경로의 프로세스가 있는지만 검사합니다.
    /// </summary>
    public Task<AutoPowerStatus> GetStatusByPathAsync(string? configuredPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return new AutoPowerStatus(false);
            }

            using var process = FindProcessByPath(configuredPath);
            return process is null
                ? new AutoPowerStatus(false)
                : new AutoPowerStatus(true, $"PID {process.Id}");
        }, cancellationToken);

    /// <summary>
    /// 실행 파일 이름 후보만으로 실행 여부를 확인합니다. 설치본과 개발 빌드처럼
    /// 설정 경로와 다른 위치에서 실행 중인 인스턴스도 '실행 중'으로 잡습니다.
    /// </summary>
    public Task<AutoPowerStatus> GetStatusByExecutableNamesAsync(
        IReadOnlyList<string> executableFileNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executableFileNames);
        return Task.Run(() =>
        {
            foreach (var fileName in executableFileNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var processName = Path.GetFileNameWithoutExtension(fileName);
                if (string.IsNullOrWhiteSpace(processName))
                {
                    continue;
                }

                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(processName);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                var found = processes.Length > 0;
                foreach (var process in processes)
                {
                    process.Dispose();
                }

                if (found)
                {
                    return new AutoPowerStatus(true, $"process {processName}");
                }
            }

            return new AutoPowerStatus(false);
        }, cancellationToken);
    }

    /// <summary>
    /// AutoPower 외 앱을 위한 창 복원 또는 실행입니다. 설정 경로와 다른 위치에서
    /// 같은 이름의 프로세스(설치본/다른 빌드)가 실행 중이면 중복 실행을 막기 위해
    /// 새로 실행하지 않고 그 프로세스의 창 복원만 시도합니다.
    /// </summary>
    public async Task<AutoPowerOperationResult> ActivateOrLaunchByNamesAsync(
        IReadOnlyList<string> executableFileNames,
        string? configuredPath,
        string displayName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executableFileNames);

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            using var exactProcess = await Task.Run(
                () => FindProcessByPath(configuredPath),
                cancellationToken).ConfigureAwait(false);
            if (exactProcess is not null)
            {
                var restored = await _windowRestorer
                    .TryRestoreAsync(exactProcess, cancellationToken)
                    .ConfigureAwait(false);
                return restored
                    ? new AutoPowerOperationResult(true)
                    : new AutoPowerOperationResult(
                        false,
                        $"{displayName}은(는) 실행 중이지만 복원할 메인 창을 찾지 못했습니다.");
            }
        }

        using var anyInstance = await Task.Run(
            () => FindProcessByAnyName(executableFileNames),
            cancellationToken).ConfigureAwait(false);
        if (anyInstance is not null)
        {
            var restoredAny = await _windowRestorer
                .TryRestoreAsync(anyInstance, cancellationToken)
                .ConfigureAwait(false);
            return restoredAny
                ? new AutoPowerOperationResult(true)
                : new AutoPowerOperationResult(
                    false,
                    $"{displayName}이(가) 다른 위치에서 이미 실행 중입니다. 중복 실행을 막기 위해 새로 실행하지 않았습니다. " +
                    "실행 중인 버전이 연동을 지원하지 않으면 종료 후 최신 빌드로 다시 실행해 주세요.");
        }

        return await ActivateOrLaunchByPathAsync(configuredPath, displayName, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>AutoPower 외 앱을 위한 경로 기반 창 복원 또는 실행입니다.</summary>
    public async Task<AutoPowerOperationResult> ActivateOrLaunchByPathAsync(
        string? configuredPath,
        string displayName,
        CancellationToken cancellationToken)
    {
        var validation = _validator.ValidateGeneric(configuredPath, displayName);
        if (!validation.IsValid || validation.NormalizedPath is null)
        {
            return new AutoPowerOperationResult(false, validation.UserMessage);
        }

        try
        {
            using var existingProcess = await Task.Run(
                () => FindProcessByPath(validation.NormalizedPath),
                cancellationToken).ConfigureAwait(false);
            if (existingProcess is not null)
            {
                var restored = await _windowRestorer
                    .TryRestoreAsync(existingProcess, cancellationToken)
                    .ConfigureAwait(false);
                return restored
                    ? new AutoPowerOperationResult(true)
                    : new AutoPowerOperationResult(
                        false,
                        $"{displayName}은(는) 실행 중이지만 복원할 메인 창을 찾지 못했습니다.");
            }

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var startInfo = new ProcessStartInfo
                {
                    FileName = validation.NormalizedPath,
                    WorkingDirectory = Path.GetDirectoryName(validation.NormalizedPath)!,
                    UseShellExecute = true,
                };
                using var started = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Windows가 새 프로세스를 만들지 못했습니다.");
            }, cancellationToken).ConfigureAwait(false);
            return new AutoPowerOperationResult(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new AutoPowerOperationResult(
                false,
                $"{displayName}을(를) 실행하거나 창을 복원하지 못했습니다. 자세한 내용은 로그를 확인해 주세요.",
                exception);
        }
    }

    public async Task<AutoPowerOperationResult> ActivateOrLaunchAsync(
        string? configuredPath,
        CancellationToken cancellationToken)
    {
        var validation = _validator.ValidateAutoPower(configuredPath);
        if (!validation.IsValid || validation.NormalizedPath is null)
        {
            return new AutoPowerOperationResult(false, validation.UserMessage);
        }

        try
        {
            using var existingProcess = await Task.Run(
                () => FindMatchingProcess(validation.NormalizedPath),
                cancellationToken).ConfigureAwait(false);
            if (existingProcess is not null)
            {
                var restored = await _windowRestorer
                    .TryRestoreAsync(existingProcess, cancellationToken)
                    .ConfigureAwait(false);
                return restored
                    ? new AutoPowerOperationResult(true)
                    : new AutoPowerOperationResult(
                        false,
                        "AutoPower는 실행 중이지만 복원할 메인 창을 찾지 못했습니다. AutoPower 트레이 아이콘에서 직접 열어 주세요.");
            }

            // 설정 경로와 다른 위치(설치본/다른 빌드)의 AutoPower가 이미 실행 중이면
            // 중복 실행을 막기 위해 절대 새로 실행하지 않습니다.
            using var otherInstance = await Task.Run(
                () => FindMatchingProcess(null),
                cancellationToken).ConfigureAwait(false);
            if (otherInstance is not null)
            {
                var restoredOther = await _windowRestorer
                    .TryRestoreAsync(otherInstance, cancellationToken)
                    .ConfigureAwait(false);
                return restoredOther
                    ? new AutoPowerOperationResult(true)
                    : new AutoPowerOperationResult(
                        false,
                        "AutoPower가 다른 위치에서 이미 실행 중입니다. 중복 실행을 막기 위해 새로 실행하지 않았습니다. " +
                        "실행 중인 버전이 연동을 지원하지 않으면 종료 후 최신 빌드로 다시 실행해 주세요.");
            }

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var startInfo = new ProcessStartInfo
                {
                    FileName = validation.NormalizedPath,
                    WorkingDirectory = Path.GetDirectoryName(validation.NormalizedPath)!,
                    UseShellExecute = true,
                };
                using var started = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Windows가 새 프로세스를 만들지 못했습니다.");
            }, cancellationToken).ConfigureAwait(false);
            return new AutoPowerOperationResult(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new AutoPowerOperationResult(
                false,
                "AutoPower를 실행하거나 창을 복원하지 못했습니다. 자세한 내용은 로그를 확인해 주세요.",
                exception);
        }
    }

    /// <summary>프로세스 id로 실제 실행 파일 경로를 조회합니다. 접근 불가/종료 시 null입니다.</summary>
    public static string? TryGetProcessPath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return null;
        }
    }

    private static Process? FindProcessByAnyName(IReadOnlyList<string> executableFileNames)
    {
        foreach (var fileName in executableFileNames)
        {
            var processName = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrWhiteSpace(processName))
            {
                continue;
            }

            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            Process? match = null;
            foreach (var process in processes)
            {
                if (match is null)
                {
                    match = process;
                }
                else
                {
                    process.Dispose();
                }
            }

            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static Process? FindProcessByPath(string configuredPath)
    {
        string fullPath;
        string processName;
        try
        {
            fullPath = Path.GetFullPath(configuredPath);
            processName = Path.GetFileNameWithoutExtension(fullPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        Process? match = null;
        foreach (var process in processes)
        {
            if (match is not null)
            {
                process.Dispose();
                continue;
            }

            var descriptor = TryDescribe(process);
            if (descriptor?.ExecutablePath is string path &&
                string.Equals(Path.GetFullPath(path), fullPath, StringComparison.OrdinalIgnoreCase))
            {
                match = process;
            }
            else
            {
                process.Dispose();
            }
        }

        return match;
    }

    private static Process? FindMatchingProcess(string? configuredPath)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(AutoPowerIdentityPolicy.ExpectedProcessName);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        Process? match = null;
        foreach (var process in processes)
        {
            if (match is not null)
            {
                process.Dispose();
                continue;
            }

            var descriptor = TryDescribe(process);
            if (descriptor is not null && AutoPowerProcessMatcher.IsMatch(descriptor, configuredPath))
            {
                match = process;
            }
            else
            {
                process.Dispose();
            }
        }

        return match;
    }

    private static ProcessDescriptor? TryDescribe(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var version = FileVersionInfo.GetVersionInfo(path);
            var identity = new ExecutableIdentity(
                Path.GetFileName(path),
                version.ProductName,
                version.CompanyName,
                version.FileVersion,
                version.ProductVersion);
            return new ProcessDescriptor(process.Id, process.ProcessName, path, identity);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException
            or IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
