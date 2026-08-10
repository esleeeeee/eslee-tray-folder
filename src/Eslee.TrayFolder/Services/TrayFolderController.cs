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
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    private readonly TrayFolderConfig _config;
    private readonly ConfigService _configService;
    private readonly AppDiscoveryService _discoveryService;
    private readonly ExecutablePathValidator _pathValidator;
    private readonly AutoPowerProcessService _processService;
    private readonly TrayHostServer _hostServer;
    private readonly UpdateCheckService _updateService;
    private readonly IAppLogger _logger;
    private UpdateCheckResult? _updateResult;
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
        AppDiscoveryService discoveryService,
        ExecutablePathValidator pathValidator,
        AutoPowerProcessService processService,
        TrayHostServer hostServer,
        UpdateCheckService updateService,
        IAppLogger logger)
    {
        _config = config;
        _configService = configService;
        _discoveryService = discoveryService;
        _pathValidator = pathValidator;
        _processService = processService;
        _hostServer = hostServer;
        _updateService = updateService;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        EnsureDefaultEntries();
        _popupWindow.SettingsRequested += OnSettingsRequested;
        _popupWindow.AppRequested += OnAppRequested;
        _popupWindow.AppMenuRequested += OnAppMenuRequested;
        _popupWindow.AppMenuActionRequested += OnAppMenuActionRequested;
        _settingsWindow.SaveAllRequested += OnSettingsSaveAllRequested;
        _settingsWindow.DiscoveryRequested += OnSettingsDiscoveryRequested;
        _settingsWindow.UpdateCheckRequested += OnUpdateCheckRequested;
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

        var versionText = UpdateCheckService.FormatVersion(CurrentVersion);
        _popupWindow.SetVersion(versionText);
        _settingsWindow.SetVersionText(versionText);

        _popupWindow.SetApps(EnabledApps.Select(app => (app.AppId, app.DisplayName)).ToList());
        await EnsureAppPathsAsync(cancellationToken).ConfigureAwait(true);
        foreach (var app in EnabledApps)
        {
            UpdateAppPresentation(app);
        }

        _hostServer.Start();
        await RefreshStatusAsync(cancellationToken).ConfigureAwait(true);
        _ = RunStartupUpdateCheckAsync();
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
        _popupWindow.AppRequested -= OnAppRequested;
        _popupWindow.AppMenuRequested -= OnAppMenuRequested;
        _popupWindow.AppMenuActionRequested -= OnAppMenuActionRequested;
        _settingsWindow.SaveAllRequested -= OnSettingsSaveAllRequested;
        _settingsWindow.DiscoveryRequested -= OnSettingsDiscoveryRequested;
        _settingsWindow.UpdateCheckRequested -= OnUpdateCheckRequested;
        _hostServer.ClientRegistered -= OnHostClientRegistered;
        _hostServer.ClientDisconnected -= OnHostClientDisconnected;
        _hostServer.Faulted -= OnHostServerFaulted;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _popupWindow.ClosePermanently();
        _settingsWindow.ClosePermanently();
        _lifetimeCancellation.Dispose();
    }

    private IEnumerable<TrayAppConfig> EnabledApps =>
        _config.Apps.Where(app => app.Enabled && !string.IsNullOrWhiteSpace(app.AppId))
            .OrderBy(app => app.Order);

    private TrayAppConfig? FindApp(string appId) => _config.Apps.FirstOrDefault(
        app => string.Equals(app.AppId, appId, StringComparison.OrdinalIgnoreCase));

    private static bool IsAutoPower(TrayAppConfig app) =>
        string.Equals(app.AppId, TrayPipeProtocol.AutoPowerAppId, StringComparison.OrdinalIgnoreCase);

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
        foreach (var app in EnabledApps)
        {
            UpdateAppPresentation(app);
            _popupWindow.SetRunningState(app.AppId, null);
        }

        _popupWindow.ShowAt(TrayIconController.GetCursorPosition());
        _statusTimer.Start();
        _ = RefreshStatusSafelyAsync();
    }

    private void ShowSettings()
    {
        ThrowIfDisposed();
        _popupWindow.Hide();
        _statusTimer.Stop();
        RefreshSettingsEntries();
        ApplyUpdateStatusToSettings();
        _settingsWindow.ShowMessage(string.Empty, isError: false);
        _settingsWindow.ShowAndActivate();
    }

    private static Version CurrentVersion =>
        typeof(TrayFolderController).Assembly.GetName().Version ?? new Version(0, 0, 0);

    private bool IsUpdateCheckDue()
    {
        if (!DateTimeOffset.TryParse(
                _config.LastUpdateCheckUtc,
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var lastCheck))
        {
            return true;
        }

        return DateTimeOffset.UtcNow - lastCheck >= UpdateCheckInterval;
    }

    private async Task RunStartupUpdateCheckAsync()
    {
        try
        {
            if (!IsUpdateCheckDue())
            {
                return;
            }

            await CheckForUpdatesAsync(showProgress: false).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("업데이트 확인 중 오류가 발생했습니다.", exception);
        }
    }

    private async void OnUpdateCheckRequested(object? sender, EventArgs e)
    {
        try
        {
            await CheckForUpdatesAsync(showProgress: true).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync("업데이트 확인 중 오류가 발생했습니다.", exception);
            _settingsWindow.SetUpdateStatus("업데이트 확인에 실패했습니다. 로그를 확인해 주세요.", null);
        }
    }

    private async Task CheckForUpdatesAsync(bool showProgress)
    {
        if (showProgress)
        {
            _settingsWindow.SetUpdateStatus("최신 버전을 확인하는 중…", null, checkInProgress: true);
        }

        var result = await _updateService
            .CheckAsync(CurrentVersion, _lifetimeCancellation.Token)
            .ConfigureAwait(true);
        if (_disposed)
        {
            return;
        }

        _updateResult = result;
        if (result.Status != UpdateStatus.Failed)
        {
            _config.LastUpdateCheckUtc = DateTimeOffset.UtcNow.ToString("O");
            await _configService.SaveAsync(_config, _lifetimeCancellation.Token).ConfigureAwait(true);
        }

        ApplyUpdateStatusToSettings(showFailure: showProgress);
        await _logger.InfoAsync(
            $"업데이트 확인 결과: {result.Status} (최신 릴리스 {result.LatestVersion ?? "알 수 없음"})");
    }

    private void ApplyUpdateStatusToSettings(bool showFailure = false)
    {
        switch (_updateResult?.Status)
        {
            case UpdateStatus.UpdateAvailable:
                _settingsWindow.SetUpdateStatus(
                    $"새 버전 {_updateResult.LatestVersion}이(가) 있습니다. Release 페이지에서 설치 파일을 받아 업데이트하세요.",
                    _updateResult.ReleaseUrl ?? UpdateCheckService.ReleasesPageUrl);
                break;
            case UpdateStatus.UpToDate:
                _settingsWindow.SetUpdateStatus("최신 버전입니다.", null);
                break;
            case UpdateStatus.Failed:
                _settingsWindow.SetUpdateStatus(
                    showFailure ? "업데이트 확인에 실패했습니다. 네트워크 연결을 확인해 주세요." : string.Empty,
                    null);
                break;
            default:
                _settingsWindow.SetUpdateStatus(string.Empty, null);
                break;
        }
    }

    private void RefreshSettingsEntries()
    {
        _settingsWindow.SetApps(
            EnabledApps
                .Select(app => new SettingsAppEntry(
                    app.AppId,
                    app.DisplayName,
                    app.ExecutablePath,
                    app.TrayMode,
                    SupportsDiscovery: TrayAppCatalog.FindSpec(app.AppId) is not null))
                .ToList());
    }

    /// <summary>
    /// 경로가 비어 있거나 더 이상 존재하지 않는 앱만 자동 탐색해 저장합니다.
    /// 사용자가 지정한 유효한 경로는 덮어쓰지 않습니다.
    /// </summary>
    private async Task EnsureAppPathsAsync(CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var app in EnabledApps.ToList())
        {
            if (!string.IsNullOrWhiteSpace(app.ExecutablePath) && File.Exists(app.ExecutablePath))
            {
                continue;
            }

            var spec = TrayAppCatalog.FindSpec(app.AppId);
            if (spec is null)
            {
                continue;
            }

            try
            {
                var discovered = await _discoveryService.DiscoverAsync(spec, cancellationToken).ConfigureAwait(true);
                if (discovered.ExecutablePath is null)
                {
                    await _logger.InfoAsync(discovered.Detail ?? $"{app.DisplayName} 자동 탐색 결과가 없습니다.");
                    continue;
                }

                app.ExecutablePath = discovered.ExecutablePath;
                changed = true;
                await _logger.InfoAsync(
                    $"{app.DisplayName} 경로를 자동 탐색하고 저장했습니다 ({discovered.Source}): {discovered.ExecutablePath}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                await _logger.ErrorAsync($"{app.DisplayName} 자동 탐색 중 오류가 발생했습니다.", exception);
            }
        }

        if (changed)
        {
            await _configService.SaveAsync(_config, cancellationToken).ConfigureAwait(true);
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
            foreach (var app in EnabledApps.ToList())
            {
                bool isRunning;
                if (_hostServer.IsClientConnected(app.AppId))
                {
                    // 파이프가 연결돼 있으면 프로세스 스캔보다 연결 상태를 우선합니다.
                    isRunning = true;
                }
                else if (IsAutoPower(app))
                {
                    // 제품 정보 기반 검사라 설치 위치가 설정 경로와 달라도 잡습니다.
                    var status = await _processService.GetStatusAsync(null, cancellationToken).ConfigureAwait(true);
                    isRunning = status.IsRunning;
                }
                else
                {
                    isRunning = false;
                    if (!string.IsNullOrWhiteSpace(app.ExecutablePath))
                    {
                        var status = await _processService
                            .GetStatusByPathAsync(app.ExecutablePath, cancellationToken)
                            .ConfigureAwait(true);
                        isRunning = status.IsRunning;
                    }

                    // 설정 경로와 다른 위치(설치본/개발 빌드)에서 실행 중인 경우를 위해
                    // 실행 파일 이름으로도 확인합니다.
                    if (!isRunning && TrayAppCatalog.FindSpec(app.AppId) is { } spec)
                    {
                        var status = await _processService
                            .GetStatusByExecutableNamesAsync(spec.ExecutableFileNames, cancellationToken)
                            .ConfigureAwait(true);
                        isRunning = status.IsRunning;
                    }
                }

                if (_disposed)
                {
                    return;
                }

                _popupWindow.SetRunningState(app.AppId, isRunning);
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
            await _logger.ErrorAsync("앱 실행 상태를 확인하지 못했습니다.", exception);
        }
    }

    private async void OnAppRequested(object? sender, string appId)
    {
        var app = FindApp(appId);
        if (app is null)
        {
            return;
        }

        _popupWindow.SetBusy(appId, true);
        try
        {
            if (_hostServer.IsClientConnected(appId))
            {
                // 연결된 앱은 파이프 Activate만 사용합니다. 창 복원 폴백은 파이프가
                // 정말 없는 경우(연동 전 버전, 미실행)를 위한 것입니다.
                var commandResult = await _hostServer
                    .SendCommandAsync(appId, TrayHostCommand.Activate, PipeCommandTimeout, _lifetimeCancellation.Token)
                    .ConfigureAwait(true);
                if (!commandResult.Succeeded)
                {
                    await _logger.InfoAsync(
                        $"파이프 Activate에 실패했습니다 ({appId}): {commandResult.ErrorMessage}");
                    if (_hostServer.IsClientConnected(appId))
                    {
                        MessageBox.Show(
                            commandResult.ErrorMessage ?? $"{app.DisplayName} 창을 여는 데 실패했습니다.",
                            "Tray Folder",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                }

                await Task.Delay(350, _lifetimeCancellation.Token).ConfigureAwait(true);
                await RefreshStatusSafelyAsync().ConfigureAwait(true);
                return;
            }

            var result = IsAutoPower(app)
                ? await _processService
                    .ActivateOrLaunchAsync(app.ExecutablePath, _lifetimeCancellation.Token)
                    .ConfigureAwait(true)
                : TrayAppCatalog.FindSpec(app.AppId) is { } spec
                    ? await _processService
                        .ActivateOrLaunchByNamesAsync(
                            spec.ExecutableFileNames, app.ExecutablePath, app.DisplayName, _lifetimeCancellation.Token)
                        .ConfigureAwait(true)
                    : await _processService
                        .ActivateOrLaunchByPathAsync(app.ExecutablePath, app.DisplayName, _lifetimeCancellation.Token)
                        .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                if (result.Exception is not null)
                {
                    await _logger.ErrorAsync($"{app.DisplayName} 실행 또는 창 복원에 실패했습니다.", result.Exception);
                }
                else
                {
                    await _logger.InfoAsync(result.UserMessage ?? $"{app.DisplayName} 작업에 실패했습니다.");
                }

                MessageBox.Show(
                    _popupWindow,
                    result.UserMessage ?? $"{app.DisplayName} 작업을 완료하지 못했습니다.",
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
            await _logger.ErrorAsync($"{app.DisplayName} 요청 처리 중 예상하지 못한 오류가 발생했습니다.", exception);
            MessageBox.Show(
                $"{app.DisplayName} 요청을 처리하지 못했습니다. 자세한 내용은 로그를 확인해 주세요.",
                "Tray Folder",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (!_disposed)
            {
                _popupWindow.SetBusy(appId, false);
            }
        }
    }

    private async void OnAppMenuRequested(object? sender, string appId)
    {
        var app = FindApp(appId);
        if (app is null)
        {
            return;
        }

        // 응답을 기다리는 사이 팝업이 닫혔다 다시 열릴 수 있으므로,
        // 우클릭 시점의 표시 회차를 캡처해 그 회차에서만 메뉴를 엽니다.
        var session = _popupWindow.VisibleSession;
        try
        {
            if (!_hostServer.IsClientConnected(appId))
            {
                _popupWindow.ShowAppMenu(
                    appId,
                    [TrayMenuItem.Action("not-connected", $"{app.DisplayName}이(가) 연결되지 않았습니다", enabled: false)],
                    session);
                return;
            }

            var items = await _hostServer
                .GetMenuAsync(appId, PipeCommandTimeout, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (items is null || items.Count == 0)
            {
                await _logger.InfoAsync($"{app.DisplayName} 메뉴를 가져오지 못했습니다 (응답 없음 또는 빈 메뉴).").ConfigureAwait(true);
                _popupWindow.ShowAppMenu(
                    appId,
                    [TrayMenuItem.Action("menu-unavailable", "메뉴를 가져오지 못했습니다", enabled: false)],
                    session);
                return;
            }

            _popupWindow.ShowAppMenu(appId, items, session);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync($"{app.DisplayName} 메뉴 요청 처리 중 오류가 발생했습니다.", exception).ConfigureAwait(true);
        }
    }

    private async void OnAppMenuActionRequested(object? sender, AppMenuActionRequest request)
    {
        try
        {
            var result = await _hostServer
                .SendMenuActionAsync(request.AppId, request.ActionId, PipeCommandTimeout, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (!result.Succeeded)
            {
                await _logger.InfoAsync(
                    $"메뉴 명령을 실행하지 못했습니다 ({request.AppId}/{request.ActionId}): {result.ErrorMessage}").ConfigureAwait(true);

                // 연결이 끊어진 경우는 알리지 않습니다: '앱 종료' 명령의 정상 결과이거나,
                // 앱 종료로 타일 상태가 이미 '실행 안 됨'으로 바뀌는 상황입니다.
                if (_hostServer.IsClientConnected(request.AppId))
                {
                    MessageBox.Show(
                        result.ErrorMessage ?? "메뉴 명령을 실행하지 못했습니다.",
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
            await _logger.ErrorAsync(
                $"메뉴 명령 처리 중 오류가 발생했습니다 ({request.AppId}/{request.ActionId}).", exception).ConfigureAwait(true);
        }
    }

    private async void OnSettingsSaveAllRequested(object? sender, IReadOnlyList<SettingsSaveRequest> requests)
    {
        // 1단계: 전체 검증. 하나라도 잘못되면 아무것도 저장하지 않습니다.
        var validated = new List<(TrayAppConfig App, string Path, TrayMode Mode)>();
        foreach (var request in requests)
        {
            var app = FindApp(request.AppId);
            if (app is null)
            {
                continue;
            }

            string normalizedPath;
            if (IsAutoPower(app))
            {
                if (string.IsNullOrWhiteSpace(request.ExecutablePath))
                {
                    normalizedPath = string.Empty;
                }
                else
                {
                    var validation = _pathValidator.ValidateAutoPower(request.ExecutablePath);
                    if (!validation.IsValid || validation.NormalizedPath is null)
                    {
                        _settingsWindow.ShowMessage(
                            $"{app.DisplayName}: {validation.UserMessage ?? "올바른 실행 파일을 지정해 주세요."}",
                            isError: true);
                        return;
                    }

                    normalizedPath = validation.NormalizedPath;
                }
            }
            else if (string.IsNullOrWhiteSpace(request.ExecutablePath))
            {
                // 경로 없이도 파이프 연동은 동작하므로 빈 경로를 허용합니다.
                normalizedPath = string.Empty;
            }
            else
            {
                var validation = _pathValidator.ValidateGeneric(request.ExecutablePath, app.DisplayName);
                if (!validation.IsValid || validation.NormalizedPath is null)
                {
                    _settingsWindow.ShowMessage(
                        $"{app.DisplayName}: {validation.UserMessage ?? "올바른 실행 파일을 지정해 주세요."}",
                        isError: true);
                    return;
                }

                normalizedPath = validation.NormalizedPath;
            }

            var mode = TrayPipeProtocol.TryParseTrayMode(request.TrayMode, out var parsedMode)
                ? parsedMode
                : TrayMode.Standalone;
            validated.Add((app, normalizedPath, mode));
        }

        // 2단계: 일괄 적용 후 한 번에 저장하고, 연결된 앱에는 모드를 즉시 적용합니다.
        try
        {
            foreach (var (app, path, mode) in validated)
            {
                app.ExecutablePath = path;
                app.TrayMode = TrayPipeProtocol.FormatTrayMode(mode);
            }

            await _configService.SaveAsync(_config, _lifetimeCancellation.Token).ConfigureAwait(true);

            var appliedCount = 0;
            var failedNames = new List<string>();
            foreach (var (app, _, mode) in validated)
            {
                UpdateAppPresentation(app);
                if (!_hostServer.IsClientConnected(app.AppId))
                {
                    continue;
                }

                var sent = await _hostServer
                    .SendTrayModeAsync(app.AppId, mode, _lifetimeCancellation.Token)
                    .ConfigureAwait(true);
                if (sent)
                {
                    appliedCount++;
                }
                else
                {
                    failedNames.Add(app.DisplayName);
                }
            }

            RefreshSettingsEntries();
            _settingsWindow.ShowMessage(
                failedNames.Count > 0
                    ? $"모든 앱 설정을 저장했지만 일부 앱에 적용하지 못했습니다: {string.Join(", ", failedNames)}"
                    : appliedCount > 0
                        ? $"모든 앱 설정을 저장하고 연결된 앱 {appliedCount}개에 즉시 적용했습니다."
                        : "모든 앱 설정을 저장했습니다. 각 앱이 연결되면 트레이 모드가 적용됩니다.",
                isError: failedNames.Count > 0);
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

    private async void OnSettingsDiscoveryRequested(object? sender, string appId)
    {
        var app = FindApp(appId);
        var spec = app is null ? null : TrayAppCatalog.FindSpec(app.AppId);
        if (app is null || spec is null)
        {
            return;
        }

        _settingsWindow.ShowMessage($"{app.DisplayName}을(를) 찾는 중입니다…", isError: false);
        try
        {
            var result = await _discoveryService
                .DiscoverAsync(spec, _lifetimeCancellation.Token)
                .ConfigureAwait(true);
            if (result.ExecutablePath is null)
            {
                _settingsWindow.ShowMessage(
                    result.Detail ?? $"{app.DisplayName}을(를) 자동으로 찾지 못했습니다.", isError: true);
                return;
            }

            // 다른 앱의 편집 중인 값이 날아가지 않도록 해당 앱의 입력값만 채우고,
            // 실제 저장은 저장 버튼에서 한 번에 합니다.
            _settingsWindow.SetAppPath(app.AppId, result.ExecutablePath);
            _settingsWindow.ShowMessage(
                $"{app.DisplayName}을(를) 찾았습니다 ({result.Source}). 저장 버튼을 눌러 적용해 주세요.",
                isError: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await _logger.ErrorAsync($"설정 화면의 {app.DisplayName} 자동 탐색에 실패했습니다.", exception);
            _settingsWindow.ShowMessage($"{app.DisplayName} 자동 탐색에 실패했습니다. 로그를 확인해 주세요.", isError: true);
        }
    }

    private void OnSettingsRequested(object? sender, EventArgs e) => ShowSettings();

    private TrayMode ConfiguredTrayMode(TrayAppConfig app) =>
        TrayPipeProtocol.TryParseTrayMode(app.TrayMode, out var mode) ? mode : TrayMode.Standalone;

    private void OnHostClientRegistered(object? sender, TrayAppRegistration registration) =>
        _ = _dispatcher.InvokeAsync(async () =>
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var app = FindApp(registration.AppId);
                if (app is null || !app.Enabled)
                {
                    await _logger.InfoAsync(
                        $"알 수 없는 앱의 파이프 등록을 무시했습니다: {registration.AppId}").ConfigureAwait(true);
                    return;
                }

                await _logger.InfoAsync(
                    $"{app.DisplayName}이(가) 파이프에 연결되었습니다 (PID {registration.ProcessId}).").ConfigureAwait(true);
                _popupWindow.SetRunningState(app.AppId, true);

                // 설정 경로가 비어 있거나 파일이 사라진 경우에만 연결된 프로세스의
                // 실제 exe 경로로 채웁니다. 정식 설치 경로처럼 사용자가 쓰는 유효한
                // 경로를 dev 빌드 연결이 덮어쓰지 않게 합니다. 잘못된 사본 재실행은
                // 실행 폴백의 중복 실행 방지 가드가 막습니다.
                var hasValidConfiguredPath =
                    !string.IsNullOrWhiteSpace(app.ExecutablePath) && File.Exists(app.ExecutablePath);
                if (!hasValidConfiguredPath)
                {
                    var connectedPath = AutoPowerProcessService.TryGetProcessPath(registration.ProcessId);
                    if (!string.IsNullOrWhiteSpace(connectedPath) &&
                        File.Exists(connectedPath) &&
                        !string.Equals(connectedPath, app.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        app.ExecutablePath = connectedPath;
                        await _configService.SaveAsync(_config, _lifetimeCancellation.Token).ConfigureAwait(true);
                        UpdateAppPresentation(app);
                        await _logger.InfoAsync(
                            $"{app.DisplayName} 실행 파일 경로를 연결된 프로세스 경로로 채웠습니다: {connectedPath}").ConfigureAwait(true);
                    }
                }
                var mode = ConfiguredTrayMode(app);
                var sent = await _hostServer
                    .SendTrayModeAsync(app.AppId, mode, _lifetimeCancellation.Token)
                    .ConfigureAwait(true);
                await _logger.InfoAsync(
                    sent
                        ? $"저장된 트레이 모드를 적용했습니다 ({app.AppId}): {TrayPipeProtocol.FormatTrayMode(mode)}"
                        : $"저장된 트레이 모드를 보내기 전에 연결이 끊어졌습니다 ({app.AppId}).").ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await _logger.ErrorAsync("파이프 등록 처리 중 오류가 발생했습니다.", exception).ConfigureAwait(true);
            }
        });

    private void OnHostClientDisconnected(object? sender, string appId) =>
        _ = _dispatcher.InvokeAsync(async () =>
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                await _logger.InfoAsync($"파이프 연결이 끊어졌습니다: {appId}").ConfigureAwait(true);
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

    private void UpdateAppPresentation(TrayAppConfig app) =>
        _popupWindow.SetApp(app.AppId, app.DisplayName, AppIconProvider.TryLoad(app.ExecutablePath));

    private void EnsureDefaultEntries()
    {
        foreach (var defaultApp in TrayFolderConfig.CreateDefault().Apps)
        {
            if (FindApp(defaultApp.AppId) is null)
            {
                _config.Apps.Add(defaultApp);
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
