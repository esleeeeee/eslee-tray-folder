using System.Text.Json.Serialization;

namespace Eslee.TrayFolder.Models;

public sealed class TrayFolderConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("apps")]
    public List<TrayAppConfig> Apps { get; set; } = [];

    public static TrayFolderConfig CreateDefault() => new()
    {
        Apps =
        [
            new TrayAppConfig
            {
                AppId = "eslee.autopower",
                DisplayName = "AutoPower",
                ExecutablePath = string.Empty,
                TrayMode = "hosted",
                Order = 0,
                Enabled = true,
            },
        ],
    };
}

public sealed class TrayAppConfig
{
    [JsonPropertyName("appId")]
    public string AppId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("executablePath")]
    public string ExecutablePath { get; set; } = string.Empty;

    [JsonPropertyName("trayMode")]
    public string TrayMode { get; set; } = "standalone";

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}
