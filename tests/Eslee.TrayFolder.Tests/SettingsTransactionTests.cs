using Eslee.TrayFolder.Models;
using Eslee.TrayFolder.Services;
using Eslee.TrayIntegration;

namespace Eslee.TrayFolder.Tests;

[TestClass]
public sealed class SettingsTransactionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SaveCommitsLiveValuesOnlyAfterPersistence(bool fail)
    {
        var config = TrayFolderConfig.CreateDefault();
        var app = config.Apps[0];
        var calls = 0;
        var persisted = "old";
        async Task Save(TrayFolderConfig candidate, CancellationToken token)
        {
            calls++;
            Assert.AreEqual(string.Empty, app.ExecutablePath, "Live settings must remain unchanged during save.");
            Assert.AreEqual("hosted", app.TrayMode);
            Assert.AreEqual("new.exe", candidate.Apps[0].ExecutablePath);
            await Task.Yield();
            if (fail) throw new IOException("injected disk failure");
            persisted = candidate.Apps[0].ExecutablePath;
        }
        var task = SettingsTransaction.CommitAsync(config, [(app, "new.exe", TrayMode.Standalone)], Save, CancellationToken.None);
        if (fail) await Assert.ThrowsExactlyAsync<IOException>(() => task);
        else await task;
        Assert.AreEqual(1, calls);
        Assert.AreEqual(fail ? string.Empty : "new.exe", app.ExecutablePath);
        Assert.AreEqual(fail ? "hosted" : "standalone", app.TrayMode);
        Assert.AreEqual(fail ? "old" : "new.exe", persisted);
        Assert.HasCount(5, config.Apps);
    }
}
