namespace octo_fiesta.Models.Settings;

/// <summary>
/// Configuration for the Deezer downloader and metadata service
/// </summary>
public class DeezerSettings
{
    /// <summary>
    /// Deezer ARL token (required for downloading)
    /// Obtained from browser cookies after logging into deezer.com
    /// </summary>
    public string? Arl { get; set; }
    
    /// <summary>
    /// Fallback ARL token (optional)
    /// Used if the primary ARL token fails
    /// </summary>
    public string? ArlFallback { get; set; }
    
    /// <summary>
    /// Preferred audio quality: FLAC, MP3_320, MP3_128
    /// If not specified or unavailable, the highest available quality will be used.
    /// </summary>
    public string? Quality { get; set; }
}
