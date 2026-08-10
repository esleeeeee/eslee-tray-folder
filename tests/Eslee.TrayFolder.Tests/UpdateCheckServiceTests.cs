using Eslee.TrayFolder.Services;

namespace Eslee.TrayFolder.Tests;

[TestClass]
public sealed class UpdateCheckServiceTests
{
    [TestMethod]
    public void TryParseVersionTagAcceptsPrefixedAndPlainTags()
    {
        Assert.IsTrue(UpdateCheckService.TryParseVersionTag("v0.1.2", out var prefixed));
        Assert.AreEqual(new Version(0, 1, 2), prefixed);

        Assert.IsTrue(UpdateCheckService.TryParseVersionTag("1.2.3", out var plain));
        Assert.AreEqual(new Version(1, 2, 3), plain);

        Assert.IsTrue(UpdateCheckService.TryParseVersionTag("V2.0", out var twoPart));
        Assert.AreEqual(new Version(2, 0, 0), twoPart);

        Assert.IsFalse(UpdateCheckService.TryParseVersionTag(null, out _));
        Assert.IsFalse(UpdateCheckService.TryParseVersionTag("latest", out _));
        Assert.IsFalse(UpdateCheckService.TryParseVersionTag("v1.2.beta", out _));
    }

    [TestMethod]
    public void IsNewerOnlyWhenReleaseIsAboveCurrentVersion()
    {
        Assert.IsTrue(UpdateCheckService.IsNewer(new Version(0, 1, 2), new Version(0, 1, 1)));
        Assert.IsTrue(UpdateCheckService.IsNewer(new Version(1, 0, 0), new Version(0, 9, 9)));

        // 같은 버전과, 릴리스보다 높은 개발 빌드에서는 업데이트를 권하지 않습니다.
        Assert.IsFalse(UpdateCheckService.IsNewer(new Version(0, 1, 2), new Version(0, 1, 2)));
        Assert.IsFalse(UpdateCheckService.IsNewer(new Version(0, 1, 1), new Version(0, 1, 2)));

        // 4자리 어셈블리 버전(0.1.2.0)과 3자리 태그 버전을 동일하게 비교합니다.
        Assert.IsFalse(UpdateCheckService.IsNewer(new Version(0, 1, 2), new Version(0, 1, 2, 0)));
        Assert.IsTrue(UpdateCheckService.IsNewer(new Version(0, 1, 3), new Version(0, 1, 2, 0)));
    }

    [TestMethod]
    public void FormatVersionUsesThreePartsWithPrefix()
    {
        Assert.AreEqual("v0.1.2", UpdateCheckService.FormatVersion(new Version(0, 1, 2, 0)));
        Assert.AreEqual("v1.2.0", UpdateCheckService.FormatVersion(new Version(1, 2)));
    }
}
