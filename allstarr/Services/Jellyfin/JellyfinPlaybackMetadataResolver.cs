using System.Net.Http.Headers;
using System.Text.Json;
using System.Collections.Concurrent;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Jellyfin;

public sealed class JellyfinPlaybackMetadataResolver : IPlaybackMetadataResolver
{
    private const int MaximumArtworkBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan MetadataCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly JellyfinSettings _settings;
    private readonly ILogger<JellyfinPlaybackMetadataResolver> _logger;
    private readonly IApplicationCache _cache;
    private readonly ConcurrentDictionary<string, Lazy<Task<PlaybackTrackMetadata?>>> _inflight =
        new(StringComparer.Ordinal);

    public JellyfinPlaybackMetadataResolver(
        IHttpClientFactory httpClientFactory,
        IOptions<JellyfinSettings> settings,
        IApplicationCache cache,
        ILogger<JellyfinPlaybackMetadataResolver> logger)
    {
        _httpClient = httpClientFactory.CreateClient(JellyfinProxyService.HttpClientName);
        _settings = settings.Value;
        _cache = cache;
        _logger = logger;
    }

    public async Task<PlaybackTrackMetadata?> ResolveAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId) ||
            itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) ||
            !CanQueryBackend())
        {
            return null;
        }

        var cacheKey = CacheKeyBuilder.BuildPlaybackMetadataKey("jellyfin", itemId);
        var negativeKey = CacheKeyBuilder.BuildPlaybackMetadataNegativeKey("jellyfin", itemId);
        if (await _cache.ExistsAsync(negativeKey)) return null;
        var cached = await _cache.GetAsync<MetadataCacheEntry>(cacheKey);
        if (cached != null) return cached.Metadata;

        var pending = _inflight.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<PlaybackTrackMetadata?>>(
                () => ResolveUncachedAsync(itemId, cacheKey, negativeKey, cancellationToken),
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
        string itemId,
        string cacheKey,
        string negativeKey,
        CancellationToken cancellationToken)
    {
        PlaybackTrackMetadata? metadata = null;
        try
        {
            using var request = CreateRequest(BuildItemUri(itemId), "application/json");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                metadata = ParseMetadata(document.RootElement, itemId);
            }
            else
            {
                _logger.LogDebug(
                    "Jellyfin playback metadata returned {StatusCode} for item {ItemId}",
                    (int)response.StatusCode,
                    itemId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to resolve Jellyfin playback metadata for item {ItemId}", itemId);
        }

        if (metadata == null)
        {
            await _cache.SetStringAsync(negativeKey, "1", FailureCacheDuration);
            return null;
        }
        await _cache.SetAsync(
            cacheKey,
            new MetadataCacheEntry(metadata),
            MetadataCacheDuration);
        return metadata;
    }

    public async Task<PlaybackArtwork?> ResolveArtworkAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !CanQueryBackend())
        {
            return null;
        }

        using var request = CreateRequest(BuildArtworkUri(itemId), "image/*");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
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

    public static PlaybackTrackMetadata ParseMetadata(JsonElement root, string itemId)
    {
        var title = TryGetString(root, "Name") ?? "Local Jellyfin track";
        var artist = TryGetString(root, "AlbumArtist") ??
                     TryGetFirstArrayString(root, "Artists") ??
                     "Jellyfin";
        var album = TryGetString(root, "Album");
        var hasPrimaryImage = root.TryGetProperty("ImageTags", out var imageTags) &&
                              imageTags.ValueKind == JsonValueKind.Object &&
                              imageTags.TryGetProperty("Primary", out var primaryTag) &&
                              primaryTag.ValueKind == JsonValueKind.String &&
                              !string.IsNullOrWhiteSpace(primaryTag.GetString());
        var albumId = TryGetString(root, "AlbumId");
        var hasAlbumImage = !string.IsNullOrWhiteSpace(albumId) &&
                            TryGetString(root, "AlbumPrimaryImageTag") is { Length: > 0 };
        var artworkItemId = hasPrimaryImage ? itemId : hasAlbumImage ? albumId : null;
        var durationSeconds = root.TryGetProperty("RunTimeTicks", out var runTimeTicks) &&
                              runTimeTicks.TryGetInt64(out var ticks) && ticks > 0
            ? (int)Math.Ceiling(ticks / (double)TimeSpan.TicksPerSecond)
            : (int?)null;

        return new PlaybackTrackMetadata(
            title,
            artist,
            album,
            artworkItemId != null
                ? $"/api/admin/downloads/artwork/{Uri.EscapeDataString(artworkItemId)}"
                : null,
            durationSeconds);
    }

    private bool CanQueryBackend() =>
        Uri.TryCreate(_settings.Url, UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(_settings.ApiKey);

    private Uri BuildItemUri(string itemId)
    {
        var relative = $"Items/{Uri.EscapeDataString(itemId)}";
        if (!string.IsNullOrWhiteSpace(_settings.UserId))
        {
            relative += $"?userId={Uri.EscapeDataString(_settings.UserId)}";
        }

        return BuildBackendUri(relative);
    }

    private Uri BuildArtworkUri(string itemId) =>
        BuildBackendUri($"Items/{Uri.EscapeDataString(itemId)}/Images/Primary?quality=90&width=96");

    private Uri BuildBackendUri(string relative) =>
        new(new Uri(_settings.Url!.TrimEnd('/') + "/", UriKind.Absolute), relative);

    private HttpRequestMessage CreateRequest(Uri uri, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("X-Emby-Token", _settings.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return request;
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element) &&
               element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static string? TryGetFirstArrayString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return element.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private sealed record MetadataCacheEntry(PlaybackTrackMetadata Metadata);

}
