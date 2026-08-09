using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eslee.TrayIntegration;

/// <summary>
/// 프로토콜 버전 1의 Named Pipe JSON 통신 규약입니다. 메시지는 UTF-8로 한 줄에
/// 하나의 JSON 객체를 담아 주고받습니다. AutoPower 쪽 구현과 저장소를 공유하지
/// 않으므로 이 파일의 규약(파이프 이름, 필드 이름, 값)이 곧 계약입니다.
/// </summary>
public static class TrayPipeProtocol
{
    public const int Version = 1;
    public const string AutoPowerAppId = "eslee.autopower";

    public const string RegisterType = "register";
    public const string SetTrayModeType = "set-tray-mode";
    public const string CommandType = "command";
    public const string CommandResultType = "command-result";
    public const string GetMenuType = "get-menu";
    public const string MenuType = "menu";

    public const string ActivateCommandValue = "activate";
    public const string ShowSettingsCommandValue = "show-settings";
    public const string ShutdownCommandValue = "shutdown";
    public const string MenuActionCommandValue = "menu-action";

    public const string HostedModeValue = "hosted";
    public const string StandaloneModeValue = "standalone";

    private const string PipeNamePrefix = "eslee.trayfolder.tray-host.v1.";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string BuildDefaultPipeName() => BuildPipeName(Environment.UserName);

    /// <summary>사용자별로 격리된 파이프 이름을 만듭니다. 양쪽 앱이 동일한 규칙을 사용해야 합니다.</summary>
    public static string BuildPipeName(string userName)
    {
        ArgumentNullException.ThrowIfNull(userName);
        var builder = new StringBuilder(PipeNamePrefix, PipeNamePrefix.Length + userName.Length);
        foreach (var character in userName)
        {
            builder.Append(
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        }

        return builder.ToString();
    }

    public static string Serialize(TrayPipeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.Serialize(message, SerializerOptions);
    }

    public static TrayPipeMessage? TryDeserialize(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TrayPipeMessage>(line, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>register 메시지를 검증해 등록 정보로 변환합니다. 규약에 어긋나면 null입니다.</summary>
    public static TrayAppRegistration? TryCreateRegistration(TrayPipeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!string.Equals(message.Type, RegisterType, StringComparison.Ordinal) ||
            message.ProtocolVersion != Version ||
            string.IsNullOrWhiteSpace(message.AppId) ||
            message.ProcessId is not (> 0))
        {
            return null;
        }

        var mode = TryParseTrayMode(message.Mode, out var parsed) ? parsed : TrayMode.Standalone;
        return new TrayAppRegistration(
            Version,
            message.AppId,
            string.IsNullOrWhiteSpace(message.DisplayName) ? message.AppId : message.DisplayName,
            mode,
            message.ProcessId.Value);
    }

    public static bool TryParseTrayMode(string? value, out TrayMode mode)
    {
        if (string.Equals(value, HostedModeValue, StringComparison.OrdinalIgnoreCase))
        {
            mode = TrayMode.Hosted;
            return true;
        }

        if (string.Equals(value, StandaloneModeValue, StringComparison.OrdinalIgnoreCase))
        {
            mode = TrayMode.Standalone;
            return true;
        }

        mode = TrayMode.Standalone;
        return false;
    }

    public static string FormatTrayMode(TrayMode mode) =>
        mode == TrayMode.Hosted ? HostedModeValue : StandaloneModeValue;

    public static string FormatCommand(TrayHostCommand command) => command switch
    {
        TrayHostCommand.Activate => ActivateCommandValue,
        TrayHostCommand.ShowSettings => ShowSettingsCommandValue,
        TrayHostCommand.Shutdown => ShutdownCommandValue,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "지원하지 않는 명령입니다."),
    };

    /// <summary>menu 메시지의 items를 렌더링 가능한 목록으로 변환합니다. 규약에 어긋난 항목은 건너뜁니다.</summary>
    public static IReadOnlyList<TrayMenuItem> ToMenuItems(TrayPipeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Items is null)
        {
            return [];
        }

        var items = new List<TrayMenuItem>(message.Items.Count);
        foreach (var payload in message.Items)
        {
            if (payload is null)
            {
                continue;
            }

            if (payload.Separator == true)
            {
                items.Add(TrayMenuItem.Separator);
                continue;
            }

            if (string.IsNullOrWhiteSpace(payload.Text))
            {
                continue;
            }

            items.Add(new TrayMenuItem(
                string.IsNullOrWhiteSpace(payload.Id) ? null : payload.Id,
                payload.Text,
                payload.Enabled ?? true,
                IsSeparator: false));
        }

        return items;
    }

    /// <summary>메뉴 항목 목록을 wire 표현으로 변환합니다.</summary>
    public static List<TrayMenuItemPayload> ToMenuPayloads(IReadOnlyList<TrayMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var payloads = new List<TrayMenuItemPayload>(items.Count);
        foreach (var item in items)
        {
            payloads.Add(item.IsSeparator
                ? new TrayMenuItemPayload { Separator = true }
                : new TrayMenuItemPayload
                {
                    Id = item.Id,
                    Text = item.Text,
                    Enabled = item.Enabled ? null : false,
                });
        }

        return payloads;
    }
}

/// <summary>
/// 프로토콜 v1의 모든 메시지를 담는 공용 봉투입니다. 형식별로 사용하는 필드만 채우고
/// 나머지는 null로 두면 직렬화 시 생략됩니다. 알 수 없는 필드는 무시되므로 향후
/// 버전에서 필드를 추가해도 v1 상대와 호환됩니다.
/// </summary>
public sealed class TrayPipeMessage
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("protocolVersion")]
    public int? ProtocolVersion { get; set; }

    [JsonPropertyName("appId")]
    public string? AppId { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("processId")]
    public int? ProcessId { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("actionId")]
    public string? ActionId { get; set; }

    [JsonPropertyName("items")]
    public List<TrayMenuItemPayload>? Items { get; set; }

    [JsonPropertyName("succeeded")]
    public bool? Succeeded { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>menu 메시지의 항목 wire 표현입니다. enabled 생략은 true, separator 생략은 false로 해석합니다.</summary>
public sealed class TrayMenuItemPayload
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("separator")]
    public bool? Separator { get; set; }
}
