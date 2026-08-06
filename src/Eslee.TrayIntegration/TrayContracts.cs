namespace Eslee.TrayIntegration;

/// <summary>앱이 자체 트레이 아이콘을 관리하는 방식을 나타냅니다.</summary>
public enum TrayMode
{
    Standalone,
    Hosted,
}

/// <summary>향후 Named Pipe 연결 시 호스트에 등록할 앱 정보입니다.</summary>
public sealed record TrayAppRegistration(
    int ProtocolVersion,
    string AppId,
    string DisplayName,
    TrayMode RequestedMode,
    int ProcessId);

/// <summary>연동 앱이 호스트에 보고할 최소 실행 상태입니다.</summary>
public sealed record TrayAppState(bool IsRunning, bool CanActivate, string? StatusText = null);

/// <summary>호스트가 연동 앱에 내릴 수 있는 최소 명령입니다.</summary>
public enum TrayHostCommand
{
    Activate,
    ShowSettings,
    Shutdown,
}

public sealed record TrayCommandResult(bool Succeeded, string? ErrorMessage = null);

/// <summary>
/// 향후 Named Pipe 전송 계층이 구현할 계약입니다. 이 단계에는 서버, 클라이언트,
/// 파이프 이름 또는 직렬화 구현을 포함하지 않습니다.
/// </summary>
public interface ITrayIntegrationClient
{
    Task<TrayAppState> GetStateAsync(CancellationToken cancellationToken);

    Task<TrayCommandResult> SendCommandAsync(
        TrayHostCommand command,
        CancellationToken cancellationToken);

    Task<TrayCommandResult> SetTrayModeAsync(
        TrayMode mode,
        CancellationToken cancellationToken);
}
