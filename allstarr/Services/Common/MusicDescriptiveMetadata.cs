using System.Globalization;
using allstarr.Models.Domain;

namespace allstarr.Services.Common;

internal static class MusicDescriptiveMetadata
{
    public static IReadOnlyList<Artist> AlbumArtists(Song song)
    {
        if (song.AlbumArtists.Count > 0) return song.AlbumArtists;
        if (!string.IsNullOrWhiteSpace(song.AlbumArtist) &&
            !song.AlbumArtist.Equals(song.Artist, StringComparison.OrdinalIgnoreCase))
            return [new Artist { Name = song.AlbumArtist }];
        return string.IsNullOrWhiteSpace(song.Artist) ? [] :
            [new Artist { Id = song.ArtistId ?? string.Empty, Name = song.Artist }];
    }

    public static IReadOnlyList<Artist> AlbumArtists(Album album) => album.Artists.Count > 0
        ? album.Artists
        : string.IsNullOrWhiteSpace(album.Artist) ? [] :
            [new Artist { Id = album.ArtistId ?? string.Empty, Name = album.Artist }];

    public static string? Identifier(IReadOnlyDictionary<string, string> identifiers, params string[] keys) =>
        keys.Select(key => identifiers.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    public static Dictionary<string, int>? ReleaseDate(string? value, int? year)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var date = value.Length > 10 && value[10] == 'T' ? value[..10] : value;
            var formats = new[] { "yyyy", "yyyy-MM", "yyyy-MM-dd" };
            if (DateTime.TryParseExact(date, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                var result = new Dictionary<string, int> { ["year"] = parsed.Year };
                if (date.Length >= 7) result["month"] = parsed.Month;
                if (date.Length == 10) result["day"] = parsed.Day;
                return result;
            }
        }
        return year is > 0 and <= 9999 ? new() { ["year"] = year.Value } : null;
    }

    public static IEnumerable<(string Name, string Role)> Contributors(Song song)
    {
        if (!string.IsNullOrWhiteSpace(song.Composer)) yield return (song.Composer, "composer");
        foreach (var name in song.Contributors.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal))
        {
            if (!name.Equals(song.Composer, StringComparison.Ordinal)) yield return (name, "contributor");
        }
    }
}
