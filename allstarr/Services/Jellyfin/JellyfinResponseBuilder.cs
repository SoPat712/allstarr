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
        
        var albumItem = new Dictionary<string, object?>
        {
            ["Id"] = playlist.Id,
            ["Name"] = playlist.Name,
            ["Type"] = "Playlist",
            ["AlbumArtist"] = curatorName,
            ["Genres"] = new[] { "Playlist" },
            ["ChildCount"] = tracks.Count,
            ["RunTimeTicks"] = totalDuration * TimeSpan.TicksPerSecond,
            ["ImageTags"] = new Dictionary<string, string>
            {
                ["Primary"] = playlist.Id
            },
            ["ProviderIds"] = new Dictionary<string, string>
            {
                [playlist.Provider] = playlist.ExternalId
            },
            ["Children"] = tracks.Select(ConvertSongToJellyfinItem).ToList()
        };
        
        if (playlist.CreatedDate.HasValue)
        {
            albumItem["PremiereDate"] = playlist.CreatedDate.Value.ToString("o");
            albumItem["ProductionYear"] = playlist.CreatedDate.Value.Year;
        }
        
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
                ["Artists"] = new[] { song.Artist },
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
        var item = new Dictionary<string, object?>
        {
            ["Id"] = song.Id,
            ["Name"] = song.Title,
            ["ServerId"] = "allstarr",
            ["Type"] = "Audio",
            ["MediaType"] = "Audio",
            ["IsFolder"] = false,
            ["Album"] = song.Album,
            ["AlbumId"] = song.AlbumId ?? song.Id,
            ["AlbumArtist"] = song.AlbumArtist ?? song.Artist,
            ["Artists"] = new[] { song.Artist },
            ["ArtistItems"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["Id"] = song.ArtistId ?? song.Id,
                    ["Name"] = song.Artist
                }
            },
            ["IndexNumber"] = song.Track,
            ["ParentIndexNumber"] = song.DiscNumber ?? 1,
            ["ProductionYear"] = song.Year,
            ["RunTimeTicks"] = (song.Duration ?? 0) * TimeSpan.TicksPerSecond,
            ["ImageTags"] = new Dictionary<string, string>
            {
                ["Primary"] = song.Id
            },
            ["BackdropImageTags"] = new string[0],
            ["ImageBlurHashes"] = new Dictionary<string, object>(),
            ["LocationType"] = "FileSystem", // External content appears as local files to clients
            ["Path"] = $"/music/{song.Artist}/{song.Album}/{song.Title}.flac", // Fake path for client compatibility
            ["ChannelId"] = (object?)null, // Match Jellyfin structure
            ["UserData"] = new Dictionary<string, object>
            {
                ["PlaybackPositionTicks"] = 0,
                ["PlayCount"] = 0,
                ["IsFavorite"] = false,
                ["Played"] = false,
                ["Key"] = $"Audio-{song.Id}"
            },
            ["CanDownload"] = true,
            ["SupportsSync"] = true
        };

        // Add provider IDs for external content
        if (!song.IsLocal && !string.IsNullOrEmpty(song.ExternalProvider))
        {
            item["ProviderIds"] = new Dictionary<string, string>
            {
                [song.ExternalProvider] = song.ExternalId ?? ""
            };
            
            if (!string.IsNullOrEmpty(song.Isrc))
            {
                var providerIds = (Dictionary<string, string>)item["ProviderIds"]!;
                providerIds["ISRC"] = song.Isrc;
            }
        }

        if (!string.IsNullOrEmpty(song.Genre))
        {
            item["Genres"] = new[] { song.Genre };
        }

        return item;
    }

    /// <summary>
    /// Converts an Album domain model to a Jellyfin item.
    /// </summary>
    public Dictionary<string, object?> ConvertAlbumToJellyfinItem(Album album)
    {
        // Add " - S" suffix to external album names (S = SquidWTF)
        var albumName = album.Title;
        if (!album.IsLocal)
        {
            albumName = $"{album.Title} - S";
        }
        
        var item = new Dictionary<string, object?>
        {
            ["Id"] = album.Id,
            ["Name"] = albumName,
            ["ServerId"] = "allstarr",
            ["Type"] = "MusicAlbum",
            ["IsFolder"] = true,
            ["AlbumArtist"] = album.Artist,
            ["AlbumArtists"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["Id"] = album.ArtistId ?? album.Id,
                    ["Name"] = album.Artist
                }
            },
            ["ProductionYear"] = album.Year,
            ["ChildCount"] = album.SongCount ?? album.Songs.Count,
            ["ImageTags"] = new Dictionary<string, string>
            {
                ["Primary"] = album.Id
            },
            ["BackdropImageTags"] = new string[0],
            ["ImageBlurHashes"] = new Dictionary<string, object>(),
            ["LocationType"] = "FileSystem",
            ["MediaType"] = (object?)null,
            ["ChannelId"] = (object?)null,
            ["CollectionType"] = (object?)null,
            ["UserData"] = new Dictionary<string, object>
            {
                ["PlaybackPositionTicks"] = 0,
                ["PlayCount"] = 0,
                ["IsFavorite"] = false,
                ["Played"] = false,
                ["Key"] = album.Id
            }
        };

        // Add provider IDs for external content
        if (!album.IsLocal && !string.IsNullOrEmpty(album.ExternalProvider))
        {
            item["ProviderIds"] = new Dictionary<string, string>
            {
                [album.ExternalProvider] = album.ExternalId ?? ""
            };
        }

        if (!string.IsNullOrEmpty(album.Genre))
        {
            item["Genres"] = new[] { album.Genre };
        }

        return item;
    }

    /// <summary>
    /// Converts an Artist domain model to a Jellyfin item.
    /// </summary>
    public Dictionary<string, object?> ConvertArtistToJellyfinItem(Artist artist)
    {
        // Add " - S" suffix to external artist names (S = SquidWTF)
        var artistName = artist.Name;
        if (!artist.IsLocal)
        {
            artistName = $"{artist.Name} - S";
        }
        
        var item = new Dictionary<string, object?>
        {
            ["Id"] = artist.Id,
            ["Name"] = artistName,
            ["ServerId"] = "allstarr",
            ["Type"] = "MusicArtist",
            ["IsFolder"] = true,
            ["AlbumCount"] = artist.AlbumCount ?? 0,
            ["ImageTags"] = new Dictionary<string, string>
            {
                ["Primary"] = artist.Id
            },
            ["BackdropImageTags"] = new string[0],
            ["ImageBlurHashes"] = new Dictionary<string, object>(),
            ["LocationType"] = "FileSystem", // External content appears as local files to clients
            ["MediaType"] = (object?)null, // Match Jellyfin structure
            ["ChannelId"] = (object?)null, // Match Jellyfin structure
            ["CollectionType"] = (object?)null, // Match Jellyfin structure
            ["UserData"] = new Dictionary<string, object>
            {
                ["PlaybackPositionTicks"] = 0,
                ["PlayCount"] = 0,
                ["IsFavorite"] = false,
                ["Played"] = false,
                ["Key"] = artist.Id
            }
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
    /// Converts an ExternalPlaylist to a Jellyfin album item.
    /// </summary>
    public Dictionary<string, object?> ConvertPlaylistToJellyfinItem(ExternalPlaylist playlist)
    {
        var curatorName = !string.IsNullOrEmpty(playlist.CuratorName) 
            ? playlist.CuratorName 
            : playlist.Provider;
        
        var item = new Dictionary<string, object?>
        {
            ["Id"] = playlist.Id,
            ["Name"] = playlist.Name,
            ["ServerId"] = "allstarr",
            ["Type"] = "Playlist",
            ["IsFolder"] = true,
            ["AlbumArtist"] = curatorName,
            ["Genres"] = new[] { "Playlist" },
            ["ChildCount"] = playlist.TrackCount,
            ["ImageTags"] = new Dictionary<string, string>
            {
                ["Primary"] = playlist.Id
            },
            ["BackdropImageTags"] = new string[0],
            ["ImageBlurHashes"] = new Dictionary<string, object>(),
            ["LocationType"] = "FileSystem",
            ["MediaType"] = (object?)null,
            ["ChannelId"] = (object?)null,
            ["CollectionType"] = (object?)null,
            ["ProviderIds"] = new Dictionary<string, string>
            {
                [playlist.Provider] = playlist.ExternalId
            },
            ["UserData"] = new Dictionary<string, object>
            {
                ["PlaybackPositionTicks"] = 0,
                ["PlayCount"] = 0,
                ["IsFavorite"] = false,
                ["Played"] = false,
                ["Key"] = playlist.Id
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
            ["Id"] = playlist.Id,
            ["Name"] = playlist.Name,
            ["Type"] = "Playlist",
            ["IsFolder"] = true,
            ["AlbumArtist"] = curatorName,
            ["ChildCount"] = playlist.TrackCount,
            ["RunTimeTicks"] = playlist.Duration * TimeSpan.TicksPerSecond,
            ["Genres"] = new[] { "Playlist" },
            ["ImageTags"] = new Dictionary<string, string>
            {
                ["Primary"] = playlist.Id
            },
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
