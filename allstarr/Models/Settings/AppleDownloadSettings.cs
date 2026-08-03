namespace allstarr.Models.Settings;

/// <summary>
/// Configuration for the optional external Apple download service.
/// </summary>
public class AppleDownloadSettings
{
    /// <summary>
    /// Base URL of an operator-managed GAMDL-compatible service. Empty means the
    /// provider is not configured; Allstarr never assumes a bundled hostname.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Preferred quality tier:
    /// - alac-16-44: 16-bit/44.1kHz ALAC (standard CD Lossless)
    /// - alac-24-96: 24-bit/96kHz ALAC (Hi-Res Lossless)
    /// - alac-24-192: 24-bit/192kHz ALAC (Highest Hi-Res Lossless, default)
    /// - aac-320: 320kbps AAC (standard lossy)
    /// - aac-96: 96kbps AAC (low quality lossy)
    /// </summary>
    public string? Quality { get; set; } = "alac-24-192";

    /// <summary>
    /// Host-mounted staging directory used by the administrator WebUI for an
    /// Apple Music APK/APKM upload. The host helper consumes the staged file.
    /// </summary>
    public string SetupUploadDirectory { get; set; } = "/app/apple-upload";
}
