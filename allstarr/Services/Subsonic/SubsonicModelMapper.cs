using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Music;
using allstarr.Models.Subsonic;

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
        string? contentType,
        string resultElement = "searchResult3")
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
                    response.TryGetProperty(resultElement, out var searchResult))
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
                var searchResult = xmlDoc.Descendants(ns + resultElement).FirstOrDefault();

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

    /// <summary>Reads the songs of a successful native getTopSongs answer; null when it is not one.</summary>
    public List<object>? ParseTopSongs(byte[] body, string? contentType)
    {
        try
        {
            var content = Encoding.UTF8.GetString(body);
            if (contentType?.Contains("json") == true)
            {
                using var document = JsonDocument.Parse(content);
                if (!document.RootElement.TryGetProperty("subsonic-response", out var response) ||
                    !response.TryGetProperty("status", out var status) || status.GetString() != "ok" ||
                    !response.TryGetProperty("topSongs", out var topSongs)) return null;
                return topSongs.TryGetProperty("song", out var songs) && songs.ValueKind == JsonValueKind.Array
                    ? songs.EnumerateArray().Select(song => _responseBuilder.ConvertSubsonicJsonElement(song, true)).ToList()
                    : [];
            }
            var xml = XDocument.Parse(content);
            var ns = xml.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var top = xml.Root?.Element(ns + "topSongs");
            if (xml.Root?.Attribute("status")?.Value != "ok" || top == null) return null;
            return top.Elements(ns + "song").Select(song => (object)_responseBuilder.ConvertSubsonicXmlElement(song, "song")).ToList();
        }
        catch (Exception error) when (error is JsonException or System.Xml.XmlException)
        {
            return null;
        }
    }

    public static string SongTitle(object item) => GetSearchField(item, "title");

    /// <summary>Describes a native search row for the shared search core without changing it.</summary>
    /// <remarks>Directory albums (search2) carry their name in <c>title</c> instead of <c>name</c>.</remarks>
    public static NativeMusicItem<object> ToNativeItem(object item, MusicItemKind kind) => new(
        kind,
        GetSearchField(item, "id"),
        kind == MusicItemKind.Song ? GetSearchField(item, "title") : NameOrTitle(item),
        kind == MusicItemKind.Artist ? null : GetSearchField(item, "artist"),
        item)
    {
        ExplicitContentLyrics = GetSearchField(item, "explicitStatus") switch { "clean" => 0, "explicit" => 1, _ => (int?)null },
        AlbumText = GetSearchField(item, "album"),
        AlbumArtistText = GetSearchField(item, "albumArtist"),
        DurationSeconds = int.TryParse(GetSearchField(item, "duration"), out var seconds) ? seconds : null,
        AlbumMetadata = kind == MusicItemKind.Album ? new()
        {
            Id = GetSearchField(item, "id"),
            Title = NameOrTitle(item),
            Artist = GetSearchField(item, "artist"),
            IsLocal = true,
            Upc = GetSearchField(item, "upc"),
            Edition = GetSearchField(item, "edition"),
            IsExplicit = GetSearchField(item, "explicitStatus") switch { "explicit" => true, "clean" => false, _ => null },
            SongCount = int.TryParse(GetSearchField(item, "songCount"), out var count) && count > 0 ? count : null
        } : null
    };

    /// <summary>Renders merged search rows: native rows keep every field, provider rows are converted.</summary>
    /// <param name="directoryAlbums">
    /// True for folder-based answers (search2), where provider albums and playlists become directory entries.
    /// </param>
    public (List<object> Songs, List<object> Albums, List<object> Artists) RenderSearchResult(
        MusicSearchResult<object> result,
        bool isJson,
        bool directoryAlbums = false)
    {
        var ns = XNamespace.Get("http://subsonic.org/restapi");
        return (Render(result.Songs, "song"), Render(result.Albums, "album"), Render(result.Artists, "artist"));

        List<object> Render(IEnumerable<MusicSearchEntry<object>> entries, string type) =>
            entries.Select(entry =>
            {
                if (entry.Native != null)
                {
                    // Native XML only needs the protocol namespace; JSON passes through as-is.
                    if (entry.Native.Payload is XElement element) element.Name = ns + type;
                    return entry.Native.Payload;
                }
                if (entry.Song != null)
                    return isJson ? (object)_responseBuilder.ConvertSongToJson(entry.Song) : _responseBuilder.ConvertSongToXml(entry.Song, ns);
                if (entry.Album != null && directoryAlbums)
                    return isJson ? (object)_responseBuilder.ConvertAlbumToDirectoryJson(entry.Album) : _responseBuilder.ConvertAlbumToDirectoryXml(entry.Album, ns);
                if (entry.Album != null)
                    return isJson ? (object)_responseBuilder.ConvertAlbumToJson(entry.Album) : _responseBuilder.ConvertAlbumToXml(entry.Album, ns);
                if (entry.Playlist != null && directoryAlbums)
                    return isJson ? (object)ConvertPlaylistToDirectoryJson(entry.Playlist) : ConvertPlaylistToDirectoryXml(entry.Playlist, ns);
                if (entry.Playlist != null)
                    return isJson ? (object)ConvertPlaylistToAlbumJson(entry.Playlist) : ConvertPlaylistToAlbumXml(entry.Playlist, ns);
                return isJson ? _responseBuilder.ConvertArtistToJson(entry.Artist!) : _responseBuilder.ConvertArtistToXml(entry.Artist!, ns);
            }).ToList();
    }

    private static string GetSearchField(object item, string field) => item switch
    {
        XElement element => element.Attribute(field)?.Value ?? string.Empty,
        Dictionary<string, object> values when values.TryGetValue(field, out var value) => value?.ToString() ?? string.Empty,
        _ => string.Empty
    };

    private static string NameOrTitle(object item)
    {
        var name = GetSearchField(item, "name");
        return name.Length > 0 ? name : GetSearchField(item, "title");
    }

    private static (string Name, string Id) PlaylistCurator(ExternalPlaylist playlist)
    {
        var artistName = $"🎵 {char.ToUpper(playlist.Provider[0])}{playlist.Provider.Substring(1)}";
        if (!string.IsNullOrEmpty(playlist.CuratorName))
        {
            artistName += $" {playlist.CuratorName}";
        }

        var artistId = $"curator-{playlist.Provider}-{playlist.CuratorName?.ToLowerInvariant().Replace(" ", "-") ?? "unknown"}";
        return (artistName, artistId);
    }

    private static Dictionary<string, object> ConvertPlaylistToDirectoryJson(ExternalPlaylist playlist)
    {
        var album = new Dictionary<string, object>
        {
            ["id"] = playlist.Id,
            ["isDir"] = true,
            ["title"] = playlist.Name,
            ["album"] = playlist.Name,
            ["artist"] = PlaylistCurator(playlist).Name,
            ["genre"] = "Playlist"
        };
        if (playlist.CreatedDate.HasValue) album["year"] = playlist.CreatedDate.Value.Year;
        if (!string.IsNullOrEmpty(playlist.CoverUrl)) album["coverArt"] = playlist.Id;
        return album;
    }

    private static XElement ConvertPlaylistToDirectoryXml(ExternalPlaylist playlist, XNamespace ns)
    {
        var album = new XElement(ns + "album",
            new XAttribute("id", playlist.Id),
            new XAttribute("isDir", "true"),
            new XAttribute("title", playlist.Name),
            new XAttribute("album", playlist.Name),
            new XAttribute("artist", PlaylistCurator(playlist).Name),
            new XAttribute("genre", "Playlist"));
        if (playlist.CreatedDate.HasValue) album.Add(new XAttribute("year", playlist.CreatedDate.Value.Year));
        if (!string.IsNullOrEmpty(playlist.CoverUrl)) album.Add(new XAttribute("coverArt", playlist.Id));
        return album;
    }

    private Dictionary<string, object> ConvertPlaylistToAlbumJson(ExternalPlaylist playlist)
    {
        var (artistName, artistId) = PlaylistCurator(playlist);

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
        var (artistName, artistId) = PlaylistCurator(playlist);

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
