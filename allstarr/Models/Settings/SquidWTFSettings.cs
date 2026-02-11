namespace allstarr.Models.Settings;

/// <summary>
/// Configuration for the SquidWTF downloader and metadata service
/// </summary>
public class SquidWTFSettings
{
    /// <summary>
    /// Preferred audio quality:
    /// - HI_RES or HI_RES_LOSSLESS: 24-bit/192kHz FLAC (highest quality)
    /// - FLAC or LOSSLESS: 16-bit/44.1kHz FLAC (CD quality, default)
    /// - HIGH: 320kbps AAC (high quality, smaller files)
    /// - LOW: 96kbps AAC (low quality, smallest files)
    /// If not specified or unavailable, LOSSLESS will be used.
    /// </summary>
    public string? Quality { get; set; }	
}
