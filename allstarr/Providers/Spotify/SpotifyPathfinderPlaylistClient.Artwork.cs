using System.Text.Json;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Spotify;

public sealed partial class SpotifyPathfinderPlaylistClient
{
    private async Task<Uri?> PlaylistArtworkUriAsync(JsonElement playlist, string playlistId,
        CancellationToken cancellationToken)
    {
        var current = ArtworkUri(playlist);
        var owner = TryPath(playlist, out var ownerData, "ownerV2", "data")
            ? String(ownerData, "username") ?? String(ownerData, "uri")
            : null;
        if (!string.Equals(owner, "spotify", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(owner, "spotify:user:spotify", StringComparison.OrdinalIgnoreCase)) return current;

        var key = CacheKeyBuilder.BuildProviderPlaylistArtworkDescriptorKey(ProviderId, playlistId, "public-oembed-v1");
        var cached = cache == null ? null : await cache.GetAsync<ArtworkCacheEntry>(key);
        if (cached != null)
            return Uri.TryCreate(cached.Uri, UriKind.Absolute, out var cachedUri) &&
                   cachedUri.Scheme == Uri.UriSchemeHttps && IsAllowedArtworkHost(cachedUri.Host)
                ? cachedUri : null;

        Uri? thumbnail = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var url = Uri.EscapeDataString($"https://open.spotify.com/playlist/{playlistId}");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://open.spotify.com/oembed?url={url}");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            const int maximumBytes = 64 * 1024;
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is not > maximumBytes)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var body = new byte[maximumBytes + 1];
                var length = 0;
                int read;
                while (length < body.Length &&
                       (read = await stream.ReadAsync(body.AsMemory(length), timeout.Token)) > 0)
                    length += read;
                if (length <= maximumBytes)
                {
                    using var document = JsonDocument.Parse(body.AsMemory(0, length));
                    if (Uri.TryCreate(String(document.RootElement, "thumbnail_url"), UriKind.Absolute, out var uri) &&
                        uri.Scheme == Uri.UriSchemeHttps && IsAllowedArtworkHost(uri.Host)) thumbnail = uri;
                }
            }
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException ||
                                      error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
        }

        if (cache != null)
            await cache.SetAsync(key, new ArtworkCacheEntry(thumbnail?.AbsoluteUri ?? ""), TimeSpan.FromMinutes(2));
        return thumbnail;
    }
}
