namespace allstarr.Models.Settings;

public class AppleDownloadSettings
{
    // Empty keeps the optional operator-managed sidecar disabled.
    public string BaseUrl { get; set; } = string.Empty;
    public string? Quality { get; set; } = "alac-24-192";
    public string SetupUploadDirectory { get; set; } = "/app/apple-upload";
}
