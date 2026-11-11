using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Music;

/// <summary>
/// Extends native artists and albums with what providers hold beyond the library.
/// A service artist joins a library artist when their songs match. Without matched songs, a same-named
/// artist joins only when it shares an album with the library, so a namesake's discography never leaks in.
/// Every linked service contributes, and names alone never link.
/// </summary>
public sealed class MusicCatalogMerge(IProtocolProviderGateway providers, ILibraryArtistLinks? links = null)
{
    private const int CandidateArtists = 5;

    public async Task<IReadOnlyList<Album>> MissingArtistAlbumsAsync(
        ProtocolExecutionContext context,
        string artistName,
        IReadOnlyCollection<Album> libraryAlbums)
    {
        var artistKey = FuzzyMatcher.NormalizeForMatching(artistName);
        if (artistKey.Length == 0) return [];
        var owned = libraryAlbums.Select(album => FuzzyMatcher.NormalizeForMatching(album.Title)).ToHashSet(StringComparer.Ordinal);
        var linked = await SongLinksAsync(context, artistName);
        var services = linked.Select(link => Service(link.ProviderId)).ToHashSet(StringComparer.Ordinal);
        var albums = (await Task.WhenAll(linked.Select(link => AlbumsAsync(context, link.ProviderId, link.ArtistId))))
            .SelectMany(found => found).ToList();
        if (owned.Count > 0)
        {
            var candidates = (await providers.SearchAsync(context, artistName, 0, 0, 20)).Artists
                .Where(artist => !string.IsNullOrEmpty(artist.ExternalId) && !string.IsNullOrEmpty(artist.ExternalProvider) &&
                                 FuzzyMatcher.NormalizeForMatching(artist.Name) == artistKey &&
                                 !services.Contains(Service(artist.ExternalProvider)))
                .OrderByDescending(artist => artist.AlbumCount ?? 0)
                .Take(CandidateArtists);
            foreach (var candidate in candidates)
            {
                if (services.Contains(Service(candidate.ExternalProvider!))) continue;
                var catalog = await AlbumsAsync(context, candidate.ExternalProvider!, candidate.ExternalId!);
                if (!catalog.Any(album => owned.Contains(FuzzyMatcher.NormalizeForMatching(album.Title)))) continue;
                services.Add(Service(candidate.ExternalProvider!));
                albums.AddRange(catalog);
            }
        }
        if (albums.Count == 0) return [];
        var nativeIds = libraryAlbums.Select(album => album.Id).ToHashSet(StringComparer.Ordinal);
        return MusicAlbumReleases.WithoutServiceRepeats(MusicAlbumReleases.Collapse(libraryAlbums.Concat(albums)))
            .Where(album => !album.IsLocal || !nativeIds.Contains(album.Id)).ToList();
    }

    private async Task<IReadOnlyList<LibraryArtistLink>> SongLinksAsync(ProtocolExecutionContext context, string artistName)
    {
        if (links == null || context.Actor?.EffectiveUserId is not { } owner) return [];
        // Links only add albums; when they can't be read the page falls back to shared albums.
        try
        {
            var found = await links.ReadAsync(owner, artistName, context.CancellationToken);
            // Matching runs in the background, so the next visit can use songs instead of names.
            if (found.Count == 0) await links.RequestAsync(owner, artistName, context.CancellationToken);
            return found;
        }
        catch (Exception error) when (error is not OperationCanceledException) { return []; }
    }

    // One service failing leaves the others' albums on the page.
    private async Task<List<Album>> AlbumsAsync(ProtocolExecutionContext context, string provider, string artistId)
    {
        try { return await providers.GetArtistAlbumsAsync(context, provider, artistId); }
        catch (Exception error) when (error is not OperationCanceledException) { return []; }
    }

    private static string Service(string provider) => AppleMusicClient.NormalizeProviderId(provider);

    /// <summary>
    /// Provider songs missing from a library album: the provider album must have the same
    /// title, or the same title with an edition suffix, and be led by the same artist.
    /// </summary>
    public async Task<IReadOnlyList<Song>> MissingAlbumSongsAsync(
        ProtocolExecutionContext context,
        string albumTitle,
        string artistName,
        IReadOnlyCollection<string> librarySongTitles)
    {
        var titleKey = FuzzyMatcher.NormalizeForMatching(albumTitle);
        var artistKey = FuzzyMatcher.NormalizeForMatching(artistName);
        if (titleKey.Length == 0 || artistKey.Length == 0) return [];
        var result = await providers.SearchAsync(context, $"{artistName} {albumTitle}", 0, 10, 0);
        var match = result.Albums.FirstOrDefault(album =>
            !string.IsNullOrEmpty(album.ExternalId) && !string.IsNullOrEmpty(album.ExternalProvider) &&
            IsSameAlbum(album.Title, albumTitle) && LeadArtistMatches(album.Artist, artistKey));
        if (match == null) return [];
        var full = await providers.GetAlbumAsync(context, match.ExternalProvider!, match.ExternalId!);
        if (full == null) return [];
        var owned = librarySongTitles.Select(FuzzyMatcher.NormalizeForMatching).ToHashSet(StringComparer.Ordinal);
        return full.Songs.Where(song => owned.Add(FuzzyMatcher.NormalizeForMatching(song.Title))).ToList();
    }

    // "Visions" matches "Visions (Deluxe Edition)" but not "Starhand Visions" or "Visions of Gideon".
    internal static bool IsSameAlbum(string? candidate, string libraryTitle)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var candidateKey = FuzzyMatcher.NormalizeForMatching(candidate);
        var titleKey = FuzzyMatcher.NormalizeForMatching(libraryTitle);
        if (candidateKey == titleKey) return true;
        return FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.StripDecorators(candidate)) == titleKey ||
               FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.StripDecorators(libraryTitle)) == candidateKey;
    }

    internal static bool LeadArtistMatches(string? credit, string artistKey)
    {
        if (string.IsNullOrWhiteSpace(credit)) return false;
        if (FuzzyMatcher.NormalizeForMatching(credit) == artistKey) return true;
        var lead = FuzzyMatcher.SplitCredits(credit).FirstOrDefault();
        return lead != null && FuzzyMatcher.NormalizeForMatching(lead) == artistKey;
    }
}
