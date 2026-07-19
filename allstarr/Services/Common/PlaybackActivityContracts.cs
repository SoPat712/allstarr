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
    int? DurationSeconds = null);

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

public sealed class PlaybackDeliveryActivityStore : IPlaybackDeliveryActivitySource
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _delivered =
        new(StringComparer.Ordinal);

    public void MarkDelivered(string itemId, string? deviceId)
    {
        if (!string.IsNullOrWhiteSpace(itemId) && !string.IsNullOrWhiteSpace(deviceId))
        {
            _delivered[$"{deviceId}\n{itemId}"] = DateTimeOffset.UtcNow;
        }
    }

    public bool WasDelivered(string itemId, string deviceId)
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        foreach (var stale in _delivered.Where(item => item.Value < cutoff))
        {
            _delivered.TryRemove(stale.Key, out _);
        }
        return _delivered.ContainsKey($"{deviceId}\n{itemId}");
    }
}
