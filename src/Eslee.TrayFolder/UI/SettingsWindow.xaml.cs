using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Eslee.TrayFolder.UI;

/// <summary>설정 창에 표시할 앱 한 개의 스냅숏입니다. TrayMode는 "hosted" 또는 "standalone"입니다.</summary>
public sealed record SettingsAppEntry(
    string AppId,
    string DisplayName,
    string ExecutablePath,
    string TrayMode,
    bool SupportsDiscovery);

/// <summary>앱 하나의 저장 값 묶음입니다. 저장 버튼은 모든 앱의 목록을 한 번에 전달합니다.</summary>
public sealed record SettingsSaveRequest(string AppId, string ExecutablePath, string TrayMode);

public partial class SettingsWindow : Window
{
    private readonly List<AppSection> _sections = [];
    private bool _allowClose;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    private string? _releaseUrl;

    /// <summary>저장 버튼: 화면의 모든 앱 설정을 한 번에 전달합니다.</summary>
    public event EventHandler<IReadOnlyList<SettingsSaveRequest>>? SaveAllRequested;

    /// <summary>앱별 자동 탐색 버튼 클릭. 해당 앱 id를 전달합니다.</summary>
    public event EventHandler<string>? DiscoveryRequested;

    /// <summary>수동 '업데이트 확인' 버튼 클릭.</summary>
    public event EventHandler? UpdateCheckRequested;

    /// <summary>프로그램 정보 영역의 현재 버전 표시를 설정합니다.</summary>
    public void SetVersionText(string versionText) =>
        VersionText.Text = $"eslee Tray Folder {versionText}";

    /// <summary>
    /// 업데이트 확인 결과를 표시합니다. releaseUrl이 있으면 Release 페이지 버튼을 보여줍니다.
    /// </summary>
    public void SetUpdateStatus(string statusText, string? releaseUrl, bool checkInProgress = false)
    {
        UpdateStatusText.Text = statusText;
        _releaseUrl = releaseUrl;
        OpenReleaseButton.Visibility = string.IsNullOrWhiteSpace(releaseUrl)
            ? Visibility.Collapsed
            : Visibility.Visible;
        CheckUpdateButton.IsEnabled = !checkInProgress;
    }

    /// <summary>모든 앱 섹션을 다시 만듭니다.</summary>
    public void SetApps(IReadOnlyList<SettingsAppEntry> apps)
    {
        AppSectionsPanel.Children.Clear();
        _sections.Clear();
        for (var index = 0; index < apps.Count; index++)
        {
            var section = BuildSection(apps[index]);
            _sections.Add(section);
            AppSectionsPanel.Children.Add(section.Root);
            if (index < apps.Count - 1)
            {
                AppSectionsPanel.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 16, 0, 16),
                    Background = new SolidColorBrush(Color.FromRgb(229, 233, 240)),
                });
            }
        }
    }

    /// <summary>자동 탐색 결과 등으로 특정 앱의 경로 입력값만 갱신합니다(저장 전).</summary>
    public void SetAppPath(string appId, string path)
    {
        var section = FindSection(appId);
        if (section is not null)
        {
            section.PathBox.Text = path;
        }
    }

    public void ShowMessage(string message, bool isError)
    {
        MessageText.Foreground = new SolidColorBrush(
            isError
                ? Color.FromRgb(196, 61, 61)
                : Color.FromRgb(28, 128, 83));
        MessageText.Text = message;
    }

    public void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    private AppSection BuildSection(SettingsAppEntry app)
    {
        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Text = app.DisplayName,
        });

        var pathGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pathBox = new TextBox
        {
            Height = 31,
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = app.ExecutablePath,
        };
        pathGrid.Children.Add(pathBox);

        var browseButton = new Button
        {
            Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(13, 5, 13, 5),
            Content = "찾아보기",
        };
        browseButton.Click += (_, _) => BrowseForExecutable(app.DisplayName, pathBox);
        Grid.SetColumn(browseButton, 1);
        pathGrid.Children.Add(browseButton);

        var discoverButton = new Button
        {
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(13, 5, 13, 5),
            Content = "자동 탐색",
            Visibility = app.SupportsDiscovery ? Visibility.Visible : Visibility.Collapsed,
        };
        discoverButton.Click += (_, _) => DiscoveryRequested?.Invoke(this, app.AppId);
        Grid.SetColumn(discoverButton, 2);
        pathGrid.Children.Add(discoverButton);
        root.Children.Add(pathGrid);

        var hosted = string.Equals(app.TrayMode, "hosted", StringComparison.OrdinalIgnoreCase);
        var standaloneRadio = new RadioButton
        {
            Margin = new Thickness(0, 10, 0, 0),
            GroupName = $"tray-mode-{app.AppId}",
            Content = "Standalone — 앱이 자체 트레이 아이콘을 표시합니다.",
            IsChecked = !hosted,
        };
        var hostedRadio = new RadioButton
        {
            Margin = new Thickness(0, 4, 0, 0),
            GroupName = $"tray-mode-{app.AppId}",
            Content = "Hosted — 앱 트레이 아이콘을 숨기고 Tray Folder가 대신 관리합니다.",
            IsChecked = hosted,
        };
        root.Children.Add(standaloneRadio);
        root.Children.Add(hostedRadio);

        return new AppSection(app.AppId, root, pathBox, hostedRadio);
    }

    private AppSection? FindSection(string appId) => _sections.FirstOrDefault(
        section => string.Equals(section.AppId, appId, StringComparison.OrdinalIgnoreCase));

    private void BrowseForExecutable(string displayName, TextBox pathBox)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"{displayName} 실행 파일 선택",
            Filter = "실행 파일 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            pathBox.Text = dialog.FileName;
            MessageText.Text = string.Empty;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var requests = _sections
            .Select(section => new SettingsSaveRequest(
                section.AppId,
                section.PathBox.Text,
                section.HostedRadio.IsChecked == true ? "hosted" : "standalone"))
            .ToList();
        if (requests.Count > 0)
        {
            SaveAllRequested?.Invoke(this, requests);
        }
    }

    private void OnCheckUpdateClick(object sender, RoutedEventArgs e) =>
        UpdateCheckRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenReleaseClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_releaseUrl))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_releaseUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            ShowMessage("브라우저를 열지 못했습니다. Release 페이지 주소: " + _releaseUrl, isError: true);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private sealed record AppSection(string AppId, StackPanel Root, TextBox PathBox, RadioButton HostedRadio);
}
