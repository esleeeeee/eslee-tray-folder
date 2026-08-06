namespace Eslee.TrayFolder.UI;

public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

public readonly record struct PixelPoint(double X, double Y);

public readonly record struct PixelSize(double Width, double Height);

public readonly record struct PixelRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

public static class PopupPositionCalculator
{
    private const double Gap = 8;

    public static TaskbarEdge InferTaskbarEdge(PixelRect monitorBounds, PixelRect workingArea)
    {
        var insets = new[]
        {
            (Edge: TaskbarEdge.Left, Size: workingArea.Left - monitorBounds.Left),
            (Edge: TaskbarEdge.Top, Size: workingArea.Top - monitorBounds.Top),
            (Edge: TaskbarEdge.Right, Size: monitorBounds.Right - workingArea.Right),
            (Edge: TaskbarEdge.Bottom, Size: monitorBounds.Bottom - workingArea.Bottom),
        };
        var largest = insets.OrderByDescending(inset => inset.Size).First();
        return largest.Size > 0 ? largest.Edge : TaskbarEdge.Bottom;
    }

    public static PixelPoint Calculate(
        PixelPoint anchor,
        PixelSize popup,
        PixelRect workingArea,
        TaskbarEdge taskbarEdge)
    {
        var left = taskbarEdge switch
        {
            TaskbarEdge.Left => workingArea.Left + Gap,
            TaskbarEdge.Right => workingArea.Right - popup.Width - Gap,
            _ => anchor.X - popup.Width + 24,
        };
        var top = taskbarEdge switch
        {
            TaskbarEdge.Top => workingArea.Top + Gap,
            TaskbarEdge.Bottom => workingArea.Bottom - popup.Height - Gap,
            _ => anchor.Y - (popup.Height / 2),
        };

        return new PixelPoint(
            Clamp(left, workingArea.Left + Gap, workingArea.Right - popup.Width - Gap),
            Clamp(top, workingArea.Top + Gap, workingArea.Bottom - popup.Height - Gap));
    }

    private static double Clamp(double value, double minimum, double maximum) =>
        maximum < minimum ? minimum : Math.Clamp(value, minimum, maximum);
}
