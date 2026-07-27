using System.Collections.Concurrent;

namespace allstarr.Services.Common;

public sealed class ExternalPlaybackMetadataResolver(
    IMusicMetadataService metadataService,
    IApplicationCache cache,
    IHttpClientFactory httpClientFactory,
    ILogger<ExternalPlaybackMetadataResolver> logger) : IPlaybackMetadataResolver
{
    private const int MaximumArtworkBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan MetadataCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, Lazy<Task<PlaybackTrackMetadata?>>> _inflight =
        new(StringComparer.Ordinal);

    public async Task<PlaybackTrackMetadata?> ResolveAsync(string itemId, CancellationToken cancellationToken)
    {
        var identity = ParseTrackIdentity(itemId);
        if (identity == null) return null;
        var cacheKey = CacheKeyBuilder.BuildPlaybackMetadataKey(identity.Value.Provider, identity.Value.ExternalId);
        var negativeKey = CacheKeyBuilder.BuildPlaybackMetadataNegativeKey(
            identity.Value.Provider, identity.Value.ExternalId);
        if (await cache.ExistsAsync(negativeKey)) return null;
        var cached = await cache.GetAsync<PlaybackMetadataCacheEntry>(cacheKey);
        if (cached != null) return cached.Metadata;

        var pending = _inflight.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<PlaybackTrackMetadata?>>(
                () => ResolveUncachedAsync(identity.Value, cacheKey, negativeKey, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await pending.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            _inflight.TryRemove(new(cacheKey, pending));
        }
    }

    private async Task<PlaybackTrackMetadata?> ResolveUncachedAsync(
        (string Provider, string ExternalId) identity,
        string cacheKey,
        string negativeKey,
        CancellationToken cancellationToken)
    {
        PlaybackTrackMetadata? metadata = null;
        try
        {
            var song = await metadataService.GetSongAsync(
                identity.Provider, identity.ExternalId, cancellationToken);
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
            logger.LogDebug(
                ex,
                "Unable to resolve {Provider} playback metadata for {ExternalId}",
                identity.Provider,
                identity.ExternalId);
        }

        if (metadata == null)
        {
            await cache.SetStringAsync(negativeKey, "1", FailureCacheDuration);
            return null;
        }
        await cache.SetAsync(
            cacheKey,
            new PlaybackMetadataCacheEntry(metadata),
            MetadataCacheDuration);
        return metadata;
    }

    public async Task<PlaybackArtwork?> ResolveArtworkAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        var identity = ParseTrackIdentity(itemId);
        if (identity == null) return null;
        try
        {
            var song = await metadataService.GetSongAsync(
                identity.Value.Provider, identity.Value.ExternalId, cancellationToken);
            var artworkUrl = song?.CoverArtUrlLarge ?? song?.CoverArtUrl;
            if (!OutboundRequestGuard.TryCreateSafeHttpUri(
                    artworkUrl, out var artworkUri, out _) || artworkUri == null)
                return null;
            using var response = await httpClientFactory.CreateClient().GetAsync(
                artworkUri, cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode ||
                contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true ||
                response.Content.Headers.ContentLength > MaximumArtworkBytes)
                return null;
            await response.Content.LoadIntoBufferAsync(MaximumArtworkBytes, cancellationToken);
            return new PlaybackArtwork(
                await response.Content.ReadAsByteArrayAsync(cancellationToken),
                contentType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unable to resolve external playback artwork for {ItemId}", itemId);
            return null;
        }
    }

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

    private sealed record PlaybackMetadataCacheEntry(PlaybackTrackMetadata Metadata);
}
