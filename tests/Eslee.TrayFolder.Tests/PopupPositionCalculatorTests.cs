using Eslee.TrayFolder.UI;

namespace Eslee.TrayFolder.Tests;

[TestClass]
public sealed class PopupPositionCalculatorTests
{
    [TestMethod]
    public void InferTaskbarEdge_DetectsEveryWorkingAreaInset()
    {
        var bounds = new PixelRect(0, 0, 1920, 1080);

        Assert.AreEqual(TaskbarEdge.Bottom, PopupPositionCalculator.InferTaskbarEdge(bounds, new PixelRect(0, 0, 1920, 1040)));
        Assert.AreEqual(TaskbarEdge.Top, PopupPositionCalculator.InferTaskbarEdge(bounds, new PixelRect(0, 40, 1920, 1040)));
        Assert.AreEqual(TaskbarEdge.Left, PopupPositionCalculator.InferTaskbarEdge(bounds, new PixelRect(40, 0, 1880, 1080)));
        Assert.AreEqual(TaskbarEdge.Right, PopupPositionCalculator.InferTaskbarEdge(bounds, new PixelRect(0, 0, 1880, 1080)));
    }

    [TestMethod]
    public void Calculate_BottomTaskbarPlacesPopupAboveWorkingAreaBottom()
    {
        var result = PopupPositionCalculator.Calculate(
            new PixelPoint(1850, 1030),
            new PixelSize(360, 278),
            new PixelRect(0, 0, 1920, 1040),
            TaskbarEdge.Bottom);

        Assert.AreEqual(754, result.Y);
        Assert.IsGreaterThanOrEqualTo(8, result.X);
        Assert.IsLessThanOrEqualTo(1912, result.X + 360);
    }

    [TestMethod]
    public void Calculate_SideTaskbarClampsPopupToSelectedMonitor()
    {
        var work = new PixelRect(-1880, 0, 1880, 1080);
        var result = PopupPositionCalculator.Calculate(
            new PixelPoint(-1860, 1000),
            new PixelSize(720, 556),
            work,
            TaskbarEdge.Left);

        Assert.AreEqual(-1872, result.X);
        Assert.IsGreaterThanOrEqualTo(8, result.Y);
        Assert.IsLessThanOrEqualTo(1072, result.Y + 556);
    }
}
