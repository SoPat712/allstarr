using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using allstarr.Core.ClientApi;
using allstarr.Core.Music;
using allstarr.Core.Playback;

namespace allstarr.Core.ClientApiV2;

public sealed record V2HistoryPage(
    [property: JsonPropertyName("plays")] IReadOnlyList<V2HistoryPlay> Plays,
    [property: JsonPropertyName("next")] string? Next);

public sealed record V2HistoryPlay(
    [property: JsonPropertyName("track"), Description("Current track, or null when the recorded song is unavailable.")] V2Track? Track,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("album")] string? Album,
    [property: JsonPropertyName("durationMs")] long? DurationMs,
    [property: JsonPropertyName("artwork")] V2Artwork? Artwork,
    [property: JsonPropertyName("playedAt")] DateTimeOffset PlayedAt,
    [property: JsonPropertyName("isExternal")] bool IsExternal,
    [property: JsonPropertyName("provider")] string? Provider);

public sealed record V2StatsSong(
    [property: JsonPropertyName("track")] V2Track? Track,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("album")] string? Album,
    [property: JsonPropertyName("playCount")] long PlayCount,
    [property: JsonPropertyName("artwork")] V2Artwork Artwork);

public sealed record V2StatsArtist(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("playCount")] long PlayCount,
    [property: JsonPropertyName("artwork")] V2Artwork Artwork);

public sealed record V2StatsAlbum(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("playCount")] long PlayCount,
    [property: JsonPropertyName("artwork")] V2Artwork Artwork);

public sealed class V2ListeningStats(ListeningStats stats, IReadOnlyList<V2StatsSong> songs)
{
    public ListeningStatsPeriod Period => stats.Period;
    public long PlayCount => stats.PlayCount;
    public int ActiveDays => stats.ActiveDays;
    public long ImportedCount => stats.ImportedCount;
    public DateTimeOffset? ObservedSince => stats.ObservedSince;
    public long EstimatedTimeFromSongLengthsMilliseconds => stats.EstimatedTimeFromSongLengthsMilliseconds;
    public ListeningStatsCoverage Coverage => stats.Coverage;
    public IReadOnlyList<V2StatsSong> TopSongs => songs;
    public IReadOnlyList<V2StatsArtist> TopArtists { get; } = Artists(stats.TopArtists);
    public IReadOnlyList<V2StatsAlbum> TopAlbums { get; } = stats.TopAlbums.Select(item =>
        new V2StatsAlbum(item.Album, item.Artist, item.PlayCount, Artwork(item.Artist, album: item.Album))).ToArray();
    public long ShortPlayCount => stats.ShortPlayCount;
    public DateTimeOffset? FirstPlayAt => stats.FirstPlayAt;
    public int UniqueSongs => stats.UniqueSongs;
    public int UniqueArtists => stats.UniqueArtists;
    public string TimelineUnit => stats.TimelineUnit;
    public IReadOnlyList<ListeningStatsPoint> Timeline => stats.Timeline;
    public IReadOnlyList<long> ByHour => stats.ByHour;
    public ListeningStatsPoint? BiggestDay => stats.BiggestDay;
    public int LongestStreakDays => stats.LongestStreakDays;
    public IReadOnlyList<ListeningStatsGenre> TopGenres => stats.TopGenres;
    public long NewArtistCount => stats.NewArtistCount;
    public IReadOnlyList<V2StatsArtist> NewArtists { get; } = Artists(stats.NewArtists);

    private static V2StatsArtist[] Artists(IReadOnlyList<ListeningStatsArtist> artists) => artists.Select(item =>
        new V2StatsArtist(item.Id is { } id && V2Ids.TryDecode(id, V2IdKind.Artist, out _) ? V2Ids.Artist(id) : null,
            item.Artist, item.PlayCount, Artwork(item.Artist, libraryArtistId: item.Id is { } source &&
                !source.StartsWith("ext-", StringComparison.Ordinal) && V2Ids.IsValidSource(source, V2IdKind.Artist) ? V2Ids.Artist(source) : null))).ToArray();

    internal static V2Artwork Artwork(string artist, string? title = null, string? album = null, string? libraryArtistId = null) =>
        new("/allstarr/v2/me/stats/art?artist=" + Uri.EscapeDataString(artist) +
            (title != null ? "&title=" + Uri.EscapeDataString(title) : album != null ? "&album=" + Uri.EscapeDataString(album) : "") +
            (libraryArtistId == null ? "" : "&libraryArtistId=" + Uri.EscapeDataString(libraryArtistId)));
}

public sealed record V2HistoryRecheck(int Songs, int Unchecked, int Doubtful, int DoubtfulPlays, IReadOnlyList<V2HistoryRecheckSong> Examples);
public sealed record V2HistoryRecheckSong(V2Track? Track, string RecordingId, string Title, string? Artist, string? Album,
    string MatchedTitle, string? MatchedArtist, string? MatchedAlbum, string Verdict, int Plays);

public sealed record V2TasteProfile(ClientTasteLayer Lately, ClientTasteLayer Usually, double LatelyWeight,
    bool Paused, bool UseImportedHistory, IReadOnlyList<TasteNudge> Nudges);

public sealed record V2Preferences(
    [Required, RegularExpression("^(heart|keep|addToLibrary)$")]
    [property: JsonPropertyName("heartAction"), Description("The heart button action: heart, keep or addToLibrary.")] string HeartAction);
