using System.Text.Json.Serialization;
using allstarr.Core.Music;
using allstarr.Core.Playback;

namespace allstarr.Core.ClientApi;

public sealed record ClientStatsArtist(string Name, long PlayCount, string? ArtworkUrl)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Id { get; init; }
}
public sealed record ClientStatsAlbum(string Title, string Artist, long PlayCount, string? ArtworkUrl);

public sealed class ClientListeningStats(ListeningStats stats)
{
    public ListeningStatsPeriod Period => stats.Period;
    public long PlayCount => stats.PlayCount;
    public int ActiveDays => stats.ActiveDays;
    public long ImportedCount => stats.ImportedCount;
    public DateTimeOffset? ObservedSince => stats.ObservedSince;
    public long EstimatedTimeFromSongLengthsMilliseconds => stats.EstimatedTimeFromSongLengthsMilliseconds;
    public ListeningStatsCoverage Coverage => stats.Coverage;
    public IReadOnlyList<ListeningStatsSong> TopSongs { get; } = stats.TopSongs.Select(item => item with
    { ArtworkUrl = Artwork(item.Artist, title: item.Title) }).ToArray();
    public IReadOnlyList<ClientStatsArtist> TopArtists { get; } = Artists(stats.TopArtists);
    public IReadOnlyList<ClientStatsAlbum> TopAlbums { get; } = stats.TopAlbums.Select(item =>
        new ClientStatsAlbum(item.Album, item.Artist, item.PlayCount, Artwork(item.Artist, album: item.Album))).ToArray();
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
    public IReadOnlyList<ClientStatsArtist> NewArtists { get; } = Artists(stats.NewArtists);

    private static ClientStatsArtist[] Artists(IReadOnlyList<ListeningStatsArtist> artists) => artists.Select(item =>
        new ClientStatsArtist(item.Artist, item.PlayCount, Artwork(item.Artist,
            libraryArtistId: item.Id != null && !item.Id.StartsWith("ext-", StringComparison.Ordinal) ? item.Id : null))
        { Id = item.Id }).ToArray();

    internal static string Artwork(string artist, string? title = null, string? album = null, string? libraryArtistId = null) =>
        "/allstarr/v1/me/stats/art?artist=" + Uri.EscapeDataString(artist) +
        (title != null ? "&title=" + Uri.EscapeDataString(title) : album != null ? "&album=" + Uri.EscapeDataString(album) : "") +
        (libraryArtistId == null ? "" : "&libraryArtistId=" + Uri.EscapeDataString(libraryArtistId));
}

public sealed record ClientTasteStrength(string Name, int Strength);
public sealed record ClientTasteLayer(IReadOnlyList<ClientTasteStrength> Tags, IReadOnlyList<ClientTasteStrength> Artists)
{
    public static ClientTasteLayer From(TasteLayer layer) => new(
        layer.Tags.Select(item => new ClientTasteStrength(item.Name, item.Strength)).ToArray(),
        layer.Artists.Select(item => new ClientTasteStrength(item.Name, item.Strength)).ToArray());
}
public sealed record ClientTasteProfile(ClientTasteLayer Lately, ClientTasteLayer Usually, double LatelyWeight,
    bool Paused, bool UseImportedHistory, IReadOnlyList<TasteNudge> Nudges);
