using Eslee.TrayFolder.Services;

namespace Eslee.TrayFolder.Tests;

[TestClass]
public sealed class AutoPowerIdentityTests
{
    [TestMethod]
    public void ValidateAutoPower_ReturnsClearErrorForMissingPath()
    {
        var validator = new ExecutablePathValidator();
        var missing = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            Guid.NewGuid().ToString("N"),
            "AutoPower.App.exe");

        var result = validator.ValidateAutoPower(missing);

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.UserMessage!, "없습니다");
    }

    [TestMethod]
    public void IdentityPolicy_AcceptsExpectedExecutableProductAndCompany()
    {
        var identity = new ExecutableIdentity(
            "AutoPower.App.exe",
            "eslee Auto Power",
            "eslee",
            "1.0.4.0",
            "1.0.4+commit");

        Assert.IsTrue(AutoPowerIdentityPolicy.IsRecognized(identity));
    }

    [TestMethod]
    public void IdentityPolicy_RejectsLookalikeExecutable()
    {
        var identity = new ExecutableIdentity(
            "AutoPower.App.exe",
            "Unrelated Auto Power",
            "Someone else",
            "1.0.0.0",
            "1.0.0");

        Assert.IsFalse(AutoPowerIdentityPolicy.IsRecognized(identity));
    }

    [TestMethod]
    public void ProcessMatcher_RequiresNamePathAndProductIdentity()
    {
        const string configuredPath = @"C:\Program Files\eslee Auto Power\AutoPower.App.exe";
        var matchingIdentity = new ExecutableIdentity(
            "AutoPower.App.exe",
            "eslee Auto Power",
            "eslee",
            "1.0.4.0",
            "1.0.4");
        var matching = new ProcessDescriptor(10, "AutoPower.App", configuredPath, matchingIdentity);
        var wrongPath = matching with { ExecutablePath = @"C:\Other\AutoPower.App.exe" };
        var wrongName = matching with { ProcessName = "AutoPower" };

        Assert.IsTrue(AutoPowerProcessMatcher.IsMatch(matching, configuredPath));
        Assert.IsFalse(AutoPowerProcessMatcher.IsMatch(wrongPath, configuredPath));
        Assert.IsFalse(AutoPowerProcessMatcher.IsMatch(wrongName, configuredPath));
    }
}
