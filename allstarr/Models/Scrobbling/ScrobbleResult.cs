namespace allstarr.Models.Scrobbling;

/// <summary>
/// Result of a scrobble or now playing request.
/// </summary>
public record ScrobbleResult
{
    /// <summary>
    /// Whether the request was successful.
    /// </summary>
    public bool Success { get; init; }
    
    /// <summary>
    /// Error message if the request failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
    
    /// <summary>
    /// Error code from the service (e.g., Last.fm error code).
    /// </summary>
    public int? ErrorCode { get; init; }
    
    /// <summary>
    /// Whether the scrobble was ignored by the service (filtered).
    /// </summary>
    public bool Ignored { get; init; }
    
    /// <summary>
    /// Reason the scrobble was ignored (if applicable).
    /// </summary>
    public string? IgnoredReason { get; init; }
    
    /// <summary>
    /// Ignored message code (e.g., Last.fm ignored code).
    /// </summary>
    public int? IgnoredCode { get; init; }
    
    /// <summary>
    /// Whether the artist was corrected by the service.
    /// </summary>
    public bool ArtistCorrected { get; init; }
    
    /// <summary>
    /// Corrected artist name (if applicable).
    /// </summary>
    public string? CorrectedArtist { get; init; }
    
    /// <summary>
    /// Whether the track was corrected by the service.
    /// </summary>
    public bool TrackCorrected { get; init; }
    
    /// <summary>
    /// Corrected track name (if applicable).
    /// </summary>
    public string? CorrectedTrack { get; init; }
    
    /// <summary>
    /// Whether the album was corrected by the service.
    /// </summary>
    public bool AlbumCorrected { get; init; }
    
    /// <summary>
    /// Corrected album name (if applicable).
    /// </summary>
    public string? CorrectedAlbum { get; init; }
    
    /// <summary>
    /// Whether the request should be retried (e.g., service offline, temporary error).
    /// </summary>
    public bool ShouldRetry { get; init; }
    
    public static ScrobbleResult CreateSuccess() => new() { Success = true };
    
    public static ScrobbleResult CreateError(string message, int? errorCode = null, bool shouldRetry = false) => new()
    {
        Success = false,
        ErrorMessage = message,
        ErrorCode = errorCode,
        ShouldRetry = shouldRetry
    };
    
    public static ScrobbleResult CreateIgnored(string reason, int ignoredCode) => new()
    {
        Success = true,
        Ignored = true,
        IgnoredReason = reason,
        IgnoredCode = ignoredCode
    };
}
