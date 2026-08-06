using System.Diagnostics;
using Microsoft.Win32;

namespace Eslee.TrayFolder.Services;

public sealed record AutoPowerDiscoveryResult(string? ExecutablePath, string Source, string? Detail = null);

public sealed class AutoPowerDiscoveryService
{
    private readonly ExecutablePathValidator _validator;

    public AutoPowerDiscoveryService(ExecutablePathValidator validator)
    {
        _validator = validator;
    }

    public Task<AutoPowerDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Discover(cancellationToken), cancellationToken);

    private AutoPowerDiscoveryResult Discover(CancellationToken cancellationToken)
    {
        foreach (var path in EnumerateRunningProcessPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validation = _validator.ValidateAutoPower(path);
            if (validation.IsValid)
            {
                return new AutoPowerDiscoveryResult(validation.NormalizedPath, "running-process");
            }
        }

        foreach (var path in EnumerateUninstallRegistryPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validation = _validator.ValidateAutoPower(path);
            if (validation.IsValid)
            {
                return new AutoPowerDiscoveryResult(validation.NormalizedPath, "uninstall-registry");
            }
        }

        foreach (var path in EnumerateCommonPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validation = _validator.ValidateAutoPower(path);
            if (validation.IsValid)
            {
                return new AutoPowerDiscoveryResult(validation.NormalizedPath, "common-location");
            }
        }

        return new AutoPowerDiscoveryResult(
            null,
            "not-found",
            "AutoPower를 자동으로 찾지 못했습니다. 설정에서 AutoPower.App.exe를 지정해 주세요.");
    }

    private static IEnumerable<string> EnumerateRunningProcessPaths()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(AutoPowerIdentityPolicy.ExpectedProcessName);
        }
        catch (InvalidOperationException)
        {
            yield break;
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
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                }

                if (!string.IsNullOrWhiteSpace(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateUninstallRegistryPaths()
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
                    var displayName = entry?.GetValue("DisplayName") as string;
                    if (displayName?.Replace(" ", string.Empty, StringComparison.Ordinal)
                            .Contains("AutoPower", StringComparison.OrdinalIgnoreCase) != true)
                    {
                        continue;
                    }

                    var installLocation = entry?.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(installLocation))
                    {
                        yield return Path.Combine(installLocation.Trim('"'), AutoPowerIdentityPolicy.ExpectedFileName);
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

    private static IEnumerable<string> EnumerateCommonPaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            programFiles,
            programFilesX86,
            Path.Combine(localAppData, "Programs"),
            localAppData,
        };

        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(root, "eslee Auto Power", AutoPowerIdentityPolicy.ExpectedFileName);
            yield return Path.Combine(root, "AutoPower", AutoPowerIdentityPolicy.ExpectedFileName);
        }
    }
}
