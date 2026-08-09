using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Eslee.TrayFolder.UI;

/// <summary>설정 창에 표시할 앱 한 개의 스냅숏입니다. TrayMode는 "hosted" 또는 "standalone"입니다.</summary>
public sealed record SettingsAppEntry(
    string AppId,
    string DisplayName,
    string ExecutablePath,
    string TrayMode,
    bool SupportsDiscovery);

/// <summary>설정 창의 저장 버튼이 전달하는 값 묶음입니다.</summary>
public sealed record SettingsSaveRequest(string AppId, string ExecutablePath, string TrayMode);

public partial class SettingsWindow : Window
{
    private readonly List<SettingsAppEntry> _apps = [];
    private bool _allowClose;
    private bool _loadingSelection;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public event EventHandler<SettingsSaveRequest>? SaveRequested;

    /// <summary>자동 탐색 버튼 클릭. 현재 선택된 앱 id를 전달합니다(현재는 AutoPower만 지원).</summary>
    public event EventHandler<string>? DiscoveryRequested;

    public string? SelectedAppId =>
        AppSelector.SelectedIndex >= 0 && AppSelector.SelectedIndex < _apps.Count
            ? _apps[AppSelector.SelectedIndex].AppId
            : null;

    public string ExecutablePath
    {
        get => ExecutablePathTextBox.Text;
        set => ExecutablePathTextBox.Text = value;
    }

    public string TrayMode
    {
        get => HostedModeRadio.IsChecked == true ? "hosted" : "standalone";
        set
        {
            var hosted = string.Equals(value, "hosted", StringComparison.OrdinalIgnoreCase);
            HostedModeRadio.IsChecked = hosted;
            StandaloneModeRadio.IsChecked = !hosted;
        }
    }

    /// <summary>앱 목록을 채우고 선택을 복원합니다. 저장 후 갱신에도 사용합니다.</summary>
    public void SetApps(IReadOnlyList<SettingsAppEntry> apps, string? selectAppId = null)
    {
        var previousSelection = selectAppId ?? SelectedAppId;
        _apps.Clear();
        _apps.AddRange(apps);
        _loadingSelection = true;
        try
        {
            AppSelector.Items.Clear();
            foreach (var app in _apps)
            {
                AppSelector.Items.Add(app.DisplayName);
            }

            var index = _apps.FindIndex(app =>
                string.Equals(app.AppId, previousSelection, StringComparison.OrdinalIgnoreCase));
            AppSelector.SelectedIndex = index >= 0 ? index : (_apps.Count > 0 ? 0 : -1);
        }
        finally
        {
            _loadingSelection = false;
        }

        LoadSelectedApp();
    }

    public void ShowMessage(string message, bool isError)
    {
        MessageText.Foreground = new System.Windows.Media.SolidColorBrush(
            isError
                ? System.Windows.Media.Color.FromRgb(196, 61, 61)
                : System.Windows.Media.Color.FromRgb(28, 128, 83));
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

    private void LoadSelectedApp()
    {
        var index = AppSelector.SelectedIndex;
        if (index < 0 || index >= _apps.Count)
        {
            return;
        }

        var app = _apps[index];
        ExecutablePathLabel.Text = $"{app.DisplayName} 실행 파일";
        ExecutablePath = app.ExecutablePath;
        TrayMode = app.TrayMode;
        DiscoverButton.Visibility = app.SupportsDiscovery ? Visibility.Visible : Visibility.Collapsed;
        MessageText.Text = string.Empty;
    }

    private void OnAppSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingSelection)
        {
            LoadSelectedApp();
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var index = AppSelector.SelectedIndex;
        var displayName = index >= 0 && index < _apps.Count ? _apps[index].DisplayName : "앱";
        var dialog = new OpenFileDialog
        {
            Title = $"{displayName} 실행 파일 선택",
            Filter = "실행 파일 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            ExecutablePath = dialog.FileName;
            MessageText.Text = string.Empty;
        }
    }

    private void OnDiscoverClick(object sender, RoutedEventArgs e)
    {
        if (SelectedAppId is string appId)
        {
            DiscoveryRequested?.Invoke(this, appId);
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (SelectedAppId is string appId)
        {
            SaveRequested?.Invoke(this, new SettingsSaveRequest(appId, ExecutablePath, TrayMode));
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
}
