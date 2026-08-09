using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Eslee.TrayFolder.Native;
using Eslee.TrayIntegration;
using Ellipse = System.Windows.Shapes.Ellipse;
using FormsScreen = System.Windows.Forms.Screen;

namespace Eslee.TrayFolder.UI;

/// <summary>앱 타일 우클릭 메뉴에서 항목이 클릭됐을 때의 정보입니다.</summary>
public sealed record AppMenuActionRequest(string AppId, string ActionId);

public partial class PopupWindow : Window
{
    private static readonly SolidColorBrush RunningBrush = new(Color.FromRgb(34, 171, 105));
    private static readonly SolidColorBrush StoppedBrush = new(Color.FromRgb(154, 163, 178));
    private static readonly SolidColorBrush UnknownBrush = new(Color.FromRgb(244, 168, 37));

    private readonly Dictionary<string, AppTile> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _allowClose;
    private ContextMenu? _appMenu;

    public PopupWindow()
    {
        InitializeComponent();
        Left = -10_000;
        Top = -10_000;
        IsVisibleChanged += OnPopupVisibleChanged;
    }

    public event EventHandler? SettingsRequested;

    /// <summary>타일 좌클릭: 해당 앱의 메인 동작(창 열기/실행)을 요청합니다.</summary>
    public event EventHandler<string>? AppRequested;

    /// <summary>타일 우클릭: 해당 앱의 트레이 메뉴 표시를 요청합니다.</summary>
    public event EventHandler<string>? AppMenuRequested;

    public event EventHandler<AppMenuActionRequest>? AppMenuActionRequested;

    /// <summary>
    /// 팝업이 화면에 표시된 회차를 구분하는 토큰입니다. 우클릭 시점의 값을 캡처해
    /// <see cref="ShowAppMenu"/>에 되돌려주면, 응답이 늦게 도착했을 때 그 사이
    /// 팝업이 닫혔다 다시 열렸어도 다른 회차에 유령 메뉴가 뜨지 않습니다.
    /// </summary>
    public int VisibleSession { get; private set; }

    /// <summary>표시할 앱 타일 목록을 다시 만듭니다. 순서대로 3열 격자에 배치됩니다.</summary>
    public void SetApps(IReadOnlyList<(string AppId, string DisplayName)> apps)
    {
        AppsGrid.Children.Clear();
        _tiles.Clear();
        foreach (var (appId, displayName) in apps)
        {
            var tile = BuildTile(appId, displayName);
            _tiles[appId] = tile;
            AppsGrid.Children.Add(tile.Button);
        }
    }

    public void SetApp(string appId, string displayName, ImageSource? icon)
    {
        if (!_tiles.TryGetValue(appId, out var tile))
        {
            return;
        }

        tile.NameText.Text = displayName;
        tile.FallbackIconText.Text = displayName.Length > 0 ? displayName[..1] : "?";
        tile.IconImage.Source = icon;
        tile.IconImage.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        tile.FallbackIconText.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetRunningState(string appId, bool? isRunning)
    {
        if (!_tiles.TryGetValue(appId, out var tile))
        {
            return;
        }

        tile.StatusText.Text = isRunning switch
        {
            true => "실행 중",
            false => "실행 안 됨",
            null => "확인 중",
        };
        tile.StatusDot.Fill = isRunning switch
        {
            true => RunningBrush,
            false => StoppedBrush,
            null => UnknownBrush,
        };
    }

    public void SetBusy(string appId, bool isBusy)
    {
        if (!_tiles.TryGetValue(appId, out var tile))
        {
            return;
        }

        // 좌클릭(메인 동작)만 막고 우클릭 메뉴는 busy 중에도 열 수 있어야 하므로
        // 버튼을 비활성화하지 않습니다(비활성 요소는 우클릭 이벤트도 삼킵니다).
        tile.Busy = isBusy;
        tile.Button.Opacity = isBusy ? 0.55 : 1.0;
    }

    /// <summary>
    /// 앱 타일 우클릭 메뉴를 커서 위치에 표시합니다. 항목 클릭 시
    /// <see cref="AppMenuActionRequested"/>로 앱과 action id를 전달합니다.
    /// </summary>
    public void ShowAppMenu(string appId, IReadOnlyList<TrayMenuItem> items, int visibleSession)
    {
        if (!IsVisible || visibleSession != VisibleSession || !_tiles.TryGetValue(appId, out var tile))
        {
            // 메뉴 데이터를 기다리는 사이 팝업이 닫혔거나 다시 열린 경우입니다.
            return;
        }

        CloseAppMenu();
        var menu = new ContextMenu
        {
            PlacementTarget = tile.Button,
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
                IsChecked = item.Checked,
            };
            if (item.Id is { Length: > 0 } actionId && item.Enabled)
            {
                menuItem.Click += (_, _) =>
                    AppMenuActionRequested?.Invoke(this, new AppMenuActionRequest(appId, actionId));
            }

            menu.Items.Add(menuItem);
        }

        menu.Closed += OnAppMenuClosed;
        _appMenu = menu;
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
        CloseAppMenu();
        Close();
    }

    private AppTile BuildTile(string appId, string displayName)
    {
        var fallbackIcon = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 30,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(44, 107, 237)),
            Text = displayName.Length > 0 ? displayName[..1] : "?",
        };
        var iconImage = new Image
        {
            Width = 46,
            Height = 46,
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed,
        };
        var iconGrid = new Grid();
        iconGrid.Children.Add(fallbackIcon);
        iconGrid.Children.Add(iconImage);
        var iconBorder = new Border
        {
            Width = 68,
            Height = 68,
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromRgb(234, 242, 255)),
            Child = iconGrid,
        };

        var nameText = new TextBlock
        {
            Margin = new Thickness(0, 9, 0, 0),
            MaxWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Text = displayName,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var statusDot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Margin = new Thickness(0, 0, 5, 0),
            Fill = StoppedBrush,
        };
        var statusText = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 133)),
            Text = "확인 중",
        };
        var statusPanel = new StackPanel
        {
            Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Orientation = Orientation.Horizontal,
        };
        statusPanel.Children.Add(statusDot);
        statusPanel.Children.Add(statusText);

        var contentPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        contentPanel.Children.Add(iconBorder);
        contentPanel.Children.Add(nameText);
        contentPanel.Children.Add(statusPanel);

        var button = new Button
        {
            Width = 96,
            Height = 142,
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = contentPanel,
        };
        var tile = new AppTile
        {
            Button = button,
            IconImage = iconImage,
            FallbackIconText = fallbackIcon,
            NameText = nameText,
            StatusDot = statusDot,
            StatusText = statusText,
        };
        button.Click += (_, _) =>
        {
            if (!tile.Busy)
            {
                AppRequested?.Invoke(this, appId);
            }
        };
        button.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            AppMenuRequested?.Invoke(this, appId);
        };
        return tile;
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

    private void OnAppMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            menu.Closed -= OnAppMenuClosed;
            if (ReferenceEquals(_appMenu, menu))
            {
                _appMenu = null;
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
            CloseAppMenu();
        }
    }

    private void CloseAppMenu()
    {
        if (_appMenu is { } menu)
        {
            menu.Closed -= OnAppMenuClosed;
            _appMenu = null;
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
        // 동안에는 숨기지 않습니다. 메뉴가 닫힐 때 OnAppMenuClosed가 정리합니다.
        if (_appMenu is null)
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

    private sealed class AppTile
    {
        public required Button Button { get; init; }

        public required Image IconImage { get; init; }

        public required TextBlock FallbackIconText { get; init; }

        public required TextBlock NameText { get; init; }

        public required Ellipse StatusDot { get; init; }

        public required TextBlock StatusText { get; init; }

        public bool Busy { get; set; }
    }
}
