using allstarr.Models.Scrobbling;

namespace allstarr.Services.Scrobbling;

/// <summary>
/// Interface for scrobbling services (Last.fm, ListenBrainz, etc.).
/// </summary>
public interface IScrobblingService
{
    /// <summary>
    /// Service name (e.g., "Last.fm", "ListenBrainz").
    /// </summary>
    string ServiceName { get; }

    /// <summary>
    /// Whether this service is enabled and configured.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Updates "Now Playing" status for a track.
    /// This is optional but recommended - shows what the user is currently listening to.
    /// </summary>
    /// <param name="track">Track being played</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the request</returns>
    Task<ScrobbleResult> UpdateNowPlayingAsync(ScrobbleTrack track, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scrobbles a track (adds to listening history).
    /// Should only be called when scrobble conditions are met (see Last.fm rules).
    /// </summary>
    /// <param name="track">Track to scrobble</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the request</returns>
    Task<ScrobbleResult> ScrobbleAsync(ScrobbleTrack track, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scrobbles multiple tracks in a batch (up to 50 for Last.fm).
    /// Useful for retrying cached scrobbles.
    /// </summary>
    /// <param name="tracks">Tracks to scrobble</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Results for each track</returns>
    Task<List<ScrobbleResult>> ScrobbleBatchAsync(List<ScrobbleTrack> tracks, CancellationToken cancellationToken = default);
}
