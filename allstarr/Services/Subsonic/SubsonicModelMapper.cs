using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;

namespace allstarr.Services.Subsonic;

public class SubsonicModelMapper
{
    private readonly SubsonicResponseBuilder _responseBuilder;
    private readonly ILogger<SubsonicModelMapper> _logger;

    public SubsonicModelMapper(
        SubsonicResponseBuilder responseBuilder,
        ILogger<SubsonicModelMapper> logger)
    {
        _responseBuilder = responseBuilder;
        _logger = logger;
    }

    public (List<object> Songs, List<object> Albums, List<object> Artists) ParseSearchResponse(
        byte[] responseBody,
        string? contentType)
    {
        var songs = new List<object>();
        var albums = new List<object>();
        var artists = new List<object>();

        try
        {
            var content = Encoding.UTF8.GetString(responseBody);

            if (contentType?.Contains("json") == true)
            {
                using var jsonDoc = JsonDocument.Parse(content);
                if (jsonDoc.RootElement.TryGetProperty("subsonic-response", out var response) &&
                    response.TryGetProperty("searchResult3", out var searchResult))
                {
                    if (searchResult.TryGetProperty("song", out var songElements))
                    {
                        foreach (var song in songElements.EnumerateArray())
                        {
                            songs.Add(_responseBuilder.ConvertSubsonicJsonElement(song, true));
                        }
                    }
                    if (searchResult.TryGetProperty("album", out var albumElements))
                    {
                        foreach (var album in albumElements.EnumerateArray())
                        {
                            albums.Add(_responseBuilder.ConvertSubsonicJsonElement(album, true));
                        }
                    }
                    if (searchResult.TryGetProperty("artist", out var artistElements))
                    {
                        foreach (var artist in artistElements.EnumerateArray())
                        {
                            artists.Add(_responseBuilder.ConvertSubsonicJsonElement(artist, true));
                        }
                    }
                }
            }
            else
            {
                var xmlDoc = XDocument.Parse(content);
                var ns = xmlDoc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                var searchResult = xmlDoc.Descendants(ns + "searchResult3").FirstOrDefault();

                if (searchResult != null)
                {
                    foreach (var song in searchResult.Elements(ns + "song"))
                    {
                        songs.Add(_responseBuilder.ConvertSubsonicXmlElement(song, "song"));
                    }
                    foreach (var album in searchResult.Elements(ns + "album"))
                    {
                        albums.Add(_responseBuilder.ConvertSubsonicXmlElement(album, "album"));
                    }
                    foreach (var artist in searchResult.Elements(ns + "artist"))
                    {
                        artists.Add(_responseBuilder.ConvertSubsonicXmlElement(artist, "artist"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing Subsonic search response");
        }

        return (songs, albums, artists);
    }

    public (List<object> MergedSongs, List<object> MergedAlbums, List<object> MergedArtists) MergeSearchResults(
        List<object> localSongs,
        List<object> localAlbums,
        List<object> localArtists,
        SearchResult externalResult,
        List<ExternalPlaylist> externalPlaylists,
        bool isJson,
        string query = "")
    {
        var ns = XNamespace.Get("http://subsonic.org/restapi");
        var localArtistNames = localArtists.Select(item => GetSearchField(item, "name"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var songs = externalResult.Songs.Select(song => (
            Item: isJson ? (object)_responseBuilder.ConvertSongToJson(song) : _responseBuilder.ConvertSongToXml(song, ns),
            Score: SearchRelevanceRanker.Score(query, song.Title, song.Artist)));
        var albums = externalResult.Albums.Select(album => (
            Item: isJson ? (object)_responseBuilder.ConvertAlbumToJson(album) : _responseBuilder.ConvertAlbumToXml(album, ns),
            Score: SearchRelevanceRanker.Score(query, album.Title, album.Artist)));
        var playlists = externalPlaylists.Select(playlist => (
            Item: isJson ? (object)ConvertPlaylistToAlbumJson(playlist) : ConvertPlaylistToAlbumXml(playlist, ns),
            Score: SearchRelevanceRanker.Score(query, playlist.Name, playlist.CuratorName)));
        var artists = externalResult.Artists.Where(artist => !localArtistNames.Contains(artist.Name)).Select(artist => (
            Item: isJson ? _responseBuilder.ConvertArtistToJson(artist) : _responseBuilder.ConvertArtistToXml(artist, ns),
            Score: SearchRelevanceRanker.Score(query, artist.Name)));

        return (Rank(localSongs, songs, "song", "title", includeArtist: true),
            Rank(localAlbums, albums.Concat(playlists), "album", "name", includeArtist: true),
            Rank(localArtists, artists, "artist", "name", includeArtist: false));

        List<object> Rank(List<object> local, IEnumerable<(object Item, double Score)> external,
            string type, string name, bool includeArtist)
        {
            // Preserve the native object and its unknown fields; XML only needs the protocol namespace.
            foreach (var element in local.OfType<XElement>()) element.Name = ns + type;
            return SearchRelevanceRanker.Merge(local.Select(item => (Item: item,
                    Score: SearchRelevanceRanker.Score(query, GetSearchField(item, name),
                        includeArtist ? GetSearchField(item, "artist") : null))),
                external, item => item.Score).Select(item => item.Item).ToList();
        }
    }

    private static string GetSearchField(object item, string field) => item switch
    {
        XElement element => element.Attribute(field)?.Value ?? string.Empty,
        Dictionary<string, object> values when values.TryGetValue(field, out var value) => value?.ToString() ?? string.Empty,
        _ => string.Empty
    };

    private Dictionary<string, object> ConvertPlaylistToAlbumJson(ExternalPlaylist playlist)
    {
        var artistName = $"🎵 {char.ToUpper(playlist.Provider[0])}{playlist.Provider.Substring(1)}";
        if (!string.IsNullOrEmpty(playlist.CuratorName))
        {
            artistName += $" {playlist.CuratorName}";
        }

        var artistId = $"curator-{playlist.Provider}-{playlist.CuratorName?.ToLowerInvariant().Replace(" ", "-") ?? "unknown"}";

        var album = new Dictionary<string, object>
        {
            ["id"] = playlist.Id,
            ["name"] = playlist.Name,
            ["artist"] = artistName,
            ["artistId"] = artistId,
            ["genre"] = "Playlist",
            ["songCount"] = playlist.TrackCount
        };

        if (playlist.Duration > 0)
        {
            album["duration"] = playlist.Duration;
        }

        if (playlist.CreatedDate.HasValue)
        {
            album["year"] = playlist.CreatedDate.Value.Year;
            album["created"] = playlist.CreatedDate.Value.ToString("yyyy-MM-ddTHH:mm:ss");
        }

        if (!string.IsNullOrEmpty(playlist.CoverUrl))
        {
            album["coverArt"] = playlist.Id;
        }

        return album;
    }

    private XElement ConvertPlaylistToAlbumXml(ExternalPlaylist playlist, XNamespace ns)
    {
        var artistName = $"🎵 {char.ToUpper(playlist.Provider[0])}{playlist.Provider.Substring(1)}";
        if (!string.IsNullOrEmpty(playlist.CuratorName))
        {
            artistName += $" {playlist.CuratorName}";
        }

        var artistId = $"curator-{playlist.Provider}-{playlist.CuratorName?.ToLowerInvariant().Replace(" ", "-") ?? "unknown"}";

        var album = new XElement(ns + "album",
            new XAttribute("id", playlist.Id),
            new XAttribute("name", playlist.Name),
            new XAttribute("artist", artistName),
            new XAttribute("artistId", artistId),
            new XAttribute("genre", "Playlist"),
            new XAttribute("songCount", playlist.TrackCount)
        );

        if (playlist.Duration > 0)
        {
            album.Add(new XAttribute("duration", playlist.Duration));
        }

        if (playlist.CreatedDate.HasValue)
        {
            album.Add(new XAttribute("year", playlist.CreatedDate.Value.Year));
            album.Add(new XAttribute("created", playlist.CreatedDate.Value.ToString("yyyy-MM-ddTHH:mm:ss")));
        }

        if (!string.IsNullOrEmpty(playlist.CoverUrl))
        {
            album.Add(new XAttribute("coverArt", playlist.Id));
        }

        return album;
    }
}
