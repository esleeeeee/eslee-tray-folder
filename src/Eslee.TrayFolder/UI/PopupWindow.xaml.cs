using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Eslee.TrayFolder.Native;
using FormsScreen = System.Windows.Forms.Screen;

namespace Eslee.TrayFolder.UI;

public partial class PopupWindow : Window
{
    private bool _allowClose;

    public PopupWindow()
    {
        InitializeComponent();
        Left = -10_000;
        Top = -10_000;
    }

    public event EventHandler? SettingsRequested;

    public event EventHandler? AutoPowerRequested;

    public void SetApp(string displayName, ImageSource? icon)
    {
        AppNameText.Text = displayName;
        AppIconImage.Source = icon;
        AppIconImage.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        FallbackIconText.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetRunningState(bool? isRunning)
    {
        StatusText.Text = isRunning switch
        {
            true => "실행 중",
            false => "실행 안 됨",
            null => "확인 중",
        };
        StatusDot.Fill = new SolidColorBrush(isRunning switch
        {
            true => Color.FromRgb(34, 171, 105),
            false => Color.FromRgb(154, 163, 178),
            null => Color.FromRgb(244, 168, 37),
        });
    }

    public void SetBusy(bool isBusy) => AutoPowerButton.IsEnabled = !isBusy;

    public void ShowAt(PixelPoint anchor)
    {
        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        PositionAt(anchor);
        Dispatcher.BeginInvoke(() => PositionAt(anchor));
        Activate();
        Focus();
    }

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    private void PositionAt(PixelPoint anchor)
    {
        var screen = FormsScreen.FromPoint(
            new System.Drawing.Point((int)Math.Round(anchor.X), (int)Math.Round(anchor.Y)));
        var bounds = new PixelRect(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height);
        var work = new PixelRect(
            screen.WorkingArea.Left,
            screen.WorkingArea.Top,
            screen.WorkingArea.Width,
            screen.WorkingArea.Height);
        var dpi = VisualTreeHelper.GetDpi(this);
        var size = new PixelSize(ActualWidth * dpi.DpiScaleX, ActualHeight * dpi.DpiScaleY);
        var edge = PopupPositionCalculator.InferTaskbarEdge(bounds, work);
        var point = PopupPositionCalculator.Calculate(anchor, size, work, edge);
        var handle = new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HwndTopmost,
            (int)Math.Round(point.X),
            (int)Math.Round(point.Y),
            (int)Math.Ceiling(size.Width),
            (int)Math.Ceiling(size.Height),
            0);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        Hide();
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAutoPowerClick(object sender, RoutedEventArgs e) =>
        AutoPowerRequested?.Invoke(this, EventArgs.Empty);

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e) => Hide();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
