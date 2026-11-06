using System.Globalization;
using allstarr.Models.Domain;

namespace allstarr.Core.Enrichment;

public static class PromotionSongFields
{
    public static Dictionary<string, string?> FromSong(Song song, string? extension = null,
        IReadOnlyDictionary<string, string>? tags = null)
    {
        static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["artist"] = string.IsNullOrWhiteSpace(song.Artist) ? "Unknown" : song.Artist,
            ["albumartist"] = string.IsNullOrWhiteSpace(song.AlbumArtist) ? song.Artist : song.AlbumArtist,
            ["album"] = string.IsNullOrWhiteSpace(song.Album) ? "Unknown" : song.Album,
            ["title"] = string.IsNullOrWhiteSpace(song.Title) ? "Unknown" : song.Title,
            ["date"] = song.ReleaseDate ?? Number(song.Year),
            ["year"] = Number(song.Year) ?? (song.ReleaseDate is { Length: >= 4 } date ? date[..4] : null),
            ["tracknumber"] = Number(song.Track ?? 0),
            ["totaltracks"] = Number(song.TotalTracks),
            ["discnumber"] = Number(song.DiscNumber ?? 1),
            ["totaldiscs"] = Number(song.TotalDiscs ?? 1),
            ["genre"] = song.Genre ?? song.Genres.FirstOrDefault(),
            ["label"] = song.Label,
            ["_extension"] = (extension ?? song.Container ?? "flac").TrimStart('.').ToLowerInvariant(),
            ["_quality"] = string.Join(' ', new[] { song.Codec?.ToUpperInvariant(), song.BitDepth.HasValue && song.SampleRate.HasValue
                ? $"{song.BitDepth.Value.ToString(CultureInfo.InvariantCulture)}-{(song.SampleRate.Value / 1000m).ToString("0.###", CultureInfo.InvariantCulture)}" : null }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
        };
        foreach (var pair in song.ProviderIds.Concat(tags ?? new Dictionary<string, string>()))
        {
            var name = pair.Key.ToLowerInvariant();
            if (name.StartsWith("musicbrainz_", StringComparison.Ordinal) && name.EndsWith("id", StringComparison.Ordinal) ||
                name is "originaldate" or "originalyear" or "catalognumber" or "releasetype" or "releasecountry" or "media")
                fields[name] = pair.Value;
        }
        if (!fields.ContainsKey("originalyear") && fields.GetValueOrDefault("originaldate") is { Length: >= 4 } original)
            fields["originalyear"] = original[..4];
        return fields;
    }
}
