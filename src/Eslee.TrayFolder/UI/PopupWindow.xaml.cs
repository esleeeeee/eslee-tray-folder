using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Eslee.TrayFolder.Native;
using Eslee.TrayIntegration;
using FormsScreen = System.Windows.Forms.Screen;

namespace Eslee.TrayFolder.UI;

public partial class PopupWindow : Window
{
    private bool _allowClose;
    private bool _busy;
    private ContextMenu? _autoPowerMenu;

    public PopupWindow()
    {
        InitializeComponent();
        Left = -10_000;
        Top = -10_000;
        IsVisibleChanged += OnPopupVisibleChanged;
    }

    public event EventHandler? SettingsRequested;

    public event EventHandler? AutoPowerRequested;

    public event EventHandler? AutoPowerMenuRequested;

    public event EventHandler<string>? AutoPowerMenuActionRequested;

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

    /// <summary>
    /// 팝업이 화면에 표시된 회차를 구분하는 토큰입니다. 우클릭 시점의 값을 캡처해
    /// <see cref="ShowAutoPowerMenu"/>에 되돌려주면, 응답이 늦게 도착했을 때 그 사이
    /// 팝업이 닫혔다 다시 열렸어도 다른 회차에 유령 메뉴가 뜨지 않습니다.
    /// </summary>
    public int VisibleSession { get; private set; }

    public void SetBusy(bool isBusy)
    {
        // 좌클릭(앱 열기)만 막고 우클릭 메뉴는 busy 중에도 열 수 있어야 하므로
        // 버튼을 비활성화하지 않습니다(비활성 요소는 우클릭 이벤트도 삼킵니다).
        _busy = isBusy;
        AutoPowerButton.Opacity = isBusy ? 0.55 : 1.0;
    }

    /// <summary>
    /// AutoPower 타일 우클릭 메뉴를 커서 위치에 표시합니다. 항목 클릭 시
    /// <see cref="AutoPowerMenuActionRequested"/>로 action id를 전달합니다.
    /// </summary>
    public void ShowAutoPowerMenu(IReadOnlyList<TrayMenuItem> items, int visibleSession)
    {
        if (!IsVisible || visibleSession != VisibleSession)
        {
            // 메뉴 데이터를 기다리는 사이 팝업이 닫혔거나 다시 열린 경우입니다.
            return;
        }

        CloseAutoPowerMenu();
        var menu = new ContextMenu
        {
            PlacementTarget = AutoPowerButton,
            Placement = PlacementMode.MousePoint,
        };
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                menu.Items.Add(new Separator());
                continue;
            }

            var menuItem = new MenuItem
            {
                Header = item.Text,
                IsEnabled = item.Enabled,
            };
            if (item.Id is { Length: > 0 } actionId && item.Enabled)
            {
                menuItem.Click += (_, _) => AutoPowerMenuActionRequested?.Invoke(this, actionId);
            }

            menu.Items.Add(menuItem);
        }

        menu.Closed += OnAutoPowerMenuClosed;
        _autoPowerMenu = menu;
        menu.IsOpen = true;
    }

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
        CloseAutoPowerMenu();
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

    private void OnAutoPowerClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        AutoPowerRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAutoPowerRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        AutoPowerMenuRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAutoPowerMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            menu.Closed -= OnAutoPowerMenuClosed;
            if (ReferenceEquals(_autoPowerMenu, menu))
            {
                _autoPowerMenu = null;
            }
        }

        // 메뉴가 열려 있는 동안에는 Deactivated로 숨기지 않으므로,
        // 메뉴가 닫힌 뒤 팝업이 비활성 상태라면 기존 규칙대로 숨깁니다.
        if (IsVisible && !IsActive)
        {
            Hide();
        }
    }

    private void OnPopupVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        VisibleSession++;
        if (!IsVisible)
        {
            CloseAutoPowerMenu();
        }
    }

    private void CloseAutoPowerMenu()
    {
        if (_autoPowerMenu is { } menu)
        {
            menu.Closed -= OnAutoPowerMenuClosed;
            _autoPowerMenu = null;
            menu.IsOpen = false;
        }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // 우클릭 메뉴가 포커스를 가져가며 창이 비활성화되므로, 메뉴가 열려 있는
        // 동안에는 숨기지 않습니다. 메뉴가 닫힐 때 OnAutoPowerMenuClosed가 정리합니다.
        if (_autoPowerMenu is null)
        {
            Hide();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
