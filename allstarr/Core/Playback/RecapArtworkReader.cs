using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Protocols;
using allstarr.Services.Common;

namespace allstarr.Core.Playback;

// Plays only record names, so covers and artist photos are looked up by name and kept once found.
public sealed class RecapArtworkReader(IProtocolProviderGateway gateway, IMediaAssetResolver media, IHttpClientFactory clients)
{
    private const int MaximumBytes = 8 * 1024 * 1024, MinimumBytes = 1024;

    public async Task<ResolvedMediaAsset?> ReadAsync(ProtocolExecutionContext protocol, string artist, string? title, string? album, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(artist) || artist.Length > 500 || title?.Length > 500 || album?.Length > 500)
            throw new ArgumentException("The artwork request is invalid.");
        var kind = title != null ? "recap-song" : album != null ? "recap-album" : "recap-artist";
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Key(artist)}|{Key(title)}|{Key(album)}")));
        var asset = await media.ResolveAsync(new(protocol.RequireActor().EffectiveUserId, null, "recap", kind, id, Width: 192), async cancel =>
        {
            var url = await FindAsync(protocol, artist, title, album);
            if (!OutboundRequestGuard.TryCreateSafeHttpUri(url, out var uri, out _) || uri == null) return null;
            using var response = await clients.CreateClient().GetAsync(uri, cancel);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true ||
                response.Content.Headers.ContentLength > MaximumBytes) return null;
            await response.Content.LoadIntoBufferAsync(MaximumBytes, cancel);
            return new MediaAssetSource(await response.Content.ReadAsByteArrayAsync(cancel), contentType);
        }, MaximumBytes, token);
        // Some sources hold a blank picture for an artist; use the placeholder instead.
        return asset is { Bytes.Length: >= MinimumBytes } ? asset : null;
    }

    private async Task<string?> FindAsync(ProtocolExecutionContext protocol, string artist, string? title, string? album)
    {
        // One quick source covers nearly everything; the rest are only asked when it comes up empty.
        foreach (var source in new[] { "deezer", null })
        {
            var query = title != null ? $"{artist} {title}" : album != null ? $"{artist} {album}" : artist;
            var found = await gateway.SearchAsync(protocol, query, title != null ? 8 : 0, title == null && album != null ? 8 : 0, title == null && album == null ? 5 : 0, source);
            var url = title != null
                ? found.Songs.Where(song => Credited(song.Artist, artist) && Close(song.Title, title)).Select(song => song.CoverArtUrlLarge ?? song.CoverArtUrl).FirstOrDefault(Usable)
                : album != null
                    ? found.Albums.Where(item => Credited(item.Artist, artist) && Close(item.Title, album)).Select(item => item.CoverArtUrl).FirstOrDefault(Usable)
                    : found.Artists.Where(item => Key(item.Name) == Key(artist)).Select(item => item.ImageUrl).FirstOrDefault(Usable);
            if (url != null) return url;
        }
        return null;
    }

    private static bool Usable(string? url) => !string.IsNullOrWhiteSpace(url);
    private static bool Credited(string? credit, string artist) => Key(credit) == Key(artist) ||
        FuzzyMatcher.SplitCredits(credit).Any(name => Key(name) == Key(artist)) || FuzzyMatcher.SplitCredits(artist).Any(name => Key(name) == Key(credit));
    private static bool Close(string? found, string wanted)
    {
        var (left, right) = (Key(found), Key(wanted));
        return left.Length > 0 && right.Length > 0 && (left == right || left.StartsWith(right, StringComparison.Ordinal) || right.StartsWith(left, StringComparison.Ordinal));
    }
    private static string Key(string? value) => new((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
