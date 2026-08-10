namespace Eslee.TrayFolder.Services;

/// <summary>
/// 앱 하나를 자동 탐색하는 데 필요한 정보입니다.
/// </summary>
/// <param name="AppId">파이프 등록과 설정에서 쓰는 앱 id.</param>
/// <param name="DisplayName">사용자에게 보여줄 이름.</param>
/// <param name="ExecutableFileNames">허용하는 실행 파일 이름 후보(대소문자 무시).</param>
/// <param name="RegistryNameHints">언인스톨 레지스트리 DisplayName에서 찾을 문자열(공백 제거 후 부분 일치).</param>
/// <param name="CommonDirectoryNames">일반 설치 루트 아래에서 확인할 폴더 이름 후보.</param>
public sealed record AppDiscoverySpec(
    string AppId,
    string DisplayName,
    IReadOnlyList<string> ExecutableFileNames,
    IReadOnlyList<string> RegistryNameHints,
    IReadOnlyList<string> CommonDirectoryNames);

/// <summary>
/// Tray Folder가 지원하는 eslee 앱들의 자동 탐색 카탈로그입니다.
/// appId는 TrayFolderConfig.CreateDefault의 항목과 일치해야 합니다.
/// </summary>
public static class TrayAppCatalog
{
    public static IReadOnlyList<AppDiscoverySpec> Specs { get; } =
    [
        new AppDiscoverySpec(
            "eslee.autopower",
            "AutoPower",
            ["AutoPower.App.exe"],
            ["autopower"],
            ["eslee Auto Power", "AutoPower"]),
        new AppDiscoverySpec(
            "eslee.folderlocker",
            "Folder Locker",
            // 정식 설치본은 한글 브랜딩 실행 파일(eslee폴더잠금기.exe)로도 실행됩니다
            // (자동 시작 등록이 이 이름을 사용). 두 이름 모두 같은 앱입니다.
            ["FolderGate.App.exe", "eslee폴더잠금기.exe"],
            ["폴더잠금기", "folderlocker", "foldergate"],
            ["eslee-folder-locker", "eslee Folder Locker", "eslee폴더잠금기", "FolderGate"]),
        new AppDiscoverySpec(
            "eslee.downloadrouter",
            "Download Router",
            ["DownloadRouter.App.exe"],
            ["downloadrouter"],
            ["eslee Download Router", "eslee-download-router", "DownloadRouter"]),
        new AppDiscoverySpec(
            "eslee.onekey",
            "OneKey",
            ["Eslee.OneKey.App.exe"],
            ["onekey"],
            ["eslee OneKey", "eslee-onekey", "OneKey"]),
        new AppDiscoverySpec(
            "eslee.quicksend",
            "QuickSend",
            ["eslee QuickSend.exe", "QuickSend.Windows.exe"],
            ["quicksend"],
            ["eslee QuickSend", "eslee-quick-send", "QuickSend"]),
    ];

    public static AppDiscoverySpec? FindSpec(string appId) => Specs.FirstOrDefault(
        spec => string.Equals(spec.AppId, appId, StringComparison.OrdinalIgnoreCase));
}
