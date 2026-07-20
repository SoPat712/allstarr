using System.Collections.Concurrent;

namespace allstarr.Services.Common;

public sealed class ExternalPlaybackMetadataResolver(
    IMusicMetadataService metadataService,
    ILogger<ExternalPlaybackMetadataResolver> logger) : IPlaybackMetadataResolver
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<PlaybackTrackMetadata?> ResolveAsync(string itemId, CancellationToken cancellationToken)
    {
        var identity = ParseTrackIdentity(itemId);
        if (identity == null) return null;
        if (_cache.TryGetValue(itemId, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
            return cached.Metadata;

        PlaybackTrackMetadata? metadata = null;
        try
        {
            var song = await metadataService.GetSongAsync(
                identity.Value.Provider, identity.Value.ExternalId, cancellationToken);
            if (song != null)
            {
                metadata = new(song.Title, song.Artist, song.Album,
                    song.CoverArtUrlLarge ?? song.CoverArtUrl, song.Duration);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unable to resolve external playback metadata for {ItemId}", itemId);
        }

        _cache[itemId] = new(metadata, DateTime.UtcNow.Add(metadata == null
            ? TimeSpan.FromSeconds(30)
            : TimeSpan.FromMinutes(10)));
        return metadata;
    }

    public Task<PlaybackArtwork?> ResolveArtworkAsync(string itemId, CancellationToken cancellationToken) =>
        Task.FromResult<PlaybackArtwork?>(null);

    internal static (string Provider, string ExternalId)? ParseTrackIdentity(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
            return null;

        var remainder = itemId[4..];
        const string marker = "-song-";
        var markerIndex = remainder.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex > 0 && markerIndex + marker.Length < remainder.Length)
            return (remainder[..markerIndex], remainder[(markerIndex + marker.Length)..]);

        var separator = remainder.IndexOf('-');
        return separator > 0 && separator + 1 < remainder.Length
            ? (remainder[..separator], remainder[(separator + 1)..])
            : null;
    }

    private sealed record CacheEntry(PlaybackTrackMetadata? Metadata, DateTime ExpiresAtUtc);
}
