using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;

namespace allstarr.Services.Jellyfin;

public class JellyfinModelMapper
{
    private readonly JellyfinResponseBuilder _responseBuilder;
    private readonly ILogger<JellyfinModelMapper> _logger;

    public JellyfinModelMapper(
        JellyfinResponseBuilder responseBuilder,
        ILogger<JellyfinModelMapper> logger)
    {
        _responseBuilder = responseBuilder;
        _logger = logger;
    }

    public (List<Song> Songs, List<Album> Albums, List<Artist> Artists) ParseItemsResponse(JsonDocument? response)
    {
        var songs = new List<Song>();
        var albums = new List<Album>();
        var artists = new List<Artist>();

        if (response == null)
        {
            return (songs, albums, artists);
        }

        try
        {
            JsonElement items;

            // Jellyfin may return either an Items envelope or a bare array.
            if (response.RootElement.TryGetProperty("Items", out items))
            {
            }
            else if (response.RootElement.ValueKind == JsonValueKind.Array)
            {
                items = response.RootElement;
            }
            else
            {
                return (songs, albums, artists);
            }

            foreach (var item in items.EnumerateArray())
            {
                var type = item.TryGetProperty("Type", out var typeEl)
                    ? typeEl.GetString()
                    : null;

                switch (type)
                {
                    case "Audio":
                        songs.Add(ParseSong(item));
                        break;
                    case "MusicAlbum":
                        albums.Add(ParseAlbum(item));
                        break;
                    case "MusicArtist":
                    case "Artist":
                        artists.Add(ParseArtist(item));
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing Jellyfin items response");
        }

        return (songs, albums, artists);
    }

    public (List<Song> Songs, List<Album> Albums, List<Artist> Artists) ParseSearchHintsResponse(JsonDocument? response)
    {
        var songs = new List<Song>();
        var albums = new List<Album>();
        var artists = new List<Artist>();

        if (response == null)
        {
            return (songs, albums, artists);
        }

        try
        {
            if (!response.RootElement.TryGetProperty("SearchHints", out var hints))
            {
                return (songs, albums, artists);
            }

            foreach (var hint in hints.EnumerateArray())
            {
                var type = hint.TryGetProperty("Type", out var typeEl)
                    ? typeEl.GetString()
                    : null;

                switch (type)
                {
                    case "Audio":
                        songs.Add(ParseSongFromHint(hint));
                        break;
                    case "MusicAlbum":
                        albums.Add(ParseAlbumFromHint(hint));
                        break;
                    case "MusicArtist":
                    case "Artist":
                        artists.Add(ParseArtistFromHint(hint));
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing Jellyfin search hints response");
        }

        return (songs, albums, artists);
    }

    public Song ParseSong(JsonElement item)
    {
        var id = item.TryGetProperty("Id", out var idEl) ? idEl.GetString() ?? "" : "";
        var runTimeTicks = item.TryGetProperty("RunTimeTicks", out var rtt) ? rtt.GetInt64() : 0;

        var song = new Song
        {
            Id = id,
            Title = item.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
            Album = item.TryGetProperty("Album", out var album) ? album.GetString() ?? "" : "",
            AlbumId = item.TryGetProperty("AlbumId", out var albumId) ? albumId.GetString() : null,
            Duration = (int)(runTimeTicks / TimeSpan.TicksPerSecond),
            Track = item.TryGetProperty("IndexNumber", out var track) ? track.GetInt32() : null,
            DiscNumber = item.TryGetProperty("ParentIndexNumber", out var disc) ? disc.GetInt32() : null,
            Year = item.TryGetProperty("ProductionYear", out var year) ? year.GetInt32() : null,
            IsLocal = true
        };

        if (item.TryGetProperty("Artists", out var artists) && artists.GetArrayLength() > 0)
        {
            song.Artist = artists[0].GetString() ?? "";
        }
        else if (item.TryGetProperty("AlbumArtist", out var albumArtist))
        {
            song.Artist = albumArtist.GetString() ?? "";
        }

        if (item.TryGetProperty("ArtistItems", out var artistItems) && artistItems.GetArrayLength() > 0)
        {
            var firstArtist = artistItems[0];
            song.ArtistId = firstArtist.TryGetProperty("Id", out var artId) ? artId.GetString() : null;
        }

        if (item.TryGetProperty("Genres", out var genres) && genres.GetArrayLength() > 0)
        {
            song.Genre = genres[0].GetString();
        }

        if (item.TryGetProperty("ProviderIds", out var providerIds))
        {
            if (providerIds.TryGetProperty("ISRC", out var isrc))
            {
                song.Isrc = isrc.GetString();
            }
        }

        song.CoverArtUrl = $"/Items/{id}/Images/Primary";

        // Preserve the complete upstream item so local matches can be replayed without field loss.
        JellyfinItemSnapshotHelper.StoreRawItemSnapshot(song, item);

        // Keep technical MediaSources available to response shaping.
        song.JellyfinMetadata ??= new Dictionary<string, object?>();
        if (item.TryGetProperty("MediaSources", out var mediaSources))
        {
            song.JellyfinMetadata["MediaSources"] = JsonSerializer.Deserialize<object>(mediaSources.GetRawText());
        }

        return song;
    }

    private Song ParseSongFromHint(JsonElement hint)
    {
        var id = hint.TryGetProperty("Id", out var idEl) ? idEl.GetString() ?? "" : "";
        var runTimeTicks = hint.TryGetProperty("RunTimeTicks", out var rtt) ? rtt.GetInt64() : 0;

        return new Song
        {
            Id = id,
            Title = hint.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
            Album = hint.TryGetProperty("Album", out var album) ? album.GetString() ?? "" : "",
            Artist = hint.TryGetProperty("AlbumArtist", out var artist) ? artist.GetString() ?? "" : "",
            Duration = (int)(runTimeTicks / TimeSpan.TicksPerSecond),
            IsLocal = true,
            CoverArtUrl = $"/Items/{id}/Images/Primary"
        };
    }

    public Album ParseAlbum(JsonElement item)
    {
        var id = item.TryGetProperty("Id", out var idEl) ? idEl.GetString() ?? "" : "";

        var album = new Album
        {
            Id = id,
            Title = item.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
            Artist = item.TryGetProperty("AlbumArtist", out var artist) ? artist.GetString() ?? "" : "",
            Year = item.TryGetProperty("ProductionYear", out var year) ? year.GetInt32() : null,
            SongCount = item.TryGetProperty("ChildCount", out var count) ? count.GetInt32() : null,
            IsLocal = true,
            CoverArtUrl = $"/Items/{id}/Images/Primary"
        };

        if (item.TryGetProperty("AlbumArtists", out var albumArtists) && albumArtists.GetArrayLength() > 0)
        {
            var firstArtist = albumArtists[0];
            album.ArtistId = firstArtist.TryGetProperty("Id", out var artId) ? artId.GetString() : null;
        }

        if (item.TryGetProperty("Genres", out var genres) && genres.GetArrayLength() > 0)
        {
            album.Genre = genres[0].GetString();
        }

        return album;
    }

    private Album ParseAlbumFromHint(JsonElement hint)
    {
        var id = hint.TryGetProperty("Id", out var idEl) ? idEl.GetString() ?? "" : "";

        return new Album
        {
            Id = id,
            Title = hint.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
            Artist = hint.TryGetProperty("AlbumArtist", out var artist) ? artist.GetString() ?? "" : "",
            Year = hint.TryGetProperty("ProductionYear", out var year) ? year.GetInt32() : null,
            IsLocal = true,
            CoverArtUrl = $"/Items/{id}/Images/Primary"
        };
    }

    public Artist ParseArtist(JsonElement item)
    {
        var id = item.TryGetProperty("Id", out var idEl) ? idEl.GetString() ?? "" : "";

        return new Artist
        {
            Id = id,
            Name = item.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
            AlbumCount = item.TryGetProperty("AlbumCount", out var count) ? count.GetInt32() : null,
            IsLocal = true,
            ImageUrl = $"/Items/{id}/Images/Primary"
        };
    }

    private Artist ParseArtistFromHint(JsonElement hint)
    {
        var id = hint.TryGetProperty("Id", out var idEl) ? idEl.GetString() ?? "" : "";

        return new Artist
        {
            Id = id,
            Name = hint.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
            IsLocal = true,
            ImageUrl = $"/Items/{id}/Images/Primary"
        };
    }

    public (List<Dictionary<string, object?>> MergedSongs,
            List<Dictionary<string, object?>> MergedAlbums,
            List<Dictionary<string, object?>> MergedArtists) MergeSearchResults(
        List<Song> localSongs,
        List<Album> localAlbums,
        List<Artist> localArtists,
        SearchResult externalResult,
        List<ExternalPlaylist> externalPlaylists)
    {
        var mergedSongs = localSongs
            .Select(s => _responseBuilder.ConvertSongToJellyfinItem(s))
            .Concat(externalResult.Songs.Select(s => _responseBuilder.ConvertSongToJellyfinItem(s)))
            .ToList();

        var mergedAlbums = localAlbums
            .Select(a => _responseBuilder.ConvertAlbumToJellyfinItem(a))
            .Concat(externalResult.Albums.Select(a => _responseBuilder.ConvertAlbumToJellyfinItem(a)))
            .Concat(externalPlaylists.Select(p => _responseBuilder.ConvertPlaylistToAlbumItem(p)))
            .ToList();

        // Prefer the local artist when names collide across catalogs.
        var localArtistNames = new HashSet<string>(
            localArtists.Select(a => a.Name),
            StringComparer.OrdinalIgnoreCase);

        var mergedArtists = localArtists
            .Select(a => _responseBuilder.ConvertArtistToJellyfinItem(a))
            .ToList();

        foreach (var externalArtist in externalResult.Artists)
        {
            if (!localArtistNames.Contains(externalArtist.Name))
            {
                mergedArtists.Add(_responseBuilder.ConvertArtistToJellyfinItem(externalArtist));
            }
        }

        return (mergedSongs, mergedAlbums, mergedArtists);
    }

    public Album? ParseAlbumWithTracks(JsonDocument? albumResponse, JsonDocument? tracksResponse)
    {
        if (albumResponse == null)
        {
            return null;
        }

        var album = ParseAlbum(albumResponse.RootElement);

        if (tracksResponse != null && tracksResponse.RootElement.TryGetProperty("Items", out var tracks))
        {
            foreach (var track in tracks.EnumerateArray())
            {
                album.Songs.Add(ParseSong(track));
            }
        }

        return album;
    }

    public Artist? ParseArtistWithAlbums(JsonDocument? artistResponse, JsonDocument? albumsResponse)
    {
        if (artistResponse == null)
        {
            return null;
        }

        var artist = ParseArtist(artistResponse.RootElement);

        if (albumsResponse != null && albumsResponse.RootElement.TryGetProperty("Items", out var albums))
        {
            artist.AlbumCount = albums.GetArrayLength();
        }

        return artist;
    }
}
