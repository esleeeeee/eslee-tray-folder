using System.Windows;
using System.Windows.Threading;
using Eslee.TrayFolder.Models;
using Eslee.TrayFolder.Native;
using Eslee.TrayFolder.UI;

namespace Eslee.TrayFolder.Services;

public sealed class TrayFolderController : IDisposable
{
    private readonly TrayFolderConfig _config;
    private readonly ConfigService _configService;
    private readonly AutoPowerDiscoveryService _discoveryService;
    private readonly ExecutablePathValidator _pathValidator;
    private readonly AutoPowerProcessService _processService;
    private readonly IAppLogger _logger;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly PopupWindow _popupWindow = new();
    private readonly SettingsWindow _settingsWindow = new();
    private readonly DispatcherTimer _statusTimer;
    private TrayIconController? _trayIcon;
    private int _refreshInProgress;
    private bool _disposed;

    public TrayFolderController(
        TrayFolderConfig config,
        ConfigService configService,
        AutoPowerDiscoveryService discoveryService,
        ExecutablePathValidator pathValidator,
        AutoPowerProcessService processService,
        IAppLogger logger)
    {
        _config = config;
        _configService = configService;
        _discoveryService = discoveryService;
        _pathValidator = pathValidator;
        _processService = processService;
        _logger = logger;

        EnsureAutoPowerEntry();
        _popupWindow.SettingsRequested += OnSettingsRequested;
        _popupWindow.AutoPowerRequested += OnAutoPowerRequested;
        _settingsWindow.SaveRequested += OnSettingsSaveRequested;
        _settingsWindow.DiscoveryRequested += OnSettingsDiscoveryRequested;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _statusTimer.Tick += OnStatusTimerTick;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _trayIcon = new TrayIconController(
            TogglePopup,
            ShowPopup,
            ShowSettings,
            () => System.Windows.Application.Current.Shutdown(),
            _logger);

        await EnsureAutoPowerPathAsync(cancellationToken).ConfigureAwait(true);
        UpdateAppPresentation();
        await RefreshStatusAsync(cancellationToken).ConfigureAwait(true);
    }

    public void ActivateFromSecondInstance()
    {
        if (_disposed)
        {
            return;
        }

        if (_settingsWindow.IsVisible)
        {
            _settingsWindow.ShowAndActivate();
        }
        else
        {
            ShowPopup();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimerTick;
        _popupWindow.SettingsRequested -= OnSettingsRequested;
        _popupWindow.AutoPowerRequested -= OnAutoPowerRequested;
        _settingsWindow.SaveRequested -= OnSettingsSaveRequested;
        _settingsWindow.DiscoveryRequested -= OnSettingsDiscoveryRequested;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _popupWindow.ClosePermanently();
        _settingsWindow.ClosePermanently();
        _lifetimeCancellation.Dispose();
    }

    private TrayAppConfig AutoPower => _config.Apps.First(
        app => string.Equals(app.AppId, "eslee.autopower", StringComparison.OrdinalIgnoreCase));

    private void TogglePopup()
    {
        if (_popupWindow.IsVisible)
        {
            _popupWindow.Hide();
            _statusTimer.Stop();
        }
        else
        {
            ShowPopup();
        }
    }

    private void ShowPopup()
    {
        ThrowIfDisposed();
        UpdateAppPresentation();
        _popupWindow.SetRunningState(null);
        _popupWindow.ShowAt(TrayIconController.GetCursorPosition());
        _statusTimer.Start();
        _ = RefreshStatusSafelyAsync();
    }

    private void ShowSettings()
    {
        ThrowIfDisposed();
        _popupWindow.Hide();
        _statusTimer.Stop();
        _settingsWindow.ExecutablePath = AutoPower.ExecutablePath;
        _settingsWindow.ShowMessage(string.Empty, isError: false);
        _settingsWindow.ShowAndActivate();
    }

    private async Task EnsureAutoPowerPathAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(AutoPower.ExecutablePath))
        {
            return;
        }

        try
        {
            var discovered = await _discoveryService.DiscoverAsync(cancellationToken).ConfigureAwait(true);
            if (discovered.ExecutablePath is null)
            {
                await _logger.InfoAsync(discovered.Detail ?? "AutoPower 자동 탐색 결과가 없습니다.");
                return;
            }

            AutoPower.ExecutablePath = discovered.ExecutablePath;
            await _configService.SaveAsync(_config, cancellationToken).ConfigureAwait(true);
            await _logger.InfoAsync($"AutoPower 경로를 자동 탐색하고 저장했습니다 ({discovered.Source}): {discovered.ExecutablePath}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("AutoPower 자동 탐색 중 오류가 발생했습니다.", exception);
        }
    }

    private async Task RefreshStatusAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _refreshInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            var status = await _processService
                .GetStatusAsync(AutoPower.ExecutablePath, cancellationToken)
                .ConfigureAwait(true);
            if (!_disposed)
            {
                _popupWindow.SetRunningState(status.IsRunning);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInProgress, 0);
        }
    }

    private async Task RefreshStatusSafelyAsync()
    {
        try
        {
            await RefreshStatusAsync(_lifetimeCancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("AutoPower 실행 상태를 확인하지 못했습니다.", exception);
            if (!_disposed)
            {
                _popupWindow.SetRunningState(false);
            }
        }
    }

    private async void OnAutoPowerRequested(object? sender, EventArgs e)
    {
        _popupWindow.SetBusy(true);
        try
        {
            var result = await _processService
                .ActivateOrLaunchAsync(AutoPower.ExecutablePath, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                if (result.Exception is not null)
                {
                    await _logger.ErrorAsync("AutoPower 실행 또는 창 복원에 실패했습니다.", result.Exception);
                }
                else
                {
                    await _logger.InfoAsync(result.UserMessage ?? "AutoPower 작업에 실패했습니다.");
                }

                MessageBox.Show(
                    _popupWindow,
                    result.UserMessage ?? "AutoPower 작업을 완료하지 못했습니다.",
                    "Tray Folder",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            await Task.Delay(350, _lifetimeCancellation.Token).ConfigureAwait(true);
            await RefreshStatusSafelyAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("AutoPower 요청 처리 중 예상하지 못한 오류가 발생했습니다.", exception);
            MessageBox.Show(
                "AutoPower 요청을 처리하지 못했습니다. 자세한 내용은 로그를 확인해 주세요.",
                "Tray Folder",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (!_disposed)
            {
                _popupWindow.SetBusy(false);
            }
        }
    }

    private async void OnSettingsSaveRequested(object? sender, string path)
    {
        var validation = _pathValidator.ValidateAutoPower(path);
        if (!validation.IsValid || validation.NormalizedPath is null)
        {
            _settingsWindow.ShowMessage(validation.UserMessage ?? "올바른 실행 파일을 지정해 주세요.", isError: true);
            return;
        }

        try
        {
            AutoPower.ExecutablePath = validation.NormalizedPath;
            await _configService.SaveAsync(_config, _lifetimeCancellation.Token).ConfigureAwait(true);
            UpdateAppPresentation();
            _settingsWindow.ExecutablePath = validation.NormalizedPath;
            _settingsWindow.ShowMessage("AutoPower 실행 파일 경로를 저장했습니다.", isError: false);
            await RefreshStatusSafelyAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("설정을 저장하지 못했습니다.", exception);
            _settingsWindow.ShowMessage("설정을 저장하지 못했습니다. 로그를 확인해 주세요.", isError: true);
        }
    }

    private async void OnSettingsDiscoveryRequested(object? sender, EventArgs e)
    {
        _settingsWindow.ShowMessage("AutoPower를 찾는 중입니다…", isError: false);
        try
        {
            var result = await _discoveryService
                .DiscoverAsync(_lifetimeCancellation.Token)
                .ConfigureAwait(true);
            if (result.ExecutablePath is null)
            {
                _settingsWindow.ShowMessage(result.Detail ?? "AutoPower를 자동으로 찾지 못했습니다.", isError: true);
                return;
            }

            AutoPower.ExecutablePath = result.ExecutablePath;
            await _configService.SaveAsync(_config, _lifetimeCancellation.Token).ConfigureAwait(true);
            _settingsWindow.ExecutablePath = result.ExecutablePath;
            _settingsWindow.ShowMessage($"AutoPower를 찾아 저장했습니다. ({result.Source})", isError: false);
            UpdateAppPresentation();
            await RefreshStatusSafelyAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("설정 화면의 AutoPower 자동 탐색에 실패했습니다.", exception);
            _settingsWindow.ShowMessage("AutoPower 자동 탐색에 실패했습니다. 로그를 확인해 주세요.", isError: true);
        }
    }

    private void OnSettingsRequested(object? sender, EventArgs e) => ShowSettings();

    private async void OnStatusTimerTick(object? sender, EventArgs e)
    {
        if (!_popupWindow.IsVisible)
        {
            _statusTimer.Stop();
            return;
        }

        await RefreshStatusSafelyAsync().ConfigureAwait(true);
    }

    private void UpdateAppPresentation() =>
        _popupWindow.SetApp(AutoPower.DisplayName, AppIconProvider.TryLoad(AutoPower.ExecutablePath));

    private void EnsureAutoPowerEntry()
    {
        if (_config.Apps.Any(app => string.Equals(app.AppId, "eslee.autopower", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _config.Apps.Add(TrayFolderConfig.CreateDefault().Apps[0]);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
