using Eslee.TrayIntegration;

namespace Eslee.TrayIntegration.Tests;

[TestClass]
public sealed class TrayContractsTests
{
    [TestMethod]
    public void RegistrationCarriesFutureHostedModeWithoutTransportImplementation()
    {
        var registration = new TrayAppRegistration(
            1,
            "eslee.autopower",
            "AutoPower",
            TrayMode.Hosted,
            1234);

        Assert.AreEqual(TrayMode.Hosted, registration.RequestedMode);
        Assert.AreEqual("eslee.autopower", registration.AppId);
    }
}
