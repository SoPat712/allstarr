using allstarr.Core.Artwork;
using allstarr.Core.Operations;
using allstarr.Models.Domain;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;
using System.Security.Cryptography;
using System.Text;

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
            var album = AlbumKey(song, reference);
            if (!albums.Add(album)) continue;
            picks.Add(song);
            if (picks.Count == 4) break;
        }
        return picks.Count >= 4 ? picks : picks.Take(1).ToArray();
    }

    internal static string AlbumKey(Song song, string reference) => song.AlbumId ??
        (string.IsNullOrWhiteSpace(song.Album) ? reference : $"{song.Album}\u001f{song.AlbumArtist ?? song.Artist}");

    // Library songs show their album's picture from the media server; streamed songs their service's cover.
    public static string? Reference(Song song)
    {
        if (song.IsLocal)
        {
            // A matched local copy can still carry its original service album ID.
            var id = string.IsNullOrWhiteSpace(song.AlbumId) || LocalLibraryService.ParseExternalResource(song.AlbumId).isExternal
                ? song.Id : song.AlbumId;
            return string.IsNullOrWhiteSpace(id) ? null : "library:" + id;
        }
        var url = song.CoverArtUrl ?? song.CoverArtUrlLarge;
        if (!string.IsNullOrWhiteSpace(url) && !ProviderArtworkFallback.IsFallback(song.ExternalProvider ?? "", url, "album") &&
            OutboundRequestGuard.TryCreateSafeHttpUri(url, out _, out _)) return url;
        var external = LocalLibraryService.ParseExternalResource(song.Id);
        return external.isExternal && external.type == "song" ? "service:" + song.Id : null;
    }
}

public sealed class MixCoverArtLoader(JellyfinProxyService jellyfin, IHttpClientFactory clients, IProtocolProviderGateway providers,
    ILogger<MixCoverArtLoader> logger)
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private readonly HashSet<(string Mix, string Reference)> _reported = [];

    internal void ReportFailure(string mix, string reference, string reason)
    {
        lock (_reported)
            if (!_reported.Add((mix, reference))) return;
        var safeReference = (reference.StartsWith("library:", StringComparison.Ordinal) || reference.StartsWith("service:", StringComparison.Ordinal)) &&
            reference.All(character => char.IsAsciiLetterOrDigit(character) || character is ':' or '-' or '_' or '.')
            ? reference : "url:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference)))[..16];
        logger.LogInformation("Mix {MixId} cover picture {Reference} unavailable: {Reason}", mix, safeReference, reason);
    }

    public async Task<byte[]?> LoadAsync(string reference, CancellationToken token, Action<string>? unavailable = null)
    {
        byte[]? Missing(string reason) { unavailable?.Invoke(reason); return null; }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            if (reference.StartsWith("library:", StringComparison.Ordinal))
            {
                using var fetching = allstarr.Core.Operations.RequestTiming.Current?.Stage("fetch");
                // The media server shares pictures without a sign-in, so a cover works for apps that load images without one.
                var (body, _, success) = await jellyfin.GetBytesSafeAsync(
                    $"Items/{Uri.EscapeDataString(reference[8..])}/Images/Primary", new() { ["maxWidth"] = "400", ["quality"] = "90" }, cancellationToken: timeout.Token);
                if (!success) return Missing("library artwork request failed");
                if (body is not { Length: > 0 }) return Missing("library artwork is empty");
                return body.Length <= MaximumBytes ? body : Missing("library artwork exceeds size limit");
            }
            if (reference.StartsWith("service:", StringComparison.Ordinal))
            {
                var external = LocalLibraryService.ParseExternalResource(reference[8..]);
                if (!external.isExternal || external.type != "song") return Missing("invalid service song reference");
                using var resolving = allstarr.Core.Operations.RequestTiming.Current?.Stage("resolve");
                reference = (await providers.GetPublicArtworkUriAsync(external.provider!, ProviderResourceKind.Track,
                    external.externalId!, timeout.Token, storedRecordingOnly: external.provider == "allstarr").WaitAsync(timeout.Token))?.AbsoluteUri ?? "";
                if (ProviderArtworkFallback.IsFallback(external.provider!, reference, "song")) return Missing("service returned no artwork or fallback artwork");
            }
            if (!OutboundRequestGuard.TryCreateSafeHttpUri(reference, out var uri, out _) || uri == null) return Missing("invalid or unsafe artwork URL");
            using var fetchingRemote = allstarr.Core.Operations.RequestTiming.Current?.Stage("fetch");
            using var response = await clients.CreateClient().GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).WaitAsync(timeout.Token);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode) return Missing($"artwork HTTP status {(int)response.StatusCode}");
            if (contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true) return Missing("artwork response is not an image");
            if (response.Content.Headers.ContentLength > MaximumBytes) return Missing("artwork exceeds size limit");
            await response.Content.LoadIntoBufferAsync(MaximumBytes, timeout.Token);
            return await response.Content.ReadAsByteArrayAsync(timeout.Token);
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            return Missing(exception is OperationCanceledException ? "artwork lookup exceeded five seconds" : exception.GetType().Name);
        }
    }
}
