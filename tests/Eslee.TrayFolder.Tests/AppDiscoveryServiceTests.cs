using Eslee.TrayFolder.Models;
using Eslee.TrayFolder.Native;
using Eslee.TrayFolder.Services;

namespace Eslee.TrayFolder.Tests;

[TestClass]
public sealed class AppDiscoveryServiceTests
{
    [TestMethod]
    public void CatalogCoversEveryDefaultConfigApp()
    {
        var defaultAppIds = TrayFolderConfig.CreateDefault().Apps.Select(app => app.AppId).ToList();

        foreach (var appId in defaultAppIds)
        {
            var spec = TrayAppCatalog.FindSpec(appId);
            Assert.IsNotNull(spec, $"카탈로그에 {appId} 스펙이 없습니다.");
            Assert.IsNotEmpty(spec.ExecutableFileNames);
            Assert.IsTrue(spec.ExecutableFileNames.All(
                name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
            Assert.IsNotEmpty(spec.RegistryNameHints);
            Assert.IsNotEmpty(spec.CommonDirectoryNames);
        }

        Assert.HasCount(defaultAppIds.Count, TrayAppCatalog.Specs);
        Assert.IsNull(TrayAppCatalog.FindSpec("eslee.unknown"));
    }

    [TestMethod]
    public void MatchesExpectedFileNameIsCaseInsensitiveAndRejectsOtherExecutables()
    {
        var spec = TrayAppCatalog.FindSpec("eslee.quicksend")!;

        Assert.IsTrue(AppDiscoveryService.MatchesExpectedFileName(spec, @"C:\Apps\eslee quicksend.EXE"));
        Assert.IsTrue(AppDiscoveryService.MatchesExpectedFileName(spec, @"C:\Apps\QuickSend.Windows.exe"));
        Assert.IsFalse(AppDiscoveryService.MatchesExpectedFileName(spec, @"C:\Apps\Other.exe"));
    }

    [TestMethod]
    public void EnumerateCommonPathsCombinesRootsDirectoriesAndFileNames()
    {
        var spec = TrayAppCatalog.FindSpec("eslee.onekey")!;

        var paths = AppDiscoveryService.EnumerateCommonPaths(
            spec, [@"C:\RootA", @"C:\RootA", string.Empty, @"C:\RootB"]).ToList();

        Assert.HasCount(2 * spec.CommonDirectoryNames.Count * spec.ExecutableFileNames.Count, paths);
        CollectionAssert.Contains(paths, @"C:\RootA\eslee OneKey\Eslee.OneKey.App.exe");
        CollectionAssert.Contains(paths, @"C:\RootB\eslee-onekey\Eslee.OneKey.App.exe");
    }

    [TestMethod]
    public void TryValidateCandidateAcceptsMatchingExistingExecutableOnly()
    {
        var service = new AppDiscoveryService(new ExecutablePathValidator());
        var spec = TrayAppCatalog.FindSpec("eslee.downloadrouter")!;
        var directory = Path.Combine(
            Path.GetTempPath(), "eslee-tray-folder-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var matching = Path.Combine(directory, "DownloadRouter.App.exe");
            File.WriteAllText(matching, "stub");
            var wrongName = Path.Combine(directory, "Other.exe");
            File.WriteAllText(wrongName, "stub");

            Assert.AreEqual(matching, service.TryValidateCandidate(spec, matching));
            Assert.IsNull(service.TryValidateCandidate(spec, wrongName));
            Assert.IsNull(service.TryValidateCandidate(spec, Path.Combine(directory, "missing.exe")));
            Assert.IsNull(service.TryValidateCandidate(spec, null));
            Assert.IsNull(service.TryValidateCandidate(spec, "   "));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task DiscoverFindsRunningProcessExecutable()
    {
        // 현재 테스트 호스트 프로세스를 '실행 중인 앱'으로 위장한 스펙을 만들어
        // 실행 중 프로세스 경로 우선 탐색을 검증합니다.
        var currentPath = Environment.ProcessPath;
        Assert.IsNotNull(currentPath);
        var service = new AppDiscoveryService(new ExecutablePathValidator());
        var spec = new AppDiscoverySpec(
            "eslee.test",
            "테스트 앱",
            [Path.GetFileName(currentPath)],
            ["neverfound"],
            ["never-found-directory"]);

        var result = await service.DiscoverAsync(spec, CancellationToken.None);

        // 같은 이름의 테스트 호스트가 여러 개일 수 있으므로 파일 이름과 존재만 확인합니다.
        Assert.AreEqual("running-process", result.Source);
        Assert.IsNotNull(result.ExecutablePath);
        Assert.AreEqual(
            Path.GetFileName(currentPath),
            Path.GetFileName(result.ExecutablePath),
            ignoreCase: true);
        Assert.IsTrue(File.Exists(result.ExecutablePath));
    }

    [TestMethod]
    public async Task ActivateOrLaunchByNamesDoesNotLaunchWhenAnotherInstanceRuns()
    {
        // 현재 테스트 호스트 프로세스를 '다른 위치에서 실행 중인 같은 앱'으로 취급합니다.
        // 설정 경로에 유효한 exe가 있어도 새로 실행하면 안 됩니다(중복 실행 방지).
        var currentPath = Environment.ProcessPath;
        Assert.IsNotNull(currentPath);
        var service = new AutoPowerProcessService(new ExecutablePathValidator(), new WindowRestorer());
        var directory = Path.Combine(
            Path.GetTempPath(), "eslee-tray-folder-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuredStub = Path.Combine(directory, Path.GetFileName(currentPath));
            File.WriteAllText(configuredStub, "stub - 실행되면 안 되는 파일");

            var result = await service.ActivateOrLaunchByNamesAsync(
                [Path.GetFileName(currentPath)],
                configuredStub,
                "테스트 앱",
                CancellationToken.None);

            // 테스트 호스트는 복원할 창이 없으므로 실패해야 하고,
            // 그 실패가 새 프로세스 실행으로 이어지면 안 됩니다.
            Assert.IsFalse(result.Succeeded);
            Assert.IsNotNull(result.UserMessage);
            StringAssert.Contains(result.UserMessage, "이미 실행 중");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void TryGetProcessPathReturnsPathForCurrentProcessAndNullForInvalidId()
    {
        var currentPath = Environment.ProcessPath;
        Assert.IsNotNull(currentPath);

        var resolved = AutoPowerProcessService.TryGetProcessPath(Environment.ProcessId);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(Path.GetFullPath(currentPath), Path.GetFullPath(resolved), ignoreCase: true);
        Assert.IsNull(AutoPowerProcessService.TryGetProcessPath(-1));
    }

    [TestMethod]
    public async Task DiscoverReportsNotFoundWithBrowseGuidance()
    {
        var service = new AppDiscoveryService(new ExecutablePathValidator());
        var spec = new AppDiscoverySpec(
            "eslee.test",
            "테스트 앱",
            ["definitely-not-running-" + Guid.NewGuid().ToString("N") + ".exe"],
            ["neverfound" + Guid.NewGuid().ToString("N")],
            ["never-found-directory"]);

        var result = await service.DiscoverAsync(spec, CancellationToken.None);

        Assert.IsNull(result.ExecutablePath);
        Assert.AreEqual("not-found", result.Source);
        Assert.IsNotNull(result.Detail);
        StringAssert.Contains(result.Detail, "직접 지정");
    }
}
