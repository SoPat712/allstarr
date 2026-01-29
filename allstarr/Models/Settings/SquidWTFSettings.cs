namespace allstarr.Models.Settings;

/// <summary>
/// Configuration for the SquidWTF downloader and metadata service
/// </summary>
public class SquidWTFSettings
{
    /// <summary>
	/// No user auth should be needed for this site.
	/// </summary>
    
    /// <summary>
    /// Preferred audio quality: FLAC, MP3_320, MP3_128
    /// If not specified or unavailable, the highest available quality will be used.
    /// </summary>
    public string? Quality { get; set; }	
}
