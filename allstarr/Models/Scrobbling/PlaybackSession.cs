namespace allstarr.Models.Scrobbling;

/// <summary>
/// Tracks playback state for scrobbling decisions.
/// </summary>
public class PlaybackSession
{
    private const int ExternalStartToleranceSeconds = 5;

    /// <summary>
    /// Unique identifier for this playback session.
    /// </summary>
    public required string SessionId { get; init; }
    
    /// <summary>
    /// Device ID of the client.
    /// </summary>
    public required string DeviceId { get; init; }
    
    /// <summary>
    /// Track being played.
    /// </summary>
    public required ScrobbleTrack Track { get; init; }
    
    /// <summary>
    /// When playback started (UTC).
    /// </summary>
    public DateTime StartTime { get; init; }
    
    /// <summary>
    /// Last reported playback position in seconds.
    /// </summary>
    public int LastPositionSeconds { get; set; }
    
    /// <summary>
    /// Whether "Now Playing" has been sent for this session.
    /// </summary>
    public bool NowPlayingSent { get; set; }
    
    /// <summary>
    /// Whether the track has been scrobbled.
    /// </summary>
    public bool Scrobbled { get; set; }
    
    /// <summary>
    /// Last activity timestamp (for cleanup).
    /// </summary>
    public DateTime LastActivity { get; set; }
    
    /// <summary>
    /// Checks if the track should be scrobbled based on Last.fm rules:
    /// - Track must be longer than 30 seconds
    /// - Track has been played for at least half its duration, or for 4 minutes (whichever occurs earlier)
    /// </summary>
    public bool ShouldScrobble()
    {
        if (Scrobbled)
            return false; // Already scrobbled

        if (Track.DurationSeconds == null || Track.DurationSeconds <= 30)
            return false; // Track too short or duration unknown

        // External scrobbles should only count if playback started near the beginning.
        // This avoids duplicate/resume scrobbles when users jump into a track mid-way.
        if (Track.IsExternal && (Track.StartPositionSeconds ?? 0) > ExternalStartToleranceSeconds)
            return false;

        var halfDuration = Track.DurationSeconds.Value / 2;
        var scrobbleThreshold = Math.Min(halfDuration, 240); // 4 minutes = 240 seconds

        return LastPositionSeconds >= scrobbleThreshold;
    }
}
