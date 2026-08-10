using System.Text.Json;
using Eslee.TrayFolder.Models;
using Eslee.TrayFolder.Services;

namespace Eslee.TrayFolder.Tests;

[TestClass]
public sealed class ConfigServiceTests
{
    [TestMethod]
    public async Task LoadOrCreateAsync_CreatesDefaultConfigWhenMissing()
    {
        using var directory = new TestDirectory();
        using var service = new ConfigService(new AppPaths(directory.Path));

        var result = await service.LoadOrCreateAsync();

        Assert.IsTrue(File.Exists(System.IO.Path.Combine(directory.Path, "config.json")));
        Assert.HasCount(5, result.Config.Apps);
        Assert.AreEqual("eslee.autopower", result.Config.Apps[0].AppId);
        Assert.AreEqual("hosted", result.Config.Apps[0].TrayMode);
        Assert.AreEqual(
            "hosted",
            result.Config.Apps.Single(app => app.AppId == "eslee.folderlocker").TrayMode,
            "Folder Locker의 기본 트레이 모드는 Hosted여야 합니다.");
        CollectionAssert.AreEqual(
            new[]
            {
                "eslee.autopower",
                "eslee.folderlocker",
                "eslee.downloadrouter",
                "eslee.onekey",
                "eslee.quicksend",
            },
            result.Config.Apps.OrderBy(app => app.Order).Select(app => app.AppId).ToArray());
        Assert.IsNull(result.RecoveredBackupPath);
    }

    [TestMethod]
    public async Task LoadOrCreateAsync_LoadsValidConfig()
    {
        using var directory = new TestDirectory();
        using var service = new ConfigService(new AppPaths(directory.Path));
        var expected = TrayFolderConfig.CreateDefault();
        expected.Apps[0].ExecutablePath = @"C:\Apps\AutoPower.App.exe";
        await service.SaveAsync(expected);

        var result = await service.LoadOrCreateAsync();

        Assert.AreEqual(@"C:\Apps\AutoPower.App.exe", result.Config.Apps[0].ExecutablePath);
        Assert.IsNull(result.RecoveredBackupPath);
    }

    [TestMethod]
    public async Task LoadOrCreateAsync_BacksUpCorruptConfigAndRecoversDefault()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.Path);
        var configPath = System.IO.Path.Combine(directory.Path, "config.json");
        await File.WriteAllTextAsync(configPath, "{ definitely-not-json");
        using var service = new ConfigService(new AppPaths(directory.Path));

        var result = await service.LoadOrCreateAsync();

        Assert.IsNotNull(result.RecoveredBackupPath);
        Assert.IsTrue(File.Exists(result.RecoveredBackupPath));
        Assert.AreEqual("{ definitely-not-json", await File.ReadAllTextAsync(result.RecoveredBackupPath));
        Assert.IsTrue(File.Exists(configPath));
        var recoveredJson = await File.ReadAllTextAsync(configPath);
        var recovered = JsonSerializer.Deserialize<TrayFolderConfig>(recoveredJson);
        Assert.IsNotNull(recovered);
        Assert.HasCount(5, recovered.Apps);
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "eslee-tray-folder-tests",
                Guid.NewGuid().ToString("N"));
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
