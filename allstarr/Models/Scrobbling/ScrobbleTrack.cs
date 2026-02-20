namespace allstarr.Models.Scrobbling;

/// <summary>
/// Represents a track to be scrobbled.
/// </summary>
public record ScrobbleTrack
{
    /// <summary>
    /// Track title (required).
    /// </summary>
    public required string Title { get; init; }
    
    /// <summary>
    /// Artist name (required).
    /// </summary>
    public required string Artist { get; init; }
    
    /// <summary>
    /// Album name (optional).
    /// </summary>
    public string? Album { get; init; }
    
    /// <summary>
    /// Album artist (optional).
    /// </summary>
    public string? AlbumArtist { get; init; }
    
    /// <summary>
    /// Track duration in seconds (optional but recommended).
    /// </summary>
    public int? DurationSeconds { get; init; }
    
    /// <summary>
    /// MusicBrainz Track ID (optional).
    /// </summary>
    public string? MusicBrainzId { get; init; }
    
    /// <summary>
    /// Unix timestamp when the track started playing (required for scrobbles).
    /// </summary>
    public long? Timestamp { get; init; }
    
    /// <summary>
    /// Whether the track was chosen by the user (true) or by an algorithm/radio (false).
    /// Default is true. Set to false for Last.fm radio, recommendation services, etc.
    /// </summary>
    public bool ChosenByUser { get; init; } = true;
    
    /// <summary>
    /// Whether the track is from an external source (Spotify, Deezer, etc.) or local library.
    /// Default is false (local library). Set to true for external tracks.
    /// ListenBrainz only scrobbles external tracks.
    /// </summary>
    public bool IsExternal { get; init; } = false;
}
