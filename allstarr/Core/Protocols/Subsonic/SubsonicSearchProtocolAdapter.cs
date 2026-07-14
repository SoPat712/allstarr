using allstarr.Models.Search;
using allstarr.Services.Subsonic;

namespace allstarr.Core.Protocols.Subsonic;

public sealed record SubsonicSearchWindow(
    string Query,
    int SongCount,
    int SongOffset,
    int AlbumCount,
    int AlbumOffset,
    int ArtistCount,
    int ArtistOffset)
{
    public int SongFetchCount => checked(SongCount + SongOffset);
    public int AlbumFetchCount => checked(AlbumCount + AlbumOffset);
    public int ArtistFetchCount => checked(ArtistCount + ArtistOffset);
}

public sealed class SubsonicSearchProtocolAdapter
{
    private const int MaximumWindow = 500;

    public SubsonicSearchWindow Parse(
        SubsonicRequestParameters parameters,
        ProtocolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(context);
        if (context.Protocol != ProtocolKind.Subsonic)
        {
            throw new InvalidOperationException("Subsonic search requires a Subsonic protocol context.");
        }

        return new SubsonicSearchWindow(
            parameters.GetValueOrDefault("query").Trim().Trim('"'),
            Count(parameters, "songCount"),
            Offset(parameters, "songOffset"),
            Count(parameters, "albumCount"),
            Offset(parameters, "albumOffset"),
            Count(parameters, "artistCount"),
            Offset(parameters, "artistOffset"));
    }

    public SearchResult ApplyWindow(SearchResult result, SubsonicSearchWindow window)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(window);
        return new SearchResult
        {
            Songs = result.Songs.Skip(window.SongOffset).Take(window.SongCount).ToList(),
            Albums = result.Albums.Skip(window.AlbumOffset).Take(window.AlbumCount).ToList(),
            Artists = result.Artists.Skip(window.ArtistOffset).Take(window.ArtistCount).ToList()
        };
    }

    public List<T> ApplyAlbumWindow<T>(IEnumerable<T> values, SubsonicSearchWindow window) =>
        values.Skip(window.AlbumOffset).Take(window.AlbumCount).ToList();

    private static int Count(SubsonicRequestParameters parameters, string name) =>
        ReadBounded(parameters, name, 20);

    private static int Offset(SubsonicRequestParameters parameters, string name) =>
        ReadBounded(parameters, name, 0);

    private static int ReadBounded(
        SubsonicRequestParameters parameters,
        string name,
        int fallback)
    {
        if (!int.TryParse(parameters.GetValueOrDefault(name), out var value) || value < 0)
        {
            return fallback;
        }

        return Math.Min(value, MaximumWindow);
    }
}
