using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Music;

/// <summary>
/// Extends native artists and albums with what providers hold beyond the library.
/// A provider artist joins only when it shares an album with the library artist, so a
/// namesake's discography never leaks in; a library artist without albums takes the
/// best-known provider artist of the same name.
/// </summary>
public sealed class MusicCatalogMerge(IProtocolProviderGateway providers)
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
        var candidates = (await providers.SearchAsync(context, artistName, 0, 0, 20)).Artists
            .Where(artist => !string.IsNullOrEmpty(artist.ExternalId) && !string.IsNullOrEmpty(artist.ExternalProvider) &&
                             FuzzyMatcher.NormalizeForMatching(artist.Name) == artistKey)
            .OrderByDescending(artist => artist.AlbumCount ?? 0)
            .Take(CandidateArtists);
        foreach (var candidate in candidates)
        {
            var albums = await providers.GetArtistAlbumsAsync(context, candidate.ExternalProvider!, candidate.ExternalId!);
            var titles = albums.Select(album => FuzzyMatcher.NormalizeForMatching(album.Title)).ToList();
            if (owned.Count > 0 && !titles.Any(owned.Contains)) continue;
            var nativeIds = libraryAlbums.Select(album => album.Id).ToHashSet(StringComparer.Ordinal);
            return MusicAlbumReleases.Collapse(libraryAlbums.Concat(albums))
                .Where(album => !album.IsLocal || !nativeIds.Contains(album.Id)).ToList();
        }
        return [];
    }

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
