namespace allstarr.Services.Common;

public sealed class ExternalPlaybackMetadataResolver(
    IMusicMetadataService metadataService,
    IApplicationCache cache,
    ILogger<ExternalPlaybackMetadataResolver> logger) : IPlaybackMetadataResolver
{
    private static readonly TimeSpan MetadataCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(30);

    public async Task<PlaybackTrackMetadata?> ResolveAsync(string itemId, CancellationToken cancellationToken)
    {
        var identity = ParseTrackIdentity(itemId);
        if (identity == null) return null;
        var cacheKey = CacheKeyBuilder.BuildPlaybackMetadataKey(identity.Value.Provider, identity.Value.ExternalId);
        var cached = await cache.GetAsync<PlaybackMetadataCacheEntry>(cacheKey);
        if (cached != null) return cached.Metadata;

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

        await cache.SetAsync(
            cacheKey,
            new PlaybackMetadataCacheEntry(metadata),
            metadata == null ? FailureCacheDuration : MetadataCacheDuration);
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

    private sealed record PlaybackMetadataCacheEntry(PlaybackTrackMetadata? Metadata);
}
