using Eslee.TrayFolder.Models;
using Eslee.TrayFolder.Services;

namespace Eslee.TrayFolder.Tests;

[TestClass]
public sealed class SingleInstanceAndOrderingTests
{
    [TestMethod]
    public void SingleInstanceDecision_MapsMutexResultToExpectedRole()
    {
        Assert.AreEqual(SingleInstanceRole.Primary, SingleInstanceDecision.FromMutexCreation(true));
        Assert.AreEqual(SingleInstanceRole.Secondary, SingleInstanceDecision.FromMutexCreation(false));
    }

    [TestMethod]
    public void SingleInstanceManager_AllowsOnePrimaryAndSignalsItFromSecondary()
    {
        var applicationId = $"eslee.trayfolder.tests.{Guid.NewGuid():N}";
        using var signalReceived = new ManualResetEventSlim();
        using var primary = new SingleInstanceManager(applicationId);
        primary.Listen(signalReceived.Set);
        using var secondary = new SingleInstanceManager(applicationId);

        secondary.SignalPrimary();

        Assert.AreEqual(SingleInstanceRole.Primary, primary.Role);
        Assert.AreEqual(SingleInstanceRole.Secondary, secondary.Role);
        Assert.IsTrue(signalReceived.Wait(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public void EnabledInDisplayOrder_FiltersAndSortsApps()
    {
        var apps = new[]
        {
            new TrayAppConfig { AppId = "c", DisplayName = "Zulu", Order = 2, Enabled = true },
            new TrayAppConfig { AppId = "b", DisplayName = "Beta", Order = 1, Enabled = true },
            new TrayAppConfig { AppId = "a", DisplayName = "Alpha", Order = 1, Enabled = true },
            new TrayAppConfig { AppId = "off", DisplayName = "Disabled", Order = 0, Enabled = false },
        };

        var ordered = AppOrdering.EnabledInDisplayOrder(apps);

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, ordered.Select(app => app.AppId).ToArray());
    }
}
