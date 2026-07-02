namespace allstarr.Models.Settings;

/// <summary>
/// Configuration for the Apple Music (gamdl-aio) service
/// </summary>
public class AppleMusicSettings
{
    /// <summary>
    /// Base URL of the gamdl-aio sidecar container
    /// </summary>
    public string BaseUrl { get; set; } = "http://gamdl-aio:8000";

    /// <summary>
    /// Preferred quality tier:
    /// - alac-16-44: 16-bit/44.1kHz ALAC (standard CD Lossless, default)
    /// - alac-24-96: 24-bit/96kHz ALAC (Hi-Res Lossless)
    /// - alac-24-192: 24-bit/192kHz ALAC (Highest Hi-Res Lossless)
    /// - aac-320: 320kbps AAC (Standard Lossy)
    /// - aac-96: 96kbps AAC (Low Quality Lossy)
    /// </summary>
    public string? Quality { get; set; } = "alac-16-44";
}
