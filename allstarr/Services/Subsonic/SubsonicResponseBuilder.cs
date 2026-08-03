using Microsoft.AspNetCore.Mvc;
using System.Xml.Linq;
using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;

namespace allstarr.Services.Subsonic;

/// <summary>
/// Handles building Subsonic API responses in both XML and JSON formats.
/// </summary>
public class SubsonicResponseBuilder
{
    private const string SubsonicNamespace = "http://subsonic.org/restapi";
    private const string SubsonicVersion = "1.16.1";

    /// <summary>
    /// Creates a generic Subsonic response with status "ok".
    /// </summary>
    public IActionResult CreateResponse(string format, string elementName, object data)
    {
        if (format == "json")
        {
            return CreateJsonResponse(new { status = "ok", version = SubsonicVersion });
        }

        var ns = XNamespace.Get(SubsonicNamespace);
        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "ok"),
                new XAttribute("version", SubsonicVersion),
                new XElement(ns + elementName)
            )
        );
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml" };
    }

    /// <summary>
    /// Creates a Subsonic error response.
    /// </summary>
    public IActionResult CreateError(string format, int code, string message)
    {
        if (format == "json")
        {
            return CreateJsonResponse(new
            {
                status = "failed",
                version = SubsonicVersion,
                error = new { code, message }
            });
        }

        var ns = XNamespace.Get(SubsonicNamespace);
        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "failed"),
                new XAttribute("version", SubsonicVersion),
                new XElement(ns + "error",
                    new XAttribute("code", code),
                    new XAttribute("message", message)
                )
            )
        );
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml" };
    }

    /// <summary>
    /// Creates a Subsonic response containing a single song.
    /// </summary>
    public IActionResult CreateSongResponse(string format, Song song)
    {
        if (format == "json")
        {
            return CreateJsonResponse(new
            {
                status = "ok",
                version = SubsonicVersion,
                song = ConvertSongToJson(song)
            });
        }

        var ns = XNamespace.Get(SubsonicNamespace);
        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "ok"),
                new XAttribute("version", SubsonicVersion),
                ConvertSongToXml(song, ns)
            )
        );
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml" };
    }

    public IActionResult CreateLyricsBySongIdResponse(
        string format,
        SubsonicStructuredLyrics? lyrics)
    {
        var hasContent = lyrics is { Lines.Count: > 0 };
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            object lyricsList = hasContent
                ? new
                {
                    structuredLyrics = new[]
                    {
                        new
                        {
                            displayArtist = lyrics!.DisplayArtist,
                            displayTitle = lyrics.DisplayTitle,
                            lang = string.IsNullOrWhiteSpace(lyrics.Language) ? "xxx" : lyrics.Language,
                            offset = lyrics.OffsetMilliseconds,
                            synced = lyrics.Synced,
                            line = lyrics.Lines.Select(line => lyrics.Synced
                                ? (object)new { start = line.StartMilliseconds, value = line.Text }
                                : new { value = line.Text }).ToList()
                        }
                    }
                }
                : new { };

            return CreateJsonResponse(new
            {
                status = "ok",
                version = SubsonicVersion,
                lyricsList
            });
        }

        var ns = XNamespace.Get(SubsonicNamespace);
        var lyricsListElement = new XElement(ns + "lyricsList");
        if (hasContent)
        {
            var structured = new XElement(ns + "structuredLyrics",
                new XAttribute("displayArtist", lyrics!.DisplayArtist),
                new XAttribute("displayTitle", lyrics.DisplayTitle),
                new XAttribute("lang", string.IsNullOrWhiteSpace(lyrics.Language) ? "xxx" : lyrics.Language),
                new XAttribute("offset", lyrics.OffsetMilliseconds),
                new XAttribute("synced", lyrics.Synced.ToString().ToLowerInvariant()));
            foreach (var line in lyrics.Lines)
            {
                var element = new XElement(ns + "line", line.Text);
                if (lyrics.Synced)
                {
                    element.Add(new XAttribute("start", line.StartMilliseconds));
                }

                structured.Add(element);
            }

            lyricsListElement.Add(structured);
        }

        return new ContentResult
        {
            Content = new XDocument(
                new XElement(ns + "subsonic-response",
                    new XAttribute("status", "ok"),
                    new XAttribute("version", SubsonicVersion),
                    lyricsListElement)).ToString(),
            ContentType = "application/xml"
        };
    }

    /// <summary>
    /// Creates a Subsonic response containing an album with songs.
    /// </summary>
    public IActionResult CreateAlbumResponse(string format, Album album)
    {
        if (format == "json")
        {
            var result = ConvertAlbumToJson(album);
            result["isCompilation"] = false;
            result["song"] = album.Songs.Select(ConvertSongToJson).ToList();
            return CreateJsonResponse(new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["version"] = SubsonicVersion,
                ["album"] = result
            });
        }

        var ns = XNamespace.Get(SubsonicNamespace);
        var albumElement = ConvertAlbumToXml(album, ns);
        foreach (var song in album.Songs)
        {
            albumElement.Add(ConvertSongToXml(song, ns));
        }
        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "ok"),
                new XAttribute("version", SubsonicVersion),
                albumElement
            )
        );
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml" };
    }

    /// <summary>
    /// Creates a Subsonic response for a playlist represented as an album.
    /// Playlists appear as albums with genre "Playlist".
    /// </summary>
    public IActionResult CreatePlaylistAsAlbumResponse(string format, ExternalPlaylist playlist, List<Song> tracks)
    {
        // Build artist name with emoji and curator
        var artistName = $"🎵 {char.ToUpper(playlist.Provider[0])}{playlist.Provider.Substring(1)}";
        if (!string.IsNullOrEmpty(playlist.CuratorName))
        {
            artistName += $" {playlist.CuratorName}";
        }

        var artistId = $"curator-{playlist.Provider}-{playlist.CuratorName?.ToLowerInvariant().Replace(" ", "-") ?? "unknown"}";

        // Aggregate unique genres from all tracks
        var genres = tracks
            .Where(s => !string.IsNullOrEmpty(s.Genre))
            .Select(s => s.Genre!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var genreString = genres.Count > 0 ? string.Join(", ", genres) : "Playlist";

        if (format == "json")
        {
            var album = new Dictionary<string, object?>
            {
                ["id"] = playlist.Id,
                ["name"] = playlist.Name,
                ["artist"] = artistName,
                ["artistId"] = artistId,
                ["songCount"] = tracks.Count,
                ["genre"] = genreString,
                ["isCompilation"] = false,
                ["song"] = tracks.Select(ConvertSongToJson).ToList()
            };
            if (TryGetTotalDuration(tracks, out var totalDuration)) album["duration"] = totalDuration;
            if (playlist.CreatedDate.HasValue)
            {
                album["year"] = playlist.CreatedDate.Value.Year;
                album["created"] = playlist.CreatedDate.Value.ToString("yyyy-MM-ddTHH:mm:ss");
            }
            if (!string.IsNullOrWhiteSpace(playlist.CoverUrl)) album["coverArt"] = playlist.Id;

            return CreateJsonResponse(new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["version"] = SubsonicVersion,
                ["album"] = album
            });
        }

        var ns = XNamespace.Get(SubsonicNamespace);
        var albumElement = new XElement(ns + "album",
            new XAttribute("id", playlist.Id),
            new XAttribute("name", playlist.Name),
            new XAttribute("artist", artistName),
            new XAttribute("artistId", artistId),
            new XAttribute("songCount", tracks.Count),
            new XAttribute("genre", genreString)
        );

        if (TryGetTotalDuration(tracks, out var xmlDuration))
            albumElement.Add(new XAttribute("duration", xmlDuration));
        if (!string.IsNullOrWhiteSpace(playlist.CoverUrl))
            albumElement.Add(new XAttribute("coverArt", playlist.Id));

        if (playlist.CreatedDate.HasValue)
        {
            albumElement.Add(new XAttribute("year", playlist.CreatedDate.Value.Year));
            albumElement.Add(new XAttribute("created", playlist.CreatedDate.Value.ToString("yyyy-MM-ddTHH:mm:ss")));
        }

        // Add songs
        foreach (var song in tracks)
        {
            albumElement.Add(ConvertSongToXml(song, ns));
        }

        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "ok"),
                new XAttribute("version", SubsonicVersion),
                albumElement
            )
        );
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml" };
    }

    /// <summary>
    /// Creates a Subsonic response containing an artist with albums.
    /// </summary>
    public IActionResult CreateArtistResponse(string format, Artist artist, List<Album> albums)
    {
        if (format == "json")
        {
            return CreateJsonResponse(new
            {
                status = "ok",
                version = SubsonicVersion,
                artist = new
                {
                    id = artist.Id,
                    name = artist.Name,
                    coverArt = artist.Id,
                    albumCount = albums.Count,
                    artistImageUrl = artist.ImageUrl,
                    album = albums.Select(a => ConvertAlbumToJson(a)).ToList()
                }
            });
        }

        var ns = XNamespace.Get(SubsonicNamespace);
        var doc = new XDocument(
            new XElement(ns + "subsonic-response",
                new XAttribute("status", "ok"),
                new XAttribute("version", SubsonicVersion),
                new XElement(ns + "artist",
                    new XAttribute("id", artist.Id),
                    new XAttribute("name", artist.Name),
                    new XAttribute("coverArt", artist.Id),
                    new XAttribute("albumCount", albums.Count),
                    albums.Select(a => ConvertAlbumToXml(a, ns))
                )
            )
        );
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml" };
    }

    /// <summary>
    /// Creates a JSON Subsonic response with "subsonic-response" key (with hyphen).
    /// </summary>
    public IActionResult CreateJsonResponse(object responseContent)
    {
        var response = new Dictionary<string, object>
        {
            ["subsonic-response"] = responseContent
        };
        return new JsonResult(response);
    }

    /// <summary>
    /// Converts a Song domain model to Subsonic JSON format.
    /// </summary>
    public Dictionary<string, object> ConvertSongToJson(Song song)
    {
        var result = new Dictionary<string, object>
        {
            ["id"] = song.Id,
            ["isDir"] = false,
            ["title"] = song.Title,
            ["type"] = "music",
            ["isVideo"] = false,
            ["isExternal"] = !song.IsLocal
        };

        if (!string.IsNullOrWhiteSpace(song.AlbumId))
        {
            result["parent"] = song.AlbumId;
            result["albumId"] = song.AlbumId;
        }
        if (!string.IsNullOrWhiteSpace(song.Album)) result["album"] = song.Album;
        if (!string.IsNullOrWhiteSpace(song.Artist))
        {
            result["artist"] = song.Artist;
            result["displayArtist"] = song.Artist;
        }
        if (!string.IsNullOrWhiteSpace(song.ArtistId)) result["artistId"] = song.ArtistId;
        if (song.Duration is > 0) result["duration"] = song.Duration.Value;
        if (song.Track is > 0) result["track"] = song.Track.Value;
        if (song.DiscNumber is > 0) result["discNumber"] = song.DiscNumber.Value;
        if (song.Year is > 0) result["year"] = song.Year.Value;
        if (song.Bpm is > 0) result["bpm"] = song.Bpm.Value;
        if (!string.IsNullOrWhiteSpace(song.Genre)) result["genre"] = song.Genre;
        var albumArtist = !string.IsNullOrWhiteSpace(song.AlbumArtist) ? song.AlbumArtist : song.Artist;
        if (!string.IsNullOrWhiteSpace(albumArtist)) result["displayAlbumArtist"] = albumArtist;
        if (!string.IsNullOrWhiteSpace(song.Composer)) result["displayComposer"] = song.Composer;

        if (song.IsLocal || !string.IsNullOrWhiteSpace(song.CoverArtUrl))
        {
            result["coverArt"] = song.Id;
        }

        if (TryGetKnownMediaType(song.LocalPath, out var suffix, out var contentType))
        {
            result["suffix"] = suffix;
            result["contentType"] = contentType;
        }

        return result;
    }

    /// <summary>
    /// Converts an Album domain model to Subsonic JSON format.
    /// </summary>
    public Dictionary<string, object?> ConvertAlbumToJson(Album album)
    {
        var result = new Dictionary<string, object?>
        {
            ["id"] = album.Id,
            ["name"] = album.Title,
            ["isExternal"] = !album.IsLocal
        };
        if (!string.IsNullOrWhiteSpace(album.Artist))
        {
            result["artist"] = album.Artist;
            result["displayArtist"] = album.Artist;
        }
        if (!string.IsNullOrWhiteSpace(album.ArtistId)) result["artistId"] = album.ArtistId;
        if (album.Songs.Count > 0) result["songCount"] = album.Songs.Count;
        else if (album.SongCount.HasValue) result["songCount"] = album.SongCount.Value;
        if (TryGetTotalDuration(album.Songs, out var duration)) result["duration"] = duration;
        if (album.Year is > 0) result["year"] = album.Year.Value;
        if (album.IsLocal || !string.IsNullOrWhiteSpace(album.CoverArtUrl))
        {
            result["coverArt"] = album.Id;
        }

        if (!string.IsNullOrWhiteSpace(album.Genre))
        {
            result["genre"] = album.Genre;
        }

        return result;
    }

    /// <summary>
    /// Converts an Artist domain model to Subsonic JSON format.
    /// </summary>
    public object ConvertArtistToJson(Artist artist)
    {
        var result = new Dictionary<string, object>
        {
            ["id"] = artist.Id,
            ["name"] = artist.Name,
            ["albumCount"] = artist.AlbumCount ?? 0,
            ["isExternal"] = !artist.IsLocal
        };
        if (artist.IsLocal || !string.IsNullOrWhiteSpace(artist.ImageUrl))
        {
            result["coverArt"] = artist.Id;
        }

        return result;
    }

    /// <summary>
    /// Converts a Song domain model to Subsonic XML format.
    /// </summary>
    public XElement ConvertSongToXml(Song song, XNamespace ns)
    {
        var element = new XElement(ns + "song",
            new XAttribute("id", song.Id),
            new XAttribute("isDir", "false"),
            new XAttribute("title", song.Title),
            new XAttribute("type", "music"),
            new XAttribute("isVideo", "false"),
            new XAttribute("isExternal", (!song.IsLocal).ToString().ToLower())
        );

        if (!string.IsNullOrWhiteSpace(song.AlbumId))
        {
            element.Add(new XAttribute("parent", song.AlbumId));
            element.Add(new XAttribute("albumId", song.AlbumId));
        }
        if (!string.IsNullOrWhiteSpace(song.Album)) element.Add(new XAttribute("album", song.Album));
        if (!string.IsNullOrWhiteSpace(song.Artist))
        {
            element.Add(new XAttribute("artist", song.Artist));
            element.Add(new XAttribute("displayArtist", song.Artist));
        }
        if (!string.IsNullOrWhiteSpace(song.ArtistId))
        {
            element.Add(new XAttribute("artistId", song.ArtistId));
        }
        if (song.Duration is > 0) element.Add(new XAttribute("duration", song.Duration.Value));
        if (song.Track is > 0) element.Add(new XAttribute("track", song.Track.Value));
        if (song.DiscNumber is > 0) element.Add(new XAttribute("discNumber", song.DiscNumber.Value));
        if (song.Year is > 0) element.Add(new XAttribute("year", song.Year.Value));
        if (song.Bpm is > 0) element.Add(new XAttribute("bpm", song.Bpm.Value));
        var albumArtist = !string.IsNullOrWhiteSpace(song.AlbumArtist) ? song.AlbumArtist : song.Artist;
        if (!string.IsNullOrWhiteSpace(albumArtist))
            element.Add(new XAttribute("displayAlbumArtist", albumArtist));
        if (!string.IsNullOrWhiteSpace(song.Composer))
            element.Add(new XAttribute("displayComposer", song.Composer));

        if (song.IsLocal || !string.IsNullOrWhiteSpace(song.CoverArtUrl))
        {
            element.Add(new XAttribute("coverArt", song.Id));
        }

        if (!string.IsNullOrWhiteSpace(song.Genre))
        {
            element.Add(new XAttribute("genre", song.Genre));
        }

        if (TryGetKnownMediaType(song.LocalPath, out var suffix, out var contentType))
        {
            element.Add(new XAttribute("suffix", suffix));
            element.Add(new XAttribute("contentType", contentType));
        }

        return element;
    }

    /// <summary>
    /// Converts an Album domain model to Subsonic XML format.
    /// </summary>
    public XElement ConvertAlbumToXml(Album album, XNamespace ns)
    {
        var element = new XElement(ns + "album",
            new XAttribute("id", album.Id),
            new XAttribute("name", album.Title),
            new XAttribute("isExternal", (!album.IsLocal).ToString().ToLower())
        );

        if (!string.IsNullOrWhiteSpace(album.Artist))
        {
            element.Add(new XAttribute("artist", album.Artist));
            element.Add(new XAttribute("displayArtist", album.Artist));
        }
        if (!string.IsNullOrWhiteSpace(album.ArtistId))
            element.Add(new XAttribute("artistId", album.ArtistId));
        if (album.Songs.Count > 0) element.Add(new XAttribute("songCount", album.Songs.Count));
        else if (album.SongCount.HasValue) element.Add(new XAttribute("songCount", album.SongCount.Value));
        if (TryGetTotalDuration(album.Songs, out var duration))
            element.Add(new XAttribute("duration", duration));
        if (album.Year is > 0) element.Add(new XAttribute("year", album.Year.Value));
        if (album.IsLocal || !string.IsNullOrWhiteSpace(album.CoverArtUrl))
        {
            element.Add(new XAttribute("coverArt", album.Id));
        }

        if (!string.IsNullOrWhiteSpace(album.Genre))
        {
            element.Add(new XAttribute("genre", album.Genre));
        }

        return element;
    }

    /// <summary>
    /// Converts an Artist domain model to Subsonic XML format.
    /// </summary>
    public XElement ConvertArtistToXml(Artist artist, XNamespace ns)
    {
        var element = new XElement(ns + "artist",
            new XAttribute("id", artist.Id),
            new XAttribute("name", artist.Name),
            new XAttribute("albumCount", artist.AlbumCount ?? 0),
            new XAttribute("isExternal", (!artist.IsLocal).ToString().ToLower())
        );
        if (artist.IsLocal || !string.IsNullOrWhiteSpace(artist.ImageUrl))
        {
            element.Add(new XAttribute("coverArt", artist.Id));
        }

        return element;
    }

    /// <summary>
    /// Converts a Subsonic JSON element to a dictionary.
    /// </summary>
    public object ConvertSubsonicJsonElement(JsonElement element, bool isLocal)
    {
        var dict = new Dictionary<string, object>();
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = ConvertJsonValue(prop.Value);
        }
        dict["isExternal"] = !isLocal;
        return dict;
    }

    /// <summary>
    /// Converts a Subsonic XML element.
    /// </summary>
    public XElement ConvertSubsonicXmlElement(XElement element, string type)
    {
        var newElement = new XElement(element);
        newElement.SetAttributeValue("isExternal", "false");
        return newElement;
    }

    private object ConvertJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.TryGetInt32(out var i) ? i : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertJsonValue).ToList(),
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonValue(p.Value)),
            JsonValueKind.Null => null!,
            _ => value.ToString()
        };
    }

    private static bool TryGetKnownMediaType(
        string? path,
        out string suffix,
        out string contentType)
    {
        suffix = Path.GetExtension(path ?? string.Empty).TrimStart('.').ToLowerInvariant();
        contentType = suffix switch
        {
            "mp3" => "audio/mpeg",
            "flac" => "audio/flac",
            "ogg" => "audio/ogg",
            "m4a" => "audio/mp4",
            "aac" => "audio/aac",
            "wav" => "audio/wav",
            _ => string.Empty
        };
        return contentType.Length > 0;
    }

    private static bool TryGetTotalDuration(
        IReadOnlyCollection<Song> songs,
        out int duration)
    {
        duration = 0;
        if (songs.Count == 0 || songs.Any(song => song.Duration is not > 0)) return false;
        try
        {
            duration = checked(songs.Sum(song => song.Duration!.Value));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
