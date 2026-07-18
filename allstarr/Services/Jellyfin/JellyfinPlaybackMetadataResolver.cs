using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
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
    private readonly ConcurrentDictionary<string, MetadataCacheEntry> _metadataCache =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ArtworkCacheEntry> _artworkCache =
        new(StringComparer.Ordinal);

    public JellyfinPlaybackMetadataResolver(
        IHttpClientFactory httpClientFactory,
        IOptions<JellyfinSettings> settings,
        ILogger<JellyfinPlaybackMetadataResolver> logger)
    {
        _httpClient = httpClientFactory.CreateClient(JellyfinProxyService.HttpClientName);
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<PlaybackTrackMetadata?> ResolveAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !CanQueryBackend())
        {
            return null;
        }

        if (_metadataCache.TryGetValue(itemId, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Metadata;
        }

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

        _metadataCache[itemId] = new MetadataCacheEntry(
            metadata,
            DateTime.UtcNow + (metadata == null ? FailureCacheDuration : MetadataCacheDuration));
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

        if (_artworkCache.TryGetValue(itemId, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Artwork;
        }

        PlaybackArtwork? artwork = null;
        try
        {
            using var request = CreateRequest(BuildArtworkUri(itemId), "image/*");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var contentLength = response.Content.Headers.ContentLength;

            if (response.IsSuccessStatusCode &&
                contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true &&
                (!contentLength.HasValue || contentLength.Value <= MaximumArtworkBytes))
            {
                var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (content.Length <= MaximumArtworkBytes)
                {
                    artwork = new PlaybackArtwork(content, contentType);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to resolve Jellyfin playback artwork for item {ItemId}", itemId);
        }

        _artworkCache[itemId] = new ArtworkCacheEntry(
            artwork,
            DateTime.UtcNow + (artwork == null ? FailureCacheDuration : MetadataCacheDuration));
        return artwork;
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

        return new PlaybackTrackMetadata(
            title,
            artist,
            album,
            artworkItemId != null
                ? $"/api/admin/downloads/artwork/{Uri.EscapeDataString(artworkItemId)}"
                : null);
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

    private sealed record MetadataCacheEntry(PlaybackTrackMetadata? Metadata, DateTime ExpiresAtUtc);

    private sealed record ArtworkCacheEntry(PlaybackArtwork? Artwork, DateTime ExpiresAtUtc);
}
