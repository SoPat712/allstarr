using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;

namespace allstarr.Services.Jellyfin;

/// <summary>
/// Builds Jellyfin-compatible API responses.
/// </summary>
public class JellyfinResponseBuilder
{
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
                ["Name"] = $"{playlist.Name} [S/P]",  // Label as playlist
                ["Type"] = "MusicAlbum",  // Must be MusicAlbum for Jellyfin clients
                ["ServerId"] = "allstarr",
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
        List<Artist> artists)
    {
        var searchHints = new List<Dictionary<string, object?>>();

        // Add artists first
        foreach (var artist in artists)
        {
            searchHints.Add(new Dictionary<string, object?>
            {
                ["Id"] = artist.Id,
                ["Name"] = artist.Name,
                ["Type"] = "MusicArtist",
                ["RunTimeTicks"] = 0,
                ["PrimaryImageAspectRatio"] = 1.0,
                ["ImageTags"] = new Dictionary<string, string>
                {
                    ["Primary"] = artist.Id
                }
            });
        }

        // Add albums
        foreach (var album in albums)
        {
            searchHints.Add(new Dictionary<string, object?>
            {
                ["Id"] = album.Id,
                ["Name"] = album.Title,
                ["Type"] = "MusicAlbum",
                ["Album"] = album.Title,
                ["AlbumArtist"] = album.Artist,
                ["ProductionYear"] = album.Year,
                ["RunTimeTicks"] = 0,
                ["ImageTags"] = new Dictionary<string, string>
                {
                    ["Primary"] = album.Id
                }
            });
        }

        // Add songs
        foreach (var song in songs)
        {
            searchHints.Add(new Dictionary<string, object?>
            {
                ["Id"] = song.Id,
                ["Name"] = song.Title,
                ["Type"] = "Audio",
                ["Album"] = song.Album,
                ["AlbumArtist"] = song.Artist,
                ["Artists"] = song.Artists.Count > 0 ? song.Artists.ToArray() : new[] { song.Artist },
                ["RunTimeTicks"] = (song.Duration ?? 0) * TimeSpan.TicksPerSecond,
                ["ImageTags"] = new Dictionary<string, string>
                {
                    ["Primary"] = song.Id
                }
            });
        }

        return CreateJsonResponse(new
        {
            SearchHints = searchHints,
            TotalRecordCount = searchHints.Count
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
        // Add " [S]" suffix to external song titles (S = streaming source)
        var songTitle = song.Title;
        var artistName = song.Artist;
        var albumName = song.Album;
        var artistNames = song.Artists.ToList();

        if (!song.IsLocal)
        {
            songTitle = $"{song.Title} [S]";

            // Also add [S] to artist and album names for consistency
            if (!string.IsNullOrEmpty(artistName) && !artistName.EndsWith(" [S]"))
            {
                artistName = $"{artistName} [S]";
            }

            if (!string.IsNullOrEmpty(albumName) && !albumName.EndsWith(" [S]"))
            {
                albumName = $"{albumName} [S]";
            }

            // Add [S] to all artist names in the list
            artistNames = artistNames.Select(a =>
                !string.IsNullOrEmpty(a) && !a.EndsWith(" [S]") ? $"{a} [S]" : a
            ).ToList();
        }

        var item = new Dictionary<string, object?>
        {
            ["Name"] = songTitle,
            ["ServerId"] = "allstarr",
            ["Id"] = song.Id,
            ["PlaylistItemId"] = song.Id, // Required for playlist items
            ["HasLyrics"] = false, // Could be enhanced to check if lyrics exist
            ["Container"] = "flac",
            ["PremiereDate"] = song.Year.HasValue ? $"{song.Year}-01-01T00:00:00.0000000Z" : null,
            ["DateCreated"] = song.Year.HasValue ? $"{song.Year}-01-01T00:00:00.0000000Z" : DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            ["RunTimeTicks"] = (song.Duration ?? 0) * TimeSpan.TicksPerSecond,
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
            ["ArtistItems"] = artistNames.Count > 0 && song.ArtistIds.Count == artistNames.Count
                ? artistNames.Select((name, index) => new Dictionary<string, object?>
                {
                    ["Name"] = name,
                    ["Id"] = song.ArtistIds[index]
                }).ToArray()
                : new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["Id"] = song.ArtistId ?? song.Id,
                        ["Name"] = artistName ?? ""
                    }
                },
            ["Album"] = albumName,
            ["AlbumId"] = song.AlbumId ?? song.Id,
            ["AlbumPrimaryImageTag"] = song.AlbumId ?? song.Id,
            ["AlbumArtist"] = song.AlbumArtist ?? artistName,
            ["AlbumArtists"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["Name"] = song.AlbumArtist ?? artistName ?? "",
                    ["Id"] = song.ArtistId ?? song.Id
                }
            },
            ["ImageTags"] = new Dictionary<string, string>
            {
                ["Primary"] = song.Id
            },
            ["BackdropImageTags"] = new string[0],
            ["ParentLogoImageTag"] = song.AlbumId ?? song.Id,
            ["ImageBlurHashes"] = new Dictionary<string, object>(),
            ["LocationType"] = "FileSystem",
            ["MediaType"] = "Audio",
            ["NormalizationGain"] = 0.0,
            ["Path"] = $"/music/{song.Artist}/{song.Album}/{song.Title}.flac",
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
                    ["Path"] = $"/music/{song.Artist}/{song.Album}/{song.Title}.flac",
                    ["Type"] = "Default",
                    ["Container"] = "flac",
                    ["Size"] = (song.Duration ?? 180) * 1337 * 128,
                    ["Name"] = song.Title,
                    ["IsRemote"] = false,
                    ["ETag"] = song.Id, // Use song ID as ETag
                    ["RunTimeTicks"] = (song.Duration ?? 180) * 10000000L,
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
                            ["BitRate"] = 1337000,
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
                    ["Bitrate"] = 1337000,
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

    private static bool ShouldDisableTranscoding(string provider)
    {
        return provider.Equals("deezer", StringComparison.OrdinalIgnoreCase) ||
               provider.Equals("qobuz", StringComparison.OrdinalIgnoreCase) ||
               provider.Equals("squidwtf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converts an Album domain model to a Jellyfin item.
    /// </summary>
    public Dictionary<string, object?> ConvertAlbumToJellyfinItem(Album album)
    {
        // Add " [S]" suffix to external album names (S = streaming source)
        var albumName = album.Title;
        if (!album.IsLocal)
        {
            albumName = $"{album.Title} [S]";
        }

        var item = new Dictionary<string, object?>
        {
            ["Name"] = albumName,
            ["ServerId"] = "allstarr",
            ["Id"] = album.Id,
            ["PremiereDate"] = album.Year.HasValue ? $"{album.Year}-01-01T05:00:00.0000000Z" : null,
            ["ChannelId"] = (object?)null,
            ["Genres"] = !string.IsNullOrEmpty(album.Genre)
                ? new[] { album.Genre }
                : new string[0],
            ["RunTimeTicks"] = 0, // Could calculate from songs
            ["ProductionYear"] = album.Year,
            ["IsFolder"] = true,
            ["Type"] = "MusicAlbum",
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
            ["Artists"] = new[] { album.Artist },
            ["ArtistItems"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["Name"] = album.Artist,
                    ["Id"] = album.ArtistId ?? album.Id
                }
            },
            ["AlbumArtist"] = album.Artist,
            ["AlbumArtists"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["Name"] = album.Artist,
                    ["Id"] = album.ArtistId ?? album.Id
                }
            },
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
        // Add " [S]" suffix to external artist names (S = streaming source)
        var artistName = artist.Name;
        if (!artist.IsLocal)
        {
            artistName = $"{artist.Name} [S]";
        }

        var item = new Dictionary<string, object?>
        {
            ["Name"] = artistName,
            ["ServerId"] = "allstarr",
            ["Id"] = artist.Id,
            ["ChannelId"] = (object?)null,
            ["Genres"] = new string[0], // Artists aggregate genres from albums/tracks
            ["RunTimeTicks"] = 0,
            ["IsFolder"] = true,
            ["Type"] = "MusicArtist",
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
            ["ServerId"] = "allstarr",
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
            ["Name"] = $"{playlist.Name} [S/P]",
            ["ServerId"] = "allstarr",
            ["Id"] = playlist.Id,
            ["ChannelId"] = (object?)null,
            ["Genres"] = new string[0],
            ["RunTimeTicks"] = playlist.Duration * TimeSpan.TicksPerSecond,
            ["IsFolder"] = true,
            ["Type"] = "MusicAlbum",
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
