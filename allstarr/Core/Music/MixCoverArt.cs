using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;

namespace allstarr.Core.Music;

// The songs whose covers make up a mix's cover: the first four from different albums, or just the first when the mix
// has fewer. Apps, the web UI and the cover image all use the same picks.
public static class MixCoverArt
{
    public static IReadOnlyList<Song> Pick(IEnumerable<Song> songs)
    {
        var picks = new List<Song>(4);
        var albums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in songs)
        {
            if (Reference(song) is not { } reference) continue;
            var album = song.AlbumId ?? (string.IsNullOrWhiteSpace(song.Album) ? reference : $"{song.Album}\u001f{song.AlbumArtist ?? song.Artist}");
            if (!albums.Add(album)) continue;
            picks.Add(song);
            if (picks.Count == 4) break;
        }
        return picks.Count >= 4 ? picks : picks.Take(1).ToArray();
    }

    // Library songs show their album's picture from the media server; streamed songs their service's cover.
    public static string? Reference(Song song)
    {
        if (song.IsLocal) return string.IsNullOrWhiteSpace(song.AlbumId ?? song.Id) ? null : "library:" + (song.AlbumId ?? song.Id);
        var url = song.CoverArtUrl ?? song.CoverArtUrlLarge;
        return string.IsNullOrWhiteSpace(url) || ProviderArtworkFallback.IsFallback(song.ExternalProvider ?? "", url, "album") ||
               !OutboundRequestGuard.TryCreateSafeHttpUri(url, out _, out _) ? null : url;
    }
}

public sealed class MixCoverArtLoader(JellyfinProxyService jellyfin, IHttpClientFactory clients, ILogger<MixCoverArtLoader> logger)
{
    private const int MaximumBytes = 8 * 1024 * 1024;

    public async Task<byte[]?> LoadAsync(string reference, CancellationToken token)
    {
        try
        {
            if (reference.StartsWith("library:", StringComparison.Ordinal))
            {
                // The media server shares pictures without a sign-in, so a cover works for apps that load images without one.
                var (body, _, success) = await jellyfin.GetBytesSafeAsync(
                    $"Items/{Uri.EscapeDataString(reference[8..])}/Images/Primary", new() { ["maxWidth"] = "400", ["quality"] = "90" });
                return success && body is { Length: > 0 and <= MaximumBytes } ? body : null;
            }
            if (!OutboundRequestGuard.TryCreateSafeHttpUri(reference, out var uri, out _) || uri == null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await clients.CreateClient().GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true ||
                response.Content.Headers.ContentLength > MaximumBytes) return null;
            await response.Content.LoadIntoBufferAsync(MaximumBytes, timeout.Token);
            return await response.Content.ReadAsByteArrayAsync(timeout.Token);
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Mix cover picture unavailable");
            return null;
        }
    }
}
