using System.Diagnostics;

namespace Eslee.TrayFolder.Services;

public sealed record ExecutableIdentity(
    string FileName,
    string? ProductName,
    string? CompanyName,
    string? FileVersion,
    string? ProductVersion);

public sealed record ExecutableValidationResult(
    bool IsValid,
    string? NormalizedPath,
    string? UserMessage,
    ExecutableIdentity? Identity = null)
{
    public static ExecutableValidationResult Invalid(string message) => new(false, null, message);
}

public static class AutoPowerIdentityPolicy
{
    public const string ExpectedFileName = "AutoPower.App.exe";
    public const string ExpectedProcessName = "AutoPower.App";

    public static bool IsRecognized(ExecutableIdentity identity)
    {
        var productName = identity.ProductName?.Replace(" ", string.Empty, StringComparison.Ordinal);
        return string.Equals(identity.FileName, ExpectedFileName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(productName, "esleeAutoPower", StringComparison.OrdinalIgnoreCase)
            && identity.CompanyName?.Contains("eslee", StringComparison.OrdinalIgnoreCase) == true;
    }
}

public sealed class ExecutablePathValidator
{
    /// <summary>
    /// AutoPower 외 앱을 위한 일반 검증입니다. 존재하는 EXE인지만 확인하며,
    /// 제품 정보 검사는 하지 않습니다.
    /// </summary>
    public ExecutableValidationResult ValidateGeneric(string? path, string displayName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ExecutableValidationResult.Invalid($"{displayName} 실행 파일 경로를 지정해 주세요.");
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ExecutableValidationResult.Invalid("실행 파일 경로 형식이 올바르지 않습니다.");
        }

        if (!File.Exists(normalizedPath))
        {
            return ExecutableValidationResult.Invalid($"지정한 {displayName} 실행 파일이 없습니다: {normalizedPath}");
        }

        if (!string.Equals(Path.GetExtension(normalizedPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return ExecutableValidationResult.Invalid("실행 파일(EXE)을 선택해 주세요.");
        }

        return new ExecutableValidationResult(true, normalizedPath, null);
    }

    public ExecutableValidationResult ValidateAutoPower(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ExecutableValidationResult.Invalid("AutoPower 실행 파일 경로를 지정해 주세요.");
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ExecutableValidationResult.Invalid("실행 파일 경로 형식이 올바르지 않습니다.");
        }

        if (!File.Exists(normalizedPath))
        {
            return ExecutableValidationResult.Invalid($"지정한 AutoPower 실행 파일이 없습니다: {normalizedPath}");
        }

        if (!string.Equals(Path.GetExtension(normalizedPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return ExecutableValidationResult.Invalid("실행 파일(EXE)을 선택해 주세요.");
        }

        try
        {
            var version = FileVersionInfo.GetVersionInfo(normalizedPath);
            var identity = new ExecutableIdentity(
                Path.GetFileName(normalizedPath),
                version.ProductName,
                version.CompanyName,
                version.FileVersion,
                version.ProductVersion);
            if (!AutoPowerIdentityPolicy.IsRecognized(identity))
            {
                return ExecutableValidationResult.Invalid(
                    "선택한 파일의 이름과 제품 정보가 eslee Auto Power와 일치하지 않습니다.");
            }

            return new ExecutableValidationResult(true, normalizedPath, null, identity);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ExecutableValidationResult.Invalid($"실행 파일 정보를 읽을 수 없습니다: {exception.Message}");
        }
    }
}

public sealed record ProcessDescriptor(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    ExecutableIdentity? Identity);

public static class AutoPowerProcessMatcher
{
    public static bool IsMatch(ProcessDescriptor process, string? configuredPath)
    {
        if (!string.Equals(
                process.ProcessName,
                AutoPowerIdentityPolicy.ExpectedProcessName,
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(process.ExecutablePath)
            || process.Identity is null
            || !AutoPowerIdentityPolicy.IsRecognized(process.Identity))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return true;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(process.ExecutablePath),
                Path.GetFullPath(configuredPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
