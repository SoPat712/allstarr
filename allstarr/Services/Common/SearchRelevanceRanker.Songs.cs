using allstarr.Models.Domain;

namespace allstarr.Services.Common;

public static partial class SearchRelevanceRanker
{
    public static double ScoreSong(string query, Song song, bool hasKnownDurationVersion = false) =>
        ScoreSong(query, song.Title, song.Artist, song.Album, song.AlbumArtist,
            song.PreciseDuration?.TotalSeconds ?? song.Duration,
            song.CanonicalRecordingId is { } recording && recording != Guid.Empty ||
            song.Isrc?.Count(char.IsLetterOrDigit) == 12, hasKnownDurationVersion,
            song.IsLocal ? null : song.AlternateTitles);
}
