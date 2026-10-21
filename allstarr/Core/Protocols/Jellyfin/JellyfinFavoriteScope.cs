using allstarr.Core.Favorites;

namespace allstarr.Core.Protocols.Jellyfin;

internal static class JellyfinFavoriteScope
{
    public static bool Matches(FavoriteViewItem item, IQueryCollection query, string? libraryId)
    {
        string[] unsupported = ["Tags", "IsPlayed", "IsUnplayed", "MinCommunityRating", "MinPremiereDate", "MaxPremiereDate"];
        if (unsupported.Any(key => !string.IsNullOrWhiteSpace(query[key])) ||
            Values(query, "Filters").Any(value => !value.Equals("IsFavorite", StringComparison.OrdinalIgnoreCase))) return false;
        var display = item.Display;
        if (!Matches(query, "ArtistIds", display.ArtistIds) ||
            !Matches(query, "AlbumArtistIds", display.AlbumArtistIds) ||
            !Matches(query, "ContributingArtistIds", display.AlbumArtistIds.Length == 0 ? [] :
                display.ArtistIds.Except(display.AlbumArtistIds, StringComparer.Ordinal)) ||
            !Matches(query, "AlbumIds", display.AlbumId == null ? [] : [display.AlbumId]) ||
            !Matches(query, "Genres", display.Genres, '|', StringComparer.OrdinalIgnoreCase) ||
            !Matches(query, "GenreIds", display.Genres.Select(genre => $"genre-{genre.ToLowerInvariant()}")) ||
            !Matches(query, "Years", display.Year is { } year ? [year.ToString(System.Globalization.CultureInfo.InvariantCulture)] : []))
            return false;
        var parent = query["ParentId"].ToString();
        return string.IsNullOrEmpty(parent) || parent == libraryId || parent == display.AlbumId ||
            display.ArtistIds.Contains(parent, StringComparer.Ordinal) || display.AlbumArtistIds.Contains(parent, StringComparer.Ordinal);
    }

    public static bool RestrictNativeQuery(Dictionary<string, string> query)
    {
        var providerOnly = false;
        foreach (var key in new[] { "Ids", "ArtistIds", "AlbumArtistIds", "ContributingArtistIds", "AlbumIds", "ParentId", "GenreIds" })
        {
            if (!query.TryGetValue(key, out var value)) continue;
            var ids = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var native = ids.Where(id => FavoriteDisplaySnapshot.Resource(id) == null &&
                (key != "GenreIds" || !id.StartsWith("genre-", StringComparison.OrdinalIgnoreCase))).ToArray();
            if (ids.Length == native.Length) continue;
            providerOnly |= native.Length == 0;
            query[key] = string.Join(',', native);
        }
        return providerOnly;
    }

    private static bool Matches(IQueryCollection query, string key, IEnumerable<string> actual,
        char separator = ',', StringComparer? comparer = null)
    {
        var requested = Values(query, key, separator);
        return requested.Length == 0 || requested.Intersect(actual, comparer ?? StringComparer.Ordinal).Any();
    }

    private static string[] Values(IQueryCollection query, string key, char separator = ',') =>
        query[key].ToString().Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
