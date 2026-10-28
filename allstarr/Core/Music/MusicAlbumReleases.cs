using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using System.Text.RegularExpressions;

namespace allstarr.Core.Music;

internal static class MusicAlbumReleases
{
    public static List<Album> Collapse(IEnumerable<Album> albums, IReadOnlySet<string>? preferredIds = null)
    {
        var rows = albums.ToArray();
        var groups = Partition(rows);
        var selected = groups.OrderBy(group => group.Min(album => Array.IndexOf(rows, album)))
            .Select(group =>
            {
                var album = group.OrderByDescending(item => item.IsLocal)
                    .ThenByDescending(item => preferredIds?.Contains(item.Id) == true)
                    .ThenBy(item => Array.IndexOf(rows, item)).First();
                if (album.IsExplicit != null || !group.Any(member => member.IsExplicit.HasValue)) return album;
                var copy = Copy(album, album.Title);
                copy.IsExplicit = group.First(member => member.IsExplicit.HasValue).IsExplicit;
                return copy;
            }).ToArray();
        return Present(selected);
    }

    internal static IReadOnlyList<List<Album>> Partition(IEnumerable<Album> albums)
    {
        var rows = albums.ToArray();
        var groups = new List<List<Album>>();
        foreach (var album in rows.OrderByDescending(album => album.IsExplicit.HasValue))
        {
            var matches = groups.Where(group => group.All(member => Compatible(member, album)) &&
                group.Any(member => SameRelease(member, album))).ToArray();
            if (matches.Length > 1)
            {
                var exact = matches.Where(group => group.Any(member => SameCatalog(member, album))).ToArray();
                if (exact.Length == 1) matches = exact;
            }
            if (matches.Length == 1) matches[0].Add(album);
            else groups.Add([album]);
        }
        return groups;
    }

    internal static bool SameRelease(Album left, Album right)
    {
        if (!Compatible(left, right)) return false;
        var firstUpc = NormalizeUpc(left.Upc);
        return SameCatalog(left, right) || firstUpc != null && firstUpc == NormalizeUpc(right.Upc) || SameTrackList(left, right);
    }

    private static bool Compatible(Album left, Album right)
    {
        if (ArtistKey(left) is not { Length: > 0 } artist || artist != ArtistKey(right) ||
            TitleKey(left) is not { Length: > 0 } title || title != TitleKey(right) ||
            EditionKey(left) != EditionKey(right) ||
            left.IsExplicit is { } first && right.IsExplicit is { } second && first != second ||
            left.SongCount is > 0 && right.SongCount is > 0 && left.SongCount != right.SongCount)
            return false;
        var firstUpc = NormalizeUpc(left.Upc);
        var secondUpc = NormalizeUpc(right.Upc);
        if (firstUpc != null && secondUpc != null && firstUpc != secondUpc && !SameTrackList(left, right)) return false;
        if (!CompatibleTracks(left, right)) return false;
        return true;
    }

    internal static bool SameCatalog(Album left, Album right) =>
        !left.IsLocal && !right.IsLocal && !string.IsNullOrWhiteSpace(left.Id) && left.Id == right.Id ||
        Aliases(left).Overlaps(Aliases(right));

    private static HashSet<(string Provider, string Id)> Aliases(Album album)
    {
        var result = new HashSet<(string, string)>();
        void Add(string? provider, string? id)
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(id)) return;
            provider = provider.Equals("apple", StringComparison.OrdinalIgnoreCase)
                ? AppleMusicClient.ProviderId : AppleMusicClient.NormalizeProviderId(provider);
            if (id.StartsWith(provider + ":", StringComparison.Ordinal)) id = id[(provider.Length + 1)..];
            result.Add((provider, id));
        }
        Add(album.ExternalProvider, album.ExternalId);
        foreach (var (provider, id) in album.ProviderIds)
            if (provider.ToLowerInvariant() is "apple" or "apple-download" or "apple-musickit" or
                "deezer" or "spotify" or "tidal" or "qobuz" or "musicbrainzalbum")
                Add(provider.Equals("musicbrainzalbum", StringComparison.OrdinalIgnoreCase) ? "musicbrainz-album" : provider, id);
        return result;
    }

    internal static string? NormalizeUpc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(value.Where(character => !char.IsWhiteSpace(character) && character != '-').ToArray());
        return digits.Length is 8 or 12 or 13 or 14 && digits.All(char.IsAsciiDigit) ? digits.PadLeft(14, '0') : null;
    }

    private static bool Complete(Album album) => album.SongCount is > 0 && album.Songs.Count == album.SongCount;

    private static bool CompatibleTracks(Album left, Album right)
    {
        if (!Complete(left) || !Complete(right)) return true;
        return left.Songs.Zip(right.Songs).All(pair =>
            (string.IsNullOrWhiteSpace(pair.First.Isrc) || string.IsNullOrWhiteSpace(pair.Second.Isrc) ||
             string.Equals(pair.First.Isrc.Trim(), pair.Second.Isrc.Trim(), StringComparison.OrdinalIgnoreCase)) &&
            (Explicit(pair.First) == null || Explicit(pair.Second) == null || Explicit(pair.First) == Explicit(pair.Second)));
    }

    private static bool SameTrackList(Album left, Album right) => Complete(left) && Complete(right) &&
        left.Songs.Count == right.Songs.Count && left.Songs.Zip(right.Songs).All(pair =>
            !string.IsNullOrWhiteSpace(pair.First.Isrc) &&
            string.Equals(pair.First.Isrc.Trim(), pair.Second.Isrc?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            FuzzyMatcher.NormalizeForMatching(pair.First.Title) == FuzzyMatcher.NormalizeForMatching(pair.Second.Title));

    private static bool? Explicit(Song song) => song.ExplicitContentLyrics switch { 1 => true, 0 or 3 => false, _ => null };

    private static string ArtistKey(Album album) => FuzzyMatcher.NormalizeForMatching(
        FuzzyMatcher.SplitCredits(album.Artist).FirstOrDefault() ?? album.Artists.FirstOrDefault()?.Name ?? "");

    private static string TitleKey(Album album)
    {
        var title = Regex.Replace(album.Title, @"\s*[\(\[](?:explicit|clean)[\)\]]\s*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var suffix = " (" + album.Edition + ")";
        if (!string.IsNullOrWhiteSpace(album.Edition) && title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            title = title[..^suffix.Length];
        return FuzzyMatcher.NormalizeForMatching(title);
    }

    private static string EditionKey(Album album) => FuzzyMatcher.NormalizeForMatching(album.Edition ?? "");

    internal static string FamilyKey(Album album) => ArtistKey(album) + "|" + TitleKey(album) + "|" + EditionKey(album);

    internal static bool HasReleaseEvidence(Album album) => NormalizeUpc(album.Upc) != null || HasTrackEvidence(album);

    internal static bool HasTrackEvidence(Album album) => Complete(album) && album.Songs.All(song => !string.IsNullOrWhiteSpace(song.Isrc));

    private static List<Album> Present(IReadOnlyList<Album> albums)
    {
        var conflicts = albums.GroupBy(album => (ArtistKey(album), TitleKey(album), EditionKey(album)))
            .Where(group => group.Any(album => album.IsExplicit == true) && group.Any(album => album.IsExplicit == false))
            .Select(group => group.Key).ToHashSet();
        return albums.Select(album =>
        {
            var title = album.Title;
            if (!string.IsNullOrWhiteSpace(album.Edition) &&
                !(" " + FuzzyMatcher.NormalizeForMatching(title) + " ").Contains(" " + EditionKey(album) + " ", StringComparison.Ordinal))
                title += " (" + album.Edition + ")";
            if (conflicts.Contains((ArtistKey(album), TitleKey(album), EditionKey(album))) && album.IsExplicit is { } explicitAlbum &&
                !Regex.IsMatch(title, @"[\(\[](?:explicit|clean)[\)\]]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                title += explicitAlbum ? " (Explicit)" : " (Clean)";
            return title == album.Title ? album : Copy(album, title);
        }).ToList();
    }

    private static Album Copy(Album source, string title) => new()
    {
        Id = source.Id,
        Title = title,
        Artist = source.Artist,
        ArtistId = source.ArtistId,
        Artists = source.Artists,
        Label = source.Label,
        Notes = source.Notes,
        ProviderIds = source.ProviderIds,
        LastFmUrl = source.LastFmUrl,
        ReleaseDate = source.ReleaseDate,
        Year = source.Year,
        SongCount = source.SongCount,
        CoverArtUrl = source.CoverArtUrl,
        Genre = source.Genre,
        IsLocal = source.IsLocal,
        ExternalProvider = source.ExternalProvider,
        ExternalId = source.ExternalId,
        Songs = source.Songs,
        IsExplicit = source.IsExplicit,
        Upc = source.Upc,
        Edition = source.Edition
    };
}
