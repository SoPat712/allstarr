namespace allstarr.Models.Settings;

public sealed class JellyfinSettings : MediaBackendSettings
{
    public string? Url { get; set; }
    public string? ApiKey { get; set; }
    public string? UserId { get; set; }
    public string? ClientUsername { get; set; }
    public string? LibraryId { get; set; }
    public string ClientName { get; set; } = "Allstarr";
    public string ClientVersion { get; set; } = AppVersion.Version;
    public string DeviceId { get; set; } = "allstarrrr-proxy";
    public string DeviceName { get; set; } = "Allstarr Proxy";
}
