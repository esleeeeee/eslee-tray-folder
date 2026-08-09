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
