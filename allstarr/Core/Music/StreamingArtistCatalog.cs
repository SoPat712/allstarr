using allstarr.Core.Protocols;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Music;

/// <summary>
/// One streaming artist across services: a service artist's albums plus the same artist's albums on the other
/// services. Another service's same-named artist joins only when it shares an album (barcode or title) with this one,
/// so a namesake never leaks in. Links are kept for a few hours, and a first visit waits only briefly for them.
/// </summary>
public sealed class StreamingArtistCatalog(IProtocolProviderGateway providers, TimeProvider? clock = null,
    EmptyServiceArtists? emptyArtists = null)
{
    private const int CandidatesPerService = 2, KeptLinks = 512;
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(6);
    private readonly Dictionary<string, (DateTimeOffset ExpiresAt, Task<IReadOnlyList<Link>> Work)> links = new(StringComparer.Ordinal);
    private sealed record Link(string Provider, string ArtistId);

    internal TimeSpan FirstWait { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <param name="artistTask">The artist the page already loads; its name finds the artist on other services.</param>
    public async Task<List<Album>> AlbumsAsync(ProtocolExecutionContext context, string provider, string artistId,
        Task<Artist?> artistTask)
    {
        var own = MusicAlbumReleases.Collapse(await providers.GetArtistAlbumsAsync(context, provider, artistId));
        // An artist that opens to nothing is left out of later searches.
        emptyArtists?.Record(provider, artistId, own.Count);
        Artist? artist;
        try { artist = await artistTask; }
        catch (Exception error) when (error is not OperationCanceledException) { artist = null; }
        if (artist == null || string.IsNullOrWhiteSpace(artist.Name) || own.Count == 0) return own;
        var linked = await LinkedAsync(context, provider, artistId, artist.Name, own);
        if (linked.Count == 0) return own;
        var pageId = string.IsNullOrWhiteSpace(artist.Id) ? $"ext-{provider}-artist-{artistId}" : artist.Id;
        var others = await Task.WhenAll(linked.Select(link => OtherAlbumsAsync(context, link, pageId, artist.Name)));
        return MusicAlbumReleases.WithoutServiceRepeats(MusicAlbumReleases.Collapse(own.Concat(others.SelectMany(found => found))));
    }

    private async Task<IReadOnlyList<Link>> LinkedAsync(ProtocolExecutionContext context, string provider, string artistId,
        string name, IReadOnlyList<Album> own)
    {
        var key = $"{context.Actor?.EffectiveUserId}|{Service(provider)}|{artistId}";
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        Task<IReadOnlyList<Link>> work;
        lock (links)
        {
            if (!links.TryGetValue(key, out var kept) || kept.ExpiresAt <= now || kept.Work.IsFaulted || kept.Work.IsCanceled)
            {
                if (links.Count >= KeptLinks) links.Remove(links.MinBy(item => item.Value.ExpiresAt).Key);
                kept = (now + LinkLifetime, DiscoverAsync(context, provider, name, own));
                links[key] = kept;
            }
            work = kept.Work;
        }
        // A slow first visit shows this service's albums; the links finish in the background for the next one.
        try { return await work.WaitAsync(FirstWait, context.CancellationToken); }
        catch (TimeoutException) { return []; }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested) { return []; }
        catch (Exception error) when (error is not OperationCanceledException) { return []; }
    }

    private async Task<IReadOnlyList<Link>> DiscoverAsync(ProtocolExecutionContext context, string provider, string name,
        IReadOnlyList<Album> own)
    {
        var artistKey = FuzzyMatcher.NormalizeForMatching(name);
        var service = Service(provider);
        var titles = own.Select(album => FuzzyMatcher.NormalizeForMatching(album.Title)).ToHashSet(StringComparer.Ordinal);
        var barcodes = own.Select(album => MusicAlbumReleases.NormalizeUpc(album.Upc)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var candidates = (await providers.SearchAsync(context, name, 0, 0, 20)).Artists
            .Where(artist => !string.IsNullOrEmpty(artist.ExternalId) && !string.IsNullOrEmpty(artist.ExternalProvider) &&
                             Service(artist.ExternalProvider) != service && FuzzyMatcher.NormalizeForMatching(artist.Name) == artistKey)
            .GroupBy(artist => Service(artist.ExternalProvider))
            .Select(group => group.OrderByDescending(artist => artist.AlbumCount ?? 0).Take(CandidatesPerService).ToArray());
        var failed = false;
        var found = await Task.WhenAll(candidates.Select(async group =>
        {
            foreach (var candidate in group)
            {
                List<Album> albums;
                try { albums = await providers.GetArtistAlbumsAsync(context, candidate.ExternalProvider!, candidate.ExternalId!); }
                catch (Exception error) when (error is not OperationCanceledException) { failed = true; continue; }
                if (albums.Any(album => MusicAlbumReleases.NormalizeUpc(album.Upc) is { } upc && barcodes.Contains(upc) ||
                        titles.Contains(FuzzyMatcher.NormalizeForMatching(album.Title))))
                    return new Link(candidate.ExternalProvider!, candidate.ExternalId!);
            }
            return null;
        }));
        var result = found.OfType<Link>().ToArray();
        // An answer missing because a service failed is not kept, so the next visit asks again.
        if (result.Length == 0 && failed) throw new InvalidOperationException("A service could not be checked for this artist.");
        return result;
    }

    // Only the artist's own releases come along, filed under the artist being viewed so apps list them as its albums.
    private async Task<IEnumerable<Album>> OtherAlbumsAsync(ProtocolExecutionContext context, Link link, string pageId, string name)
    {
        List<Album> albums;
        try { albums = await providers.GetArtistAlbumsAsync(context, link.Provider, link.ArtistId); }
        catch (Exception error) when (error is not OperationCanceledException) { return []; }
        var artistKey = FuzzyMatcher.NormalizeForMatching(name);
        return albums.Where(album => string.IsNullOrWhiteSpace(album.Artist) || MusicCatalogMerge.LeadArtistMatches(album.Artist, artistKey))
            .Select(album =>
            {
                var copy = album.Copy();
                if (string.IsNullOrWhiteSpace(copy.Artist)) copy.Artist = name;
                copy.ArtistId = pageId;
                return copy;
            }).ToArray();
    }

    private static string Service(string? provider) => AppleMusicClient.NormalizeProviderId(provider ?? "");
}
