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

public sealed record V2HistoryRows(
    [property: JsonPropertyName("plays")] IReadOnlyList<V2HistoryRow> Plays,
    [property: JsonPropertyName("next")] string? Next);

public sealed record V2HistoryRow(
    [property: JsonPropertyName("trackId"), Description("The id a track read gives this song, or null when the recorded song is unavailable.")] string? TrackId,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string> Aliases,
    [property: JsonPropertyName("libraryItemId"), Description("The media server item when the song is in the library.")] string? LibraryItemId,
    [property: JsonPropertyName("title"), Description("The title the play recorded.")] string Title,
    [property: JsonPropertyName("artists"), Description("The song's artists with the ids their pages open by; the recorded artist, without an id, when the song is unavailable.")] IReadOnlyList<V2Ref> Artists,
    [property: JsonPropertyName("album"), Description("The song's album with the id its page opens by; the recorded album, without an id, when the song is unavailable.")] V2AlbumRef? Album,
    [property: JsonPropertyName("durationMs")] long? DurationMs,
    [property: JsonPropertyName("artwork")] V2Artwork? Artwork,
    [property: JsonPropertyName("playedAt")] DateTimeOffset PlayedAt,
    [property: JsonPropertyName("isExternal")] bool IsExternal,
    [property: JsonPropertyName("provider")] string? Provider);

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
    [property: JsonPropertyName("trackId"), Description("The id a track read gives this song, or null when the song is unavailable.")] string? TrackId,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string> Aliases,
    [property: JsonPropertyName("libraryItemId"), Description("The media server item when the song is in the library.")] string? LibraryItemId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist"), Description("The artist as the plays were counted under.")] string Artist,
    [property: JsonPropertyName("artists"), Description("The song's artists with the ids their pages open by; the counted name, without an id, when the song is unavailable.")] IReadOnlyList<V2Ref> Artists,
    [property: JsonPropertyName("album"), Description("The album as the plays were counted under, with the id its page opens by when the song's album is known.")] V2AlbumRef? Album,
    [property: JsonPropertyName("playCount")] long PlayCount,
    [property: JsonPropertyName("artwork")] V2Artwork Artwork);

public sealed record V2StatsArtist(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("playCount")] long PlayCount,
    [property: JsonPropertyName("artwork")] V2Artwork Artwork)
{
    [JsonPropertyName("libraryId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("The media server artist when it is in the library.")]
    public string? LibraryId => V2Ids.LibrarySource(Id, V2IdKind.Artist);

    [JsonPropertyName("aliases"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("The older service id of an artist that is not in the library, which the media-server routes accept.")]
    public IReadOnlyList<string>? Aliases => V2Ids.ServiceSource(Id, V2IdKind.Artist) is { } source ? [source] : null;
}

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
    [RegularExpression("^(heart|keep|addToLibrary)$")]
    [property: JsonPropertyName("heartAction"), Description("The heart button action: heart, keep or addToLibrary.")] string? HeartAction,
    [property: JsonPropertyName("libraries"), Description("The music libraries songs are picked from for this listener: mixes, Made for you and autoplay.")]
    V2LibraryChoice? Libraries = null);

public sealed record V2LibraryChoice(
    [property: JsonPropertyName("selected"), Description("The chosen library ids, or null for every library this listener can open.")] IReadOnlyList<string>? Selected,
    [property: JsonPropertyName("available"), Description("Every music library this listener can choose. Read only.")] IReadOnlyList<V2MusicLibrary>? Available = null,
    [property: JsonPropertyName("revision"), Description("Rises each time the choice is saved, from any app; 0 when it was never set. Read only.")] long? Revision = null);

public sealed record V2MusicLibrary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record V2DatedPlaysRequest(
    [property: JsonPropertyName("plays"), Description("1 to 50 plays heard while the app could not report them, each sent once.")] IReadOnlyList<V2DatedPlay> Plays);

public sealed record V2DatedPlay(
    [property: JsonPropertyName("trackId"), Description("The track in any accepted spelling: a library item, a trk_ id or a service alias.")] string TrackId,
    [property: JsonPropertyName("startedAt"), Description("When the song started playing; within the last 60 days.")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("playedSeconds"), Description("Seconds heard, from 0 to 86400.")] double PlayedSeconds,
    [property: JsonPropertyName("playSessionId"), Description("The id the app gave this play; the same id again from this listener answers duplicate.")] string PlaySessionId,
    [property: JsonPropertyName("durationSeconds"), Description("The song's length, used when the server does not know it.")] double? DurationSeconds = null);

public sealed record V2DatedPlaysResponse(
    [property: JsonPropertyName("results"), Description("One result per play, in request order.")] IReadOnlyList<V2DatedPlayResult> Results);

public sealed record V2DatedPlayResult(
    [property: JsonPropertyName("playSessionId")] string PlaySessionId,
    [property: JsonPropertyName("state"), Description("counted, tooShort, duplicate or unknownTrack. Every state is final.")] string State);
