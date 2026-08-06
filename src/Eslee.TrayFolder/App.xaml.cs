using System.Windows;
using System.Windows.Threading;
using Eslee.TrayFolder.Native;
using Eslee.TrayFolder.Services;

namespace Eslee.TrayFolder;

public partial class App : System.Windows.Application
{
    private const string ApplicationId = "eslee.trayfolder";
    private SingleInstanceManager? _singleInstance;
    private TrayFolderController? _controller;
    private ConfigService? _configService;
    private FileAppLogger? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new SingleInstanceManager(ApplicationId);
        if (_singleInstance.Role == SingleInstanceRole.Secondary)
        {
            _singleInstance.SignalPrimary();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        try
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            var paths = AppPaths.ForCurrentUser();
            _logger = new FileAppLogger(paths);
            await _logger.ApplyRetentionAsync().ConfigureAwait(true);
            _configService = new ConfigService(paths);
            var loadResult = await _configService.LoadOrCreateAsync().ConfigureAwait(true);
            if (loadResult.RecoveredBackupPath is not null)
            {
                await _logger.InfoAsync($"손상된 설정을 백업하고 기본 설정으로 복구했습니다: {loadResult.RecoveredBackupPath}");
                MessageBox.Show(
                    $"설정 파일이 손상되어 기본 설정으로 복구했습니다.\n백업: {loadResult.RecoveredBackupPath}",
                    "Tray Folder",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            var validator = new ExecutablePathValidator();
            _controller = new TrayFolderController(
                loadResult.Config,
                _configService,
                new AutoPowerDiscoveryService(validator),
                validator,
                new AutoPowerProcessService(validator, new WindowRestorer()),
                _logger);
            await _controller.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
            _singleInstance.Listen(() => Dispatcher.BeginInvoke(_controller.ActivateFromSecondInstance));
            await _logger.InfoAsync("Tray Folder가 시작되었습니다.");
        }
        catch (Exception exception)
        {
            if (_logger is not null)
            {
                await _logger.ErrorAsync("Tray Folder 시작에 실패했습니다.", exception);
            }

            MessageBox.Show(
                "Tray Folder를 시작하지 못했습니다. 설정 및 로그 폴더에 접근할 수 있는지 확인해 주세요.",
                "Tray Folder",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        _controller?.Dispose();
        _controller = null;
        _singleInstance?.Dispose();
        _singleInstance = null;
        _configService?.Dispose();
        _configService = null;
        _logger?.Dispose();
        _logger = null;
        base.OnExit(e);
    }

    private async void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_logger is not null)
        {
            await _logger.ErrorAsync("UI 처리 중 예기치 않은 오류가 발생했습니다.", e.Exception);
        }

        MessageBox.Show(
            "요청을 처리하지 못했습니다. Tray Folder는 계속 실행되며, 자세한 내용은 로그에 기록했습니다.",
            "Tray Folder",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        if (_logger is not null)
        {
            _ = _logger.ErrorAsync("백그라운드 작업에서 예기치 않은 오류가 발생했습니다.", e.Exception);
        }
    }
}
