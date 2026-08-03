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
    string? CoverArtUrl,
    int? DurationSeconds = null,
    string? AlbumArtist = null,
    string? RecordingMusicBrainzId = null,
    int? TrackNumber = null);

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

public interface IPlaybackDeliveryActivitySource
{
    bool WasDelivered(string itemId, string deviceId);
}

public sealed class PlaybackDeliveryActivityStore : IPlaybackDeliveryActivitySource, IDisposable
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    private readonly Microsoft.Extensions.Caching.Memory.MemoryCache _delivered = new(
        new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions
        {
            SizeLimit = 4_096
        });

    public void MarkDelivered(string itemId, string? deviceId)
    {
        if (!string.IsNullOrWhiteSpace(itemId) && !string.IsNullOrWhiteSpace(deviceId))
        {
            Microsoft.Extensions.Caching.Memory.CacheExtensions.Set(
                _delivered,
                $"{deviceId}\n{itemId}",
                true,
                new Microsoft.Extensions.Caching.Memory.MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = Retention,
                    Size = 1
                });
        }
    }

    public bool WasDelivered(string itemId, string deviceId) =>
        _delivered.TryGetValue($"{deviceId}\n{itemId}", out _);

    public void Dispose() => _delivered.Dispose();
}
