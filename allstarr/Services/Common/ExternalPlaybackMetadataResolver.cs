using System.Collections.Concurrent;
using allstarr.Core.Operations;

namespace allstarr.Services.Common;

public sealed class ExternalPlaybackMetadataResolver(
    IMusicMetadataService metadataService,
    IApplicationCache cache,
    IHttpClientFactory httpClientFactory,
    IPlatformClock clock,
    ILogger<ExternalPlaybackMetadataResolver> logger,
    ApplicationCacheActivityMetrics? activityMetrics = null) : IPlaybackMetadataResolver
{
    private const int MaximumArtworkBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan MetadataCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, Lazy<Task<PlaybackTrackMetadata?>>> _inflight =
        new(StringComparer.Ordinal);
    private readonly ApplicationCacheActivityMetrics _activity =
        activityMetrics ?? new ApplicationCacheActivityMetrics();

    public async Task<PlaybackTrackMetadata?> ResolveAsync(string itemId, CancellationToken cancellationToken)
    {
        var identity = ParseTrackIdentity(itemId);
        if (identity == null) return null;
        var cacheKey = CacheKeyBuilder.BuildPlaybackMetadataKey(identity.Value.Provider, identity.Value.ExternalId);
        var negativeKey = CacheKeyBuilder.BuildPlaybackMetadataNegativeKey(
            identity.Value.Provider, identity.Value.ExternalId);
        var cached = await cache.GetAsync<PlaybackMetadataCacheEntry>(cacheKey);
        if (cached != null)
        {
            if (cached.FreshUntil <= clock.UtcNow)
            {
                _activity.RecordStaleServe();
                _ = RefreshStaleAsync(identity.Value, cacheKey, negativeKey);
            }

            return cached.Metadata;
        }
        if (await cache.ExistsAsync(negativeKey)) return null;

        return await ResolveCoalescedAsync(
            identity.Value, cacheKey, negativeKey, cancellationToken);
    }

    private async Task<PlaybackTrackMetadata?> ResolveCoalescedAsync(
        (string Provider, string ExternalId) identity,
        string cacheKey,
        string negativeKey,
        CancellationToken cancellationToken)
    {
        var created = new Lazy<Task<PlaybackTrackMetadata?>>(
            () => ResolveUncachedAsync(identity, cacheKey, negativeKey, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var pending = _inflight.GetOrAdd(cacheKey, created);
        if (!ReferenceEquals(pending, created))
        {
            _activity.RecordCoalesced();
        }
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
            new PlaybackMetadataCacheEntry(metadata, clock.UtcNow.Add(MetadataCacheDuration)),
            MetadataCacheDuration +
            ApplicationCachePolicyRegistry.Resolve(ApplicationCacheCategory.CanonicalMetadata).StaleFor);
        return metadata;
    }

    private async Task RefreshStaleAsync(
        (string Provider, string ExternalId) identity,
        string cacheKey,
        string negativeKey)
    {
        try
        {
            await ResolveCoalescedAsync(identity, cacheKey, negativeKey, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Stale playback metadata refresh failed for {CacheKey}", cacheKey);
        }
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

    private sealed record PlaybackMetadataCacheEntry(
        PlaybackTrackMetadata Metadata,
        DateTimeOffset FreshUntil);
}
