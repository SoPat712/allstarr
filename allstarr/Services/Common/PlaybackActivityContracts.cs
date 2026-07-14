namespace allstarr.Services.Common;

public sealed record PlaybackActivityState(
    string DeviceId,
    string ItemId,
    long PositionTicks,
    DateTime LastActivity);

public sealed record PlaybackTrackMetadata(
    string Title,
    string Artist,
    string? Album,
    string? CoverArtUrl);

public sealed record PlaybackArtwork(byte[] Content, string ContentType);

public interface IPlaybackActivitySource
{
    IReadOnlyList<PlaybackActivityState> GetActivePlaybackStates(TimeSpan maxAge);
}

public interface IPlaybackMetadataResolver
{
    Task<PlaybackTrackMetadata?> ResolveAsync(string itemId, CancellationToken cancellationToken);

    Task<PlaybackArtwork?> ResolveArtworkAsync(string itemId, CancellationToken cancellationToken);
}
