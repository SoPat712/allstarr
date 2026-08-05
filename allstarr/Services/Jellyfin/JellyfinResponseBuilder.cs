using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;

namespace allstarr.Services.Jellyfin;

/// <summary>
/// Builds Jellyfin-compatible API responses.
/// </summary>
public class JellyfinResponseBuilder
{
    private const int DefaultExternalBitrate = 1_337_000;
    private readonly string _serverId;

    public JellyfinResponseBuilder(IOptions<JellyfinSettings>? settings = null)
    {
        _serverId = string.IsNullOrWhiteSpace(settings?.Value.DeviceId)
            ? "allstarrrr-proxy"
            : settings.Value.DeviceId;
    }

    /// <summary>
    /// Creates a Jellyfin items response containing songs.
    /// </summary>
    public IActionResult CreateItemsResponse(List<Song> songs)
    {
        var items = songs.Select(ConvertSongToJellyfinItem).ToList();

        return CreateJsonResponse(new
        {
            Items = items,
            TotalRecordCount = items.Count,
            StartIndex = 0
        });
    }

    /// <summary>
    /// Creates a Jellyfin items response for albums.
    /// </summary>
    public IActionResult CreateAlbumsResponse(List<Album> albums)
    {
        var items = albums.Select(ConvertAlbumToJellyfinItem).ToList();

        return CreateJsonResponse(new
        {
            Items = items,
            TotalRecordCount = items.Count,
            StartIndex = 0
        });
    }

    /// <summary>
    /// Creates a Jellyfin items response for artists.
    /// </summary>
    public IActionResult CreateArtistsResponse(List<Artist> artists)
    {
        var items = artists.Select(ConvertArtistToJellyfinItem).ToList();

        return CreateJsonResponse(new
        {
            Items = items,
            TotalRecordCount = items.Count,
            StartIndex = 0
        });
    }

    /// <summary>
    /// Creates a single item response.
    /// </summary>
    public IActionResult CreateSongResponse(Song song)
    {
        return CreateJsonResponse(ConvertSongToJellyfinItem(song));
    }

    /// <summary>
    /// Creates a single album response with tracks.
    /// </summary>
    public IActionResult CreateAlbumResponse(Album album)
    {
        var albumItem = ConvertAlbumToJellyfinItem(album);

        // For album detail, include child items (songs)
        if (album.Songs.Count > 0)
        {
            albumItem["Children"] = album.Songs.Select(ConvertSongToJellyfinItem).ToList();
        }

        return CreateJsonResponse(albumItem);
    }

    /// <summary>
    /// Creates a single artist response with albums.
    /// </summary>
    public IActionResult CreateArtistResponse(Artist artist, List<Album> albums)
    {
        var artistItem = ConvertArtistToJellyfinItem(artist);
        artistItem["Albums"] = albums.Select(ConvertAlbumToJellyfinItem).ToList();

        return CreateJsonResponse(artistItem);
    }

    /// <summary>
    /// Creates a response for a playlist represented as an album.
    /// </summary>
    public IActionResult CreatePlaylistAsAlbumResponse(ExternalPlaylist playlist, List<Song> tracks)
    {
        var totalDuration = tracks.Sum(s => s.Duration ?? 0);

        var curatorName = !string.IsNullOrEmpty(playlist.CuratorName)
            ? playlist.CuratorName
            : playlist.Provider;

        // Create artist items for the curator
        var artistId = $"ext-{playlist.Provider}-curator-{curatorName.ToLowerInvariant().Replace(" ", "-")}";
        var artistItems = new[]
        {
                new Dictionary<string, object>
                {
                    ["Name"] = curatorName,
                    ["Id"] = artistId
                }
            };

        // Aggregate unique genres from all tracks
        var genres = tracks
            .Where(s => !string.IsNullOrEmpty(s.Genre))
            .Select(s => s.Genre!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // If no genres found, fallback to "Playlist"
        if (genres.Count == 0)
        {
            genres.Add("Playlist");
        }

        var genreItems = genres.Select(g => new Dictionary<string, object>
        {
            ["Name"] = g,
            ["Id"] = $"genre-{g.ToLowerInvariant()}"
        }).ToArray();

        var albumItem = new Dictionary<string, object?>
        {
            ["Id"] = playlist.Id,
            ["Name"] = BuildExternalPlaylistName(playlist.Name, playlist.Provider),
            ["Type"] = "MusicAlbum",  // Must be MusicAlbum for Jellyfin clients
            ["ServerId"] = _serverId,
            ["ChannelId"] = null,
            ["IsFolder"] = true,
            ["PremiereDate"] = playlist.CreatedDate?.ToString("o"),
            ["ProductionYear"] = playlist.CreatedDate?.Year,
            ["Genres"] = genres.ToArray(),
            ["GenreItems"] = genreItems,
            ["Artists"] = new[] { curatorName },
            ["ArtistItems"] = artistItems,
            ["AlbumArtist"] = curatorName,
            ["AlbumArtists"] = artistItems,
            ["ParentLogoItemId"] = artistId,
            ["ParentBackdropItemId"] = artistId,
            ["ParentBackdropImageTags"] = new string[0],
            ["ChildCount"] = tracks.Count,
            ["RunTimeTicks"] = totalDuration * TimeSpan.TicksPerSecond,
            ["ImageTags"] = new Dictionary<string, string>
            {
                ["Primary"] = playlist.Id
            },
            ["BackdropImageTags"] = new string[0],
            ["ParentLogoImageTag"] = artistId,
            ["ImageBlurHashes"] = new Dictionary<string, object>(),
            ["LocationType"] = "FileSystem",  // Must be FileSystem for Jellyfin to show artist albums
            ["MediaType"] = "Unknown",
            ["UserData"] = new Dictionary<string, object>
            {
                ["PlaybackPositionTicks"] = 0,
                ["PlayCount"] = 0,
                ["IsFavorite"] = false,
                ["Played"] = false,
                ["Key"] = $"{curatorName}-{playlist.Name}",
                ["ItemId"] = playlist.Id
            },
            ["ProviderIds"] = new Dictionary<string, string>
            {
                [playlist.Provider] = playlist.ExternalId
            },
            ["Children"] = tracks.Select(song =>
            {
                var item = ConvertSongToJellyfinItem(song);
                // Override ParentId and AlbumId to be the playlist ID
                // This makes all tracks appear to be from the same "album" (the playlist)
                item["ParentId"] = playlist.Id;
                item["AlbumId"] = playlist.Id;
                item["AlbumPrimaryImageTag"] = playlist.Id;
                item["ParentLogoItemId"] = playlist.Id;
                item["ParentLogoImageTag"] = playlist.Id;
                item["ParentBackdropItemId"] = playlist.Id;
                return item;
            }).ToList()
        };

        // Return album object directly (not wrapped) - same as CreateAlbumResponse
        return CreateJsonResponse(albumItem);
    }

    /// <summary>
    /// Creates a search hints response (Jellyfin search format).
    /// </summary>
    public IActionResult CreateSearchHintsResponse(
        List<Song> songs,
        List<Album> albums,
        List<Artist> artists,
        JsonDocument? nativeResponse = null,
        int? limit = null)
    {
        var searchHints = new List<Dictionary<string, object?>>();

        if (nativeResponse?.RootElement.TryGetProperty("SearchHints", out var nativeHints) == true)
        {
            foreach (var nativeHint in nativeHints.EnumerateArray())
            {
                var id = nativeHint.TryGetProperty("Id", out var idProperty)
                    ? idProperty.GetString()
                    : nativeHint.TryGetProperty("ItemId", out var itemIdProperty)
                        ? itemIdProperty.GetString()
                        : null;
                if (string.IsNullOrWhiteSpace(id)) continue;

                var preserved = JsonSerializer.Deserialize<Dictionary<string, object?>>(nativeHint.GetRawText())!;
                preserved["Id"] = id;
                preserved["ItemId"] = id;
                searchHints.Add(preserved);
            }
        }

        // Add artists first
        foreach (var artist in artists)
        {
            var item = ConvertArtistToJellyfinItem(artist);
            searchHints.Add(new Dictionary<string, object?>
            {
                ["Id"] = item["Id"],
                ["ItemId"] = item["Id"],
                ["Name"] = item["Name"],
                ["Type"] = "MusicArtist",
                ["RunTimeTicks"] = 0,
                ["PrimaryImageAspectRatio"] = item["PrimaryImageAspectRatio"],
                ["ImageTags"] = item["ImageTags"]
            });
        }

        // Add albums
        foreach (var album in albums)
        {
            var item = ConvertAlbumToJellyfinItem(album);
            searchHints.Add(new Dictionary<string, object?>
            {
                ["Id"] = item["Id"],
                ["ItemId"] = item["Id"],
                ["Name"] = item["Name"],
                ["Type"] = "MusicAlbum",
                ["Album"] = item["Name"],
                ["AlbumArtist"] = item["AlbumArtist"],
                ["ProductionYear"] = item["ProductionYear"],
                ["RunTimeTicks"] = 0,
                ["ImageTags"] = item["ImageTags"]
            });
        }

        // Add songs
        foreach (var song in songs)
        {
            var item = ConvertSongToJellyfinItem(song);
            searchHints.Add(new Dictionary<string, object?>
            {
                ["Id"] = item["Id"],
                ["ItemId"] = item["Id"],
                ["Name"] = item["Name"],
                ["Type"] = "Audio",
                ["Album"] = item["Album"],
                ["AlbumArtist"] = item["AlbumArtist"],
                ["Artists"] = item["Artists"],
                ["RunTimeTicks"] = item["RunTimeTicks"],
                ["ImageTags"] = item["ImageTags"]
            });
        }

        var limitedHints = limit.HasValue
            ? searchHints.Take(Math.Max(0, limit.Value)).ToList()
            : searchHints;

        return CreateJsonResponse(new
        {
            SearchHints = limitedHints,
            TotalRecordCount = limitedHints.Count
        });
    }

    /// <summary>
    /// Creates an error response in Jellyfin format.
    /// </summary>
    public IActionResult CreateError(int statusCode, string message)
    {
        return new ObjectResult(new
        {
            type = "https://tools.ietf.org/html/rfc7231#section-6.5.4",
            title = message,
            status = statusCode
        })
        {
            StatusCode = statusCode
        };
    }

    /// <summary>
    /// Creates a JSON response.
    /// </summary>
    public IActionResult CreateJsonResponse(object data)
    {
        return new JsonResult(data);
    }

    /// <summary>
    /// Converts a Song domain model to a Jellyfin item.
    /// </summary>
    public Dictionary<string, object?> ConvertSongToJellyfinItem(Song song)
    {
        if (song.IsLocal && JellyfinItemSnapshotHelper.TryGetClonedRawItemSnapshot(song, out var original))
        {
            return original;
        }

        // Add external/explicit labels to song titles for external tracks.
        var songTitle = song.Title;
        var artistNames = song.Artists
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var artistName = !string.IsNullOrWhiteSpace(song.Artist)
            ? song.Artist.Trim()
            : artistNames.FirstOrDefault() ?? (!string.IsNullOrWhiteSpace(song.AlbumArtist)
                ? song.AlbumArtist.Trim()
                : "Unknown artist");
        if (artistNames.Count == 0) artistNames.Add(artistName);
        var albumArtistName = !string.IsNullOrWhiteSpace(song.AlbumArtist)
            ? song.AlbumArtist.Trim()
            : artistName;
        var albumName = song.Album;
        var runTimeTicks = Math.Max(0, song.Duration ?? 0) * TimeSpan.TicksPerSecond;
        var externalBitrate = song.Bitrate is > 0 ? song.Bitrate.Value : DefaultExternalBitrate;
        var estimatedSize = song.Duration is > 0
            ? song.Duration.Value * (externalBitrate / 8L)
            : (long?)null;

        if (!song.IsLocal)
        {
            songTitle = BuildExternalSongTitle(song);

            artistName = AppendExternalSourceLabel(artistName, song.ExternalProvider);
            albumName = AppendExternalSourceLabel(albumName, song.ExternalProvider);
            artistNames = artistNames
                .Select(a => AppendExternalSourceLabel(a, song.ExternalProvider))
                .ToList();
            albumArtistName = AppendExternalSourceLabel(albumArtistName, song.ExternalProvider);
        }

        var primaryImageTag = song.IsLocal ? song.Id : $"{song.Id}-art-v2";
        Dictionary<string, object?>[] artistItems =
            artistNames.Count > 0 && song.ArtistIds.Count == artistNames.Count
            ? artistNames
                .Select((name, index) => (Name: name, Id: song.ArtistIds[index]))
                .Where(artist => !string.IsNullOrWhiteSpace(artist.Name) &&
                                 !string.IsNullOrWhiteSpace(artist.Id))
                .Select(artist => new Dictionary<string, object?>
                {
                    ["Name"] = artist.Name,
                    ["Id"] = artist.Id
                })
                .ToArray()
            : !string.IsNullOrWhiteSpace(artistName) && !string.IsNullOrWhiteSpace(song.ArtistId)
                ?
                [
                    new Dictionary<string, object?>
                    {
                        ["Name"] = artistName,
                        ["Id"] = song.ArtistId
                    }
                ]
                : [];
        Dictionary<string, object?>[] albumArtists =
            !string.IsNullOrWhiteSpace(albumArtistName) &&
            !string.IsNullOrWhiteSpace(song.ArtistId)
            ?
            [
                new Dictionary<string, object?>
                {
                    ["Name"] = albumArtistName,
                    ["Id"] = song.ArtistId
                }
            ]
            : [];

        var item = new Dictionary<string, object?>
        {
            ["Name"] = songTitle,
            ["ServerId"] = _serverId,
            ["Id"] = song.Id,
            ["PlaylistItemId"] = song.Id, // Required for playlist items
            // This capability flag prompts Jellyfin clients to call the lyrics route.
            // The route still returns 404 when every configured provider has a genuine miss.
            ["HasLyrics"] = !song.IsLocal,
            ["Container"] = "flac",
            ["PremiereDate"] = song.Year.HasValue ? $"{song.Year}-01-01T00:00:00.0000000Z" : null,
            ["DateCreated"] = song.Year.HasValue ? $"{song.Year}-01-01T00:00:00.0000000Z" : DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            ["RunTimeTicks"] = runTimeTicks,
            ["ProductionYear"] = song.Year,
            ["IndexNumber"] = song.Track,
            ["ParentIndexNumber"] = song.DiscNumber ?? 1,
            ["IsFolder"] = false,
            ["Type"] = "Audio",
            ["ChannelId"] = (object?)null,
            ["ParentId"] = song.AlbumId,
            ["Genres"] = !string.IsNullOrEmpty(song.Genre)
                ? new[] { song.Genre }
                : new string[0],
            ["GenreItems"] = !string.IsNullOrEmpty(song.Genre)
                ? new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["Name"] = song.Genre,
                        ["Id"] = $"genre-{song.Genre?.ToLowerInvariant()}"
                    }
                }
                : new Dictionary<string, object?>[0],
            ["Tags"] = new string[0],
            ["People"] = new object[0],
            ["SortName"] = songTitle,
            ["AudioInfo"] = new Dictionary<string, object?>(),
            ["ParentLogoItemId"] = song.AlbumId,
            ["ParentBackdropItemId"] = song.AlbumId,
            ["ParentBackdropImageTags"] = new string[0],
            ["UserData"] = new Dictionary<string, object>
            {
                ["PlaybackPositionTicks"] = 0,
                ["PlayCount"] = 0,
                ["IsFavorite"] = false,
                ["Played"] = false,
                ["Key"] = $"Audio-{song.Id}",
                ["ItemId"] = song.Id
            },
            ["Artists"] = artistNames.Count > 0 ? artistNames.ToArray() : new[] { artistName ?? "" },
            ["ArtistItems"] = artistItems,
            ["Album"] = albumName,
            ["AlbumId"] = song.AlbumId,
            ["AlbumPrimaryImageTag"] = song.AlbumId,
            ["PrimaryImageAspectRatio"] = 1.0,
            ["AlbumArtist"] = albumArtistName,
            ["AlbumArtists"] = albumArtists,
            ["ImageTags"] = new Dictionary<string, string>
            {
                ["Primary"] = primaryImageTag
            },
            ["BackdropImageTags"] = new string[0],
            ["ParentLogoImageTag"] = song.AlbumId,
            ["ImageBlurHashes"] = new Dictionary<string, object>(),
            ["LocationType"] = "FileSystem",
            ["MediaType"] = "Audio",
            ["NormalizationGain"] = 0.0,
            ["Path"] = $"/music/{artistName}/{albumName}/{songTitle}.flac",
            ["CanDelete"] = false,
            ["CanDownload"] = true,
            ["SupportsSync"] = true
        };

        // Add provider IDs for external content
        if (!song.IsLocal && !string.IsNullOrEmpty(song.ExternalProvider))
        {
            var supportsTranscoding = !ShouldDisableTranscoding(song.ExternalProvider);

            item["ProviderIds"] = new Dictionary<string, string>
            {
                [song.ExternalProvider] = song.ExternalId ?? ""
            };

            if (!string.IsNullOrEmpty(song.Isrc))
            {
                var providerIds = (Dictionary<string, string>)item["ProviderIds"]!;
                providerIds["ISRC"] = song.Isrc;
            }

            // Add MediaSources with complete structure matching real Jellyfin
            item["MediaSources"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["Protocol"] = "File",
                    ["Id"] = song.Id,
                    ["Path"] = $"/music/{artistName}/{albumName}/{songTitle}.flac",
                    ["DirectStreamUrl"] = $"/Audio/{Uri.EscapeDataString(song.Id)}/stream?static=true",
                    ["TranscodingUrl"] = $"/Audio/{Uri.EscapeDataString(song.Id)}/universal?container=flac&audioCodec=flac",
                    ["Type"] = "Default",
                    ["Container"] = "flac",
                    ["Size"] = estimatedSize,
                    ["Name"] = song.Title,
                    ["IsRemote"] = false,
                    ["ETag"] = song.Id, // Use song ID as ETag
                    ["RunTimeTicks"] = runTimeTicks,
                    ["ReadAtNativeFramerate"] = false,
                    ["IgnoreDts"] = false,
                    ["IgnoreIndex"] = false,
                    ["GenPtsInput"] = false,
                    ["SupportsTranscoding"] = supportsTranscoding,
                    ["SupportsDirectStream"] = true,
                    ["SupportsDirectPlay"] = true,
                    ["IsInfiniteStream"] = false,
                    ["UseMostCompatibleTranscodingProfile"] = false,
                    ["RequiresOpening"] = false,
                    ["RequiresClosing"] = false,
                    ["RequiresLooping"] = false,
                    ["SupportsProbing"] = true,
                    ["MediaStreams"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["Codec"] = "flac",
                            ["TimeBase"] = "1/44100",
                            ["VideoRange"] = "Unknown",
                            ["VideoRangeType"] = "Unknown",
                            ["AudioSpatialFormat"] = "None",
                            ["LocalizedDefault"] = "Default",
                            ["LocalizedExternal"] = "External",
                            ["DisplayTitle"] = "FLAC - Stereo",
                            ["IsInterlaced"] = false,
                            ["IsAVC"] = false,
                            ["ChannelLayout"] = "stereo",
                            ["BitRate"] = externalBitrate,
                            ["BitDepth"] = 16,
                            ["Channels"] = 2,
                            ["SampleRate"] = 44100,
                            ["IsDefault"] = false,
                            ["IsForced"] = false,
                            ["IsHearingImpaired"] = false,
                            ["Type"] = "Audio",
                            ["Index"] = 0,
                            ["IsExternal"] = false,
                            ["IsTextSubtitleStream"] = false,
                            ["SupportsExternalStream"] = false,
                            ["Level"] = 0
                        }
                    },
                    ["MediaAttachments"] = new List<object>(),
                    ["Formats"] = new List<string>(),
                    ["Bitrate"] = externalBitrate,
                    ["RequiredHttpHeaders"] = new Dictionary<string, string>(),
                    ["TranscodingSubProtocol"] = "http",
                    ["DefaultAudioStreamIndex"] = 0,
                    ["HasSegments"] = false
                }
            };
        }
        else if (song.IsLocal && song.JellyfinMetadata != null && song.JellyfinMetadata.ContainsKey("MediaSources"))
        {
            // Use preserved Jellyfin metadata for local tracks to maintain bitrate info
            item["MediaSources"] = song.JellyfinMetadata["MediaSources"];
        }

        return item;
    }

    private static string BuildExternalSongTitle(Song song)
    {
        var title = AppendExternalSourceLabel(song.Title, song.ExternalProvider);

        if (song.ExplicitContentLyrics == 1)
        {
            title = $"{title} [E]";
        }

        return title;
    }

    private static bool ShouldDisableTranscoding(string provider)
    {
        return provider.Equals("deezer", StringComparison.OrdinalIgnoreCase) ||
               provider.Equals("qobuz", StringComparison.OrdinalIgnoreCase) ||
               provider.Equals("squidwtf", StringComparison.OrdinalIgnoreCase);
    }

    internal static string AppendExternalSourceLabel(string value, string? provider)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var label = GetExternalSourceLabel(provider);
        return value.EndsWith($" {label}", StringComparison.Ordinal)
            ? value
            : $"{value} {label}";
    }

    private static string BuildExternalPlaylistName(string playlistName, string? provider)
    {
        return $"{playlistName} [{GetExternalSourceCode(provider)}/P]";
    }

    private static string GetExternalSourceLabel(string? provider)
    {
        return $"[{GetExternalSourceCode(provider)}]";
    }

    private static string GetExternalSourceCode(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return "EXT";

        var normalized = provider.Trim().ToLowerInvariant();
        if (normalized.StartsWith("spotiflac-", StringComparison.Ordinal))
            normalized = normalized["spotiflac-".Length..];

        var known = normalized switch
        {
            "deezer" => "D",
            "qobuz" or "qobuz-web" => "Q",
            "applemusic" or "apple-download" => "AM",
            "apple-music" or "apple-musickit" => "AM",
            "amazon" or "amazonmusic" or "amazon-music" => "AmM",
            "spotify" => "S",
            "tidal" or "tidal-web" => "T",
            "squidwtf" => "Sq",
            "soundcloud" => "So",
            "ytmusic" or "ytmusic-spotiflac" or "youtube-music" => "YM",
            "ext" or "external" or "unknown" => "EXT",
            _ => null
        };
        if (known != null) return known;

        var words = normalized.Split(['-', '_', ' ', '.'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0
            ? "EXT"
            : string.Concat(words.Select(word => char.ToUpperInvariant(word[0])));
    }

    /// <summary>
    /// Converts an Album domain model to a Jellyfin item.
    /// </summary>
    public Dictionary<string, object?> ConvertAlbumToJellyfinItem(Album album)
    {
        var albumName = album.Title;
        var artistName = string.IsNullOrWhiteSpace(album.Artist)
            ? "Unknown artist"
            : album.Artist.Trim();
        if (!album.IsLocal)
        {
            albumName = AppendExternalSourceLabel(album.Title, album.ExternalProvider);
            artistName = AppendExternalSourceLabel(artistName, album.ExternalProvider);
        }

        Dictionary<string, object?>[] albumArtistItems =
            !string.IsNullOrWhiteSpace(artistName) &&
            !string.IsNullOrWhiteSpace(album.ArtistId)
            ?
            [
                new Dictionary<string, object?>
                {
                    ["Name"] = artistName,
                    ["Id"] = album.ArtistId
                }
            ]
            : [];
        var item = new Dictionary<string, object?>
        {
            ["Name"] = albumName,
            ["ServerId"] = _serverId,
            ["Id"] = album.Id,
            ["PremiereDate"] = album.Year.HasValue ? $"{album.Year}-01-01T05:00:00.0000000Z" : null,
            ["DateCreated"] = album.Year.HasValue ? $"{album.Year}-01-01T05:00:00.0000000Z" : "1970-01-01T00:00:00.0000000Z",
            ["ChannelId"] = (object?)null,
            ["Genres"] = !string.IsNullOrEmpty(album.Genre)
                ? new[] { album.Genre }
                : new string[0],
            ["RunTimeTicks"] = 0, // Could calculate from songs
            ["ProductionYear"] = album.Year,
            ["IsFolder"] = true,
            ["Type"] = "MusicAlbum",
            ["SortName"] = albumName,
            ["BasicSyncInfo"] = new Dictionary<string, object?>(),
            ["GenreItems"] = !string.IsNullOrEmpty(album.Genre)
                ? new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["Name"] = album.Genre,
                        ["Id"] = $"genre-{album.Genre?.ToLowerInvariant()}"
                    }
                }
                : new Dictionary<string, object?>[0],
            ["ParentLogoItemId"] = album.ArtistId ?? album.Id,
            ["ParentBackdropItemId"] = album.ArtistId ?? album.Id,
            ["ParentBackdropImageTags"] = new string[0],
            ["UserData"] = new Dictionary<string, object>
            {
                ["PlaybackPositionTicks"] = 0,
                ["PlayCount"] = 0,
                ["IsFavorite"] = false,
                ["Played"] = false,
                ["Key"] = $"{album.Artist}-{album.Title}",
                ["ItemId"] = album.Id
            },
            ["Artists"] = new[] { artistName },
            ["ArtistItems"] = albumArtistItems,
            ["AlbumArtist"] = artistName,
            ["AlbumArtists"] = albumArtistItems,
            ["ImageTags"] = new Dictionary<string, string>
            {
                ["Primary"] = album.Id
            },
            ["BackdropImageTags"] = new string[0],
            ["ParentLogoImageTag"] = album.ArtistId ?? album.Id,
            ["ImageBlurHashes"] = new Dictionary<string, object>(),
            ["LocationType"] = "FileSystem",
            ["MediaType"] = "Unknown",
            ["ChildCount"] = album.SongCount ?? album.Songs.Count
        };

        // Add provider IDs for external content
        if (!album.IsLocal && !string.IsNullOrEmpty(album.ExternalProvider))
        {
            item["ProviderIds"] = new Dictionary<string, string>
            {
                [album.ExternalProvider] = album.ExternalId ?? ""
            };
        }

        return item;
    }

    /// <summary>
    /// Converts an Artist domain model to a Jellyfin item.
    /// </summary>
    public Dictionary<string, object?> ConvertArtistToJellyfinItem(Artist artist)
    {
        var artistName = string.IsNullOrWhiteSpace(artist.Name)
            ? "Unknown artist"
            : artist.Name.Trim();
        if (!artist.IsLocal)
        {
            artistName = AppendExternalSourceLabel(artistName, artist.ExternalProvider);
        }

        var item = new Dictionary<string, object?>
        {
            ["Name"] = artistName,
            ["ServerId"] = _serverId,
            ["Id"] = artist.Id,
            ["ChannelId"] = (object?)null,
            ["Genres"] = new string[0], // Artists aggregate genres from albums/tracks
            ["RunTimeTicks"] = 0,
            ["IsFolder"] = true,
            ["Type"] = "MusicArtist",
            ["SortName"] = artistName,
            ["PrimaryImageAspectRatio"] = 1.0,
            ["BasicSyncInfo"] = new Dictionary<string, object?>(),
            ["GenreItems"] = new Dictionary<string, object?>[0],
            ["UserData"] = new Dictionary<string, object>
            {
                ["PlaybackPositionTicks"] = 0,
                ["PlayCount"] = 0,
                ["IsFavorite"] = false,
                ["Played"] = false,
                ["Key"] = $"Artist-{artist.Name}",
                ["ItemId"] = artist.Id
            },
            ["ImageTags"] = new Dictionary<string, string>
            {
                ["Primary"] = artist.Id
            },
            ["BackdropImageTags"] = new string[0],
            ["ImageBlurHashes"] = new Dictionary<string, object>(),
            ["LocationType"] = "FileSystem",
            ["MediaType"] = "Unknown",
            ["AlbumCount"] = artist.AlbumCount ?? 0
        };

        // Add provider IDs for external content
        if (!artist.IsLocal && !string.IsNullOrEmpty(artist.ExternalProvider))
        {
            item["ProviderIds"] = new Dictionary<string, string>
            {
                [artist.ExternalProvider] = artist.ExternalId ?? ""
            };
        }

        return item;
    }

    /// <summary>
    /// Converts a Jellyfin JSON element to a dictionary.
    /// </summary>
    public object ConvertJellyfinJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ConvertJellyfinJsonElement(p.Value)),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(ConvertJellyfinJsonElement)
                .ToList(),
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null!,
            _ => element.ToString()
        };
    }

    /// <summary>
    /// Converts an ExternalPlaylist to a Jellyfin playlist item.
    /// </summary>
    public Dictionary<string, object?> ConvertPlaylistToJellyfinItem(ExternalPlaylist playlist)
    {
        var curatorName = !string.IsNullOrEmpty(playlist.CuratorName)
            ? playlist.CuratorName
            : playlist.Provider;

        var item = new Dictionary<string, object?>
        {
            ["Name"] = playlist.Name,
            ["ServerId"] = _serverId,
            ["Id"] = playlist.Id,
            ["ChannelId"] = (object?)null,
            ["Genres"] = new string[0], // Playlists aggregate genres from tracks
            ["RunTimeTicks"] = playlist.Duration * TimeSpan.TicksPerSecond,
            ["IsFolder"] = true,
            ["Type"] = "Playlist",
            ["GenreItems"] = new Dictionary<string, object?>[0],
            ["UserData"] = new Dictionary<string, object>
            {
                ["PlaybackPositionTicks"] = 0,
                ["PlayCount"] = 0,
                ["IsFavorite"] = false,
                ["Played"] = false,
                ["Key"] = playlist.Id,
                ["ItemId"] = playlist.Id
            },
            ["ChildCount"] = playlist.TrackCount,
            ["ImageTags"] = new Dictionary<string, string>
            {
                ["Primary"] = playlist.Id
            },
            ["BackdropImageTags"] = new string[0],
            ["ImageBlurHashes"] = new Dictionary<string, object>(),
            ["LocationType"] = "FileSystem",
            ["MediaType"] = "Audio",
            ["ProviderIds"] = new Dictionary<string, string>
            {
                [playlist.Provider] = playlist.ExternalId
            }
        };

        if (playlist.CreatedDate.HasValue)
        {
            item["PremiereDate"] = playlist.CreatedDate.Value.ToString("o");
            item["ProductionYear"] = playlist.CreatedDate.Value.Year;
        }

        return item;
    }
    public Dictionary<string, object?> ConvertPlaylistToAlbumItem(ExternalPlaylist playlist)
    {
        var curatorName = !string.IsNullOrEmpty(playlist.CuratorName)
            ? playlist.CuratorName
            : playlist.Provider;

        var item = new Dictionary<string, object?>
        {
            ["Name"] = BuildExternalPlaylistName(playlist.Name, playlist.Provider),
            ["ServerId"] = _serverId,
            ["Id"] = playlist.Id,
            ["ChannelId"] = (object?)null,
            ["Genres"] = new string[0],
            ["RunTimeTicks"] = playlist.Duration * TimeSpan.TicksPerSecond,
            ["IsFolder"] = true,
            ["Type"] = "MusicAlbum",
            ["SortName"] = BuildExternalPlaylistName(playlist.Name, playlist.Provider),
            ["DateCreated"] = playlist.CreatedDate.HasValue
                ? playlist.CreatedDate.Value.ToString("o")
                : "1970-01-01T00:00:00.0000000Z",
            ["BasicSyncInfo"] = new Dictionary<string, object?>(),
            ["GenreItems"] = new Dictionary<string, object?>[0],
            ["UserData"] = new Dictionary<string, object>
            {
                ["PlaybackPositionTicks"] = 0,
                ["PlayCount"] = 0,
                ["IsFavorite"] = false,
                ["Played"] = false,
                ["Key"] = playlist.Id,
                ["ItemId"] = playlist.Id
            },
            ["ChildCount"] = playlist.TrackCount,
            ["Artists"] = new[] { curatorName },
            ["ArtistItems"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["Name"] = curatorName,
                    ["Id"] = $"ext-{playlist.Provider}-curator-{curatorName.ToLowerInvariant().Replace(" ", "-")}"
                }
            },
            ["AlbumArtist"] = curatorName,
            ["AlbumArtists"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["Name"] = curatorName,
                    ["Id"] = $"ext-{playlist.Provider}-curator-{curatorName.ToLowerInvariant().Replace(" ", "-")}"
                }
            },
            ["ImageTags"] = new Dictionary<string, string>
            {
                ["Primary"] = playlist.Id
            },
            ["BackdropImageTags"] = new string[0],
            ["ImageBlurHashes"] = new Dictionary<string, object>(),
            ["LocationType"] = "FileSystem",
            ["MediaType"] = "Unknown",
            ["ProviderIds"] = new Dictionary<string, string>
            {
                [playlist.Provider] = playlist.ExternalId
            }
        };

        if (playlist.CreatedDate.HasValue)
        {
            item["PremiereDate"] = playlist.CreatedDate.Value.ToString("o");
            item["ProductionYear"] = playlist.CreatedDate.Value.Year;
        }

        return item;
    }
}
