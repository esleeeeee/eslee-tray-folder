namespace Eslee.TrayIntegration;

/// <summary>앱이 자체 트레이 아이콘을 관리하는 방식을 나타냅니다.</summary>
public enum TrayMode
{
    Standalone,
    Hosted,
}

/// <summary>Named Pipe 연결 시 호스트에 등록되는 앱 정보입니다.</summary>
public sealed record TrayAppRegistration(
    int ProtocolVersion,
    string AppId,
    string DisplayName,
    TrayMode RequestedMode,
    int ProcessId);

/// <summary>연동 앱이 호스트에 보고할 최소 실행 상태입니다.</summary>
public sealed record TrayAppState(bool IsRunning, bool CanActivate, string? StatusText = null);

/// <summary>
/// 연동 앱이 제공하는 트레이 메뉴 항목입니다. 호스트는 이 목록을 그대로 렌더링하고,
/// 클릭 시 <see cref="Id"/>를 액션 명령으로 돌려보냅니다. 구분선은 Id와 Text가 없습니다.
/// </summary>
public sealed record TrayMenuItem(string? Id, string Text, bool Enabled, bool IsSeparator)
{
    public static TrayMenuItem Separator { get; } = new(null, string.Empty, false, true);

    public static TrayMenuItem Action(string id, string text, bool enabled = true) =>
        new(id, text, enabled, false);
}

/// <summary>호스트가 연동 앱에 내릴 수 있는 최소 명령입니다.</summary>
public enum TrayHostCommand
{
    Activate,
    ShowSettings,
    Shutdown,
}

public sealed record TrayCommandResult(bool Succeeded, string? ErrorMessage = null);

/// <summary>
/// 연동 앱 쪽 전송 계층이 구현할 계약입니다. 프로토콜 v1의 호스트 쪽 구현은
/// <see cref="TrayHostServer"/>, 규약은 <see cref="TrayPipeProtocol"/>을 참고하세요.
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
