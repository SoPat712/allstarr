using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using allstarr.Core.Music;
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
    public const string Search2 = "search2";
    public const string Search3 = "search3";
    private const int MaximumWindow = 500;

    /// <summary>Names the response element of a search method: searchResult2 or searchResult3.</summary>
    public static string ResultElement(string method) =>
        string.Equals(method, Search2, StringComparison.OrdinalIgnoreCase) ? "searchResult2" : "searchResult3";

    /// <summary>search2 answers in folder form, so its albums are directory entries rather than ID3 albums.</summary>
    public static bool UsesDirectoryAlbums(string method) =>
        string.Equals(method, Search2, StringComparison.OrdinalIgnoreCase);

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

/// <summary>
/// The backend half of a Subsonic integrated search: the client's own search2 or search3 request
/// and single-song lookups, kept in whatever format the backend answered with.
/// </summary>
public sealed class SubsonicNativeMusicSearch(
    SubsonicProxyService proxy,
    SubsonicModelMapper mapper,
    SubsonicResponseBuilder responses,
    SubsonicRequestParameters parameters,
    string format,
    string method = SubsonicSearchProtocolAdapter.Search3) : INativeMusicSearch<object>
{
    public bool IsJson { get; private set; } = format == "json";

    public async Task<IReadOnlyList<NativeMusicItem<object>>> SearchAsync(CancellationToken cancellationToken)
    {
        var response = await proxy.RelaySafeAsync($"rest/{method}", parameters, cancellationToken);
        IsJson = format == "json" || response.ContentType?.Contains("json") == true;
        if (!response.Success || response.Body == null) return [];
        var (songs, albums, artists) = mapper.ParseSearchResponse(
            response.Body,
            response.ContentType,
            SubsonicSearchProtocolAdapter.ResultElement(method));
        return songs.Select(item => SubsonicModelMapper.ToNativeItem(item, MusicItemKind.Song))
            .Concat(albums.Select(item => SubsonicModelMapper.ToNativeItem(item, MusicItemKind.Album)))
            .Concat(artists.Select(item => SubsonicModelMapper.ToNativeItem(item, MusicItemKind.Artist)))
            .ToList();
    }

    public async Task<NativeMusicItem<object>?> GetSongAsync(string id, CancellationToken cancellationToken)
    {
        var response = await proxy.RelaySafeAsync("rest/getSong", parameters.SetValue("id", id)
            .SetValue("f", IsJson ? "json" : "xml"), cancellationToken);
        if (!response.Success || response.Body == null) return null;
        try
        {
            if (IsJson)
            {
                using var document = JsonDocument.Parse(response.Body);
                return document.RootElement.TryGetProperty("subsonic-response", out var root) &&
                       root.TryGetProperty("status", out var status) && status.GetString() == "ok" &&
                       root.TryGetProperty("song", out var song) && song.TryGetProperty("id", out var songId) &&
                       songId.GetString() == id
                    ? SubsonicModelMapper.ToNativeItem(responses.ConvertSubsonicJsonElement(song, true), MusicItemKind.Song)
                    : null;
            }
            var xml = XDocument.Parse(Encoding.UTF8.GetString(response.Body));
            var element = xml.Root?.Element(xml.Root.GetDefaultNamespace() + "song");
            return xml.Root?.Attribute("status")?.Value == "ok" && element?.Attribute("id")?.Value == id
                ? SubsonicModelMapper.ToNativeItem(responses.ConvertSubsonicXmlElement(element, "song"), MusicItemKind.Song)
                : null;
        }
        catch (Exception error) when (error is JsonException or XmlException)
        {
            return null;
        }
    }
}
