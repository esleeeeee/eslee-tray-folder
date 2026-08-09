using System.Windows;
using System.Windows.Threading;
using Eslee.TrayFolder.Models;
using Eslee.TrayFolder.Native;
using Eslee.TrayFolder.UI;
using Eslee.TrayIntegration;

namespace Eslee.TrayFolder.Services;

public sealed class TrayFolderController : IDisposable
{
    private static readonly TimeSpan PipeCommandTimeout = TimeSpan.FromSeconds(3);

    private readonly TrayFolderConfig _config;
    private readonly ConfigService _configService;
    private readonly AutoPowerDiscoveryService _discoveryService;
    private readonly ExecutablePathValidator _pathValidator;
    private readonly AutoPowerProcessService _processService;
    private readonly TrayHostServer _hostServer;
    private readonly IAppLogger _logger;
    private readonly Dispatcher _dispatcher;
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
        TrayHostServer hostServer,
        IAppLogger logger)
    {
        _config = config;
        _configService = configService;
        _discoveryService = discoveryService;
        _pathValidator = pathValidator;
        _processService = processService;
        _hostServer = hostServer;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        EnsureAutoPowerEntry();
        _popupWindow.SettingsRequested += OnSettingsRequested;
        _popupWindow.AutoPowerRequested += OnAutoPowerRequested;
        _popupWindow.AutoPowerMenuRequested += OnAutoPowerMenuRequested;
        _popupWindow.AutoPowerMenuActionRequested += OnAutoPowerMenuActionRequested;
        _settingsWindow.SaveRequested += OnSettingsSaveRequested;
        _settingsWindow.DiscoveryRequested += OnSettingsDiscoveryRequested;
        _hostServer.ClientRegistered += OnHostClientRegistered;
        _hostServer.ClientDisconnected += OnHostClientDisconnected;
        _hostServer.Faulted += OnHostServerFaulted;

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
        _hostServer.Start();
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
        _popupWindow.AutoPowerMenuRequested -= OnAutoPowerMenuRequested;
        _popupWindow.AutoPowerMenuActionRequested -= OnAutoPowerMenuActionRequested;
        _settingsWindow.SaveRequested -= OnSettingsSaveRequested;
        _settingsWindow.DiscoveryRequested -= OnSettingsDiscoveryRequested;
        _hostServer.ClientRegistered -= OnHostClientRegistered;
        _hostServer.ClientDisconnected -= OnHostClientDisconnected;
        _hostServer.Faulted -= OnHostServerFaulted;
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
        _settingsWindow.TrayMode = AutoPower.TrayMode;
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
            if (_hostServer.IsClientConnected)
            {
                if (!_disposed)
                {
                    _popupWindow.SetRunningState(true);
                }

                return;
            }

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
            if (_hostServer.IsClientConnected)
            {
                var commandResult = await _hostServer
                    .SendCommandAsync(TrayHostCommand.Activate, PipeCommandTimeout, _lifetimeCancellation.Token)
                    .ConfigureAwait(true);
                if (commandResult.Succeeded)
                {
                    await Task.Delay(350, _lifetimeCancellation.Token).ConfigureAwait(true);
                    await RefreshStatusSafelyAsync().ConfigureAwait(true);
                    return;
                }

                await _logger.InfoAsync(
                    $"파이프 Activate에 실패해 창 복원 폴백을 사용합니다: {commandResult.ErrorMessage}");
            }

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

    private async void OnSettingsSaveRequested(object? sender, SettingsSaveRequest request)
    {
        var validation = _pathValidator.ValidateAutoPower(request.ExecutablePath);
        if (!validation.IsValid || validation.NormalizedPath is null)
        {
            _settingsWindow.ShowMessage(validation.UserMessage ?? "올바른 실행 파일을 지정해 주세요.", isError: true);
            return;
        }

        try
        {
            var requestedMode = TrayPipeProtocol.TryParseTrayMode(request.TrayMode, out var parsedMode)
                ? parsedMode
                : TrayMode.Standalone;
            AutoPower.ExecutablePath = validation.NormalizedPath;
            AutoPower.TrayMode = TrayPipeProtocol.FormatTrayMode(requestedMode);
            await _configService.SaveAsync(_config, _lifetimeCancellation.Token).ConfigureAwait(true);
            UpdateAppPresentation();
            _settingsWindow.ExecutablePath = validation.NormalizedPath;
            if (_hostServer.IsClientConnected)
            {
                var sent = await _hostServer
                    .SendTrayModeAsync(requestedMode, _lifetimeCancellation.Token)
                    .ConfigureAwait(true);
                _settingsWindow.ShowMessage(
                    sent
                        ? "설정을 저장하고 AutoPower에 즉시 적용했습니다."
                        : "설정을 저장했지만 AutoPower에 적용하지 못했습니다. 다시 연결되면 적용됩니다.",
                    isError: !sent);
            }
            else
            {
                _settingsWindow.ShowMessage(
                    "설정을 저장했습니다. AutoPower가 연결되면 트레이 모드가 적용됩니다.",
                    isError: false);
            }

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

    private async void OnAutoPowerMenuRequested(object? sender, EventArgs e)
    {
        // 응답을 기다리는 사이 팝업이 닫혔다 다시 열릴 수 있으므로,
        // 우클릭 시점의 표시 회차를 캡처해 그 회차에서만 메뉴를 엽니다.
        var session = _popupWindow.VisibleSession;
        try
        {
            if (!_hostServer.IsClientConnected)
            {
                _popupWindow.ShowAutoPowerMenu(
                    [TrayMenuItem.Action("not-connected", "AutoPower가 연결되지 않았습니다", enabled: false)],
                    session);
                return;
            }

            var items = await _hostServer
                .GetMenuAsync(PipeCommandTimeout, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (items is null || items.Count == 0)
            {
                await _logger.InfoAsync("AutoPower 메뉴를 가져오지 못했습니다 (응답 없음 또는 빈 메뉴).").ConfigureAwait(true);
                _popupWindow.ShowAutoPowerMenu(
                    [TrayMenuItem.Action("menu-unavailable", "메뉴를 가져오지 못했습니다", enabled: false)],
                    session);
                return;
            }

            _popupWindow.ShowAutoPowerMenu(items, session);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("AutoPower 메뉴 요청 처리 중 오류가 발생했습니다.", exception).ConfigureAwait(true);
        }
    }

    private async void OnAutoPowerMenuActionRequested(object? sender, string actionId)
    {
        try
        {
            var result = await _hostServer
                .SendMenuActionAsync(actionId, PipeCommandTimeout, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (!result.Succeeded)
            {
                await _logger.InfoAsync(
                    $"AutoPower 메뉴 명령을 실행하지 못했습니다 ({actionId}): {result.ErrorMessage}").ConfigureAwait(true);

                // 연결이 끊어진 경우는 알리지 않습니다: '앱 종료' 명령의 정상 결과이거나,
                // 앱 종료로 타일 상태가 이미 '실행 안 됨'으로 바뀌는 상황입니다.
                if (_hostServer.IsClientConnected)
                {
                    MessageBox.Show(
                        result.ErrorMessage ?? "AutoPower 메뉴 명령을 실행하지 못했습니다.",
                        "Tray Folder",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }

            await RefreshStatusSafelyAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync($"AutoPower 메뉴 명령 처리 중 오류가 발생했습니다 ({actionId}).", exception)
                .ConfigureAwait(true);
        }
    }

    private TrayMode ConfiguredTrayMode =>
        TrayPipeProtocol.TryParseTrayMode(AutoPower.TrayMode, out var mode) ? mode : TrayMode.Standalone;

    private void OnHostClientRegistered(object? sender, TrayAppRegistration registration) =>
        _ = _dispatcher.InvokeAsync(async () =>
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (!string.Equals(registration.AppId, TrayPipeProtocol.AutoPowerAppId, StringComparison.OrdinalIgnoreCase))
                {
                    await _logger.InfoAsync(
                        $"알 수 없는 앱의 파이프 등록을 무시했습니다: {registration.AppId}").ConfigureAwait(true);
                    return;
                }

                await _logger.InfoAsync(
                    $"AutoPower가 파이프에 연결되었습니다 (PID {registration.ProcessId}).").ConfigureAwait(true);
                _popupWindow.SetRunningState(true);
                var mode = ConfiguredTrayMode;
                var sent = await _hostServer
                    .SendTrayModeAsync(mode, _lifetimeCancellation.Token)
                    .ConfigureAwait(true);
                await _logger.InfoAsync(
                    sent
                        ? $"저장된 트레이 모드를 적용했습니다: {TrayPipeProtocol.FormatTrayMode(mode)}"
                        : "저장된 트레이 모드를 보내기 전에 연결이 끊어졌습니다.").ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await _logger.ErrorAsync("파이프 등록 처리 중 오류가 발생했습니다.", exception).ConfigureAwait(true);
            }
        });

    private void OnHostClientDisconnected(object? sender, EventArgs e) =>
        _ = _dispatcher.InvokeAsync(async () =>
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                await _logger.InfoAsync("AutoPower 파이프 연결이 끊어졌습니다.").ConfigureAwait(true);
                await RefreshStatusSafelyAsync().ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await _logger.ErrorAsync("파이프 연결 해제 처리 중 오류가 발생했습니다.", exception).ConfigureAwait(true);
            }
        });

    private void OnHostServerFaulted(object? sender, Exception exception) =>
        _ = _dispatcher.InvokeAsync(async () =>
        {
            if (!_disposed)
            {
                await _logger.ErrorAsync("트레이 호스트 파이프에서 오류가 발생했습니다.", exception).ConfigureAwait(true);
            }
        });

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
