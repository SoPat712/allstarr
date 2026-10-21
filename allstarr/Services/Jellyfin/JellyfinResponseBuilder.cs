using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;
using allstarr.Core.Protocols;

namespace allstarr.Services.Jellyfin;

public class JellyfinResponseBuilder
{
    public Dictionary<string, object?> ConvertFavoriteToJellyfinItem(allstarr.Core.Favorites.FavoriteViewItem favorite)
    {
        var item = favorite.Resource.ResourceKind switch
        {
            allstarr.Core.Capabilities.ProviderResourceKind.Track => ConvertSongToJellyfinItem(favorite.Song()),
            allstarr.Core.Capabilities.ProviderResourceKind.Album => ConvertAlbumToJellyfinItem(favorite.Album()),
            allstarr.Core.Capabilities.ProviderResourceKind.Artist => ConvertArtistToJellyfinItem(favorite.Artist()),
            allstarr.Core.Capabilities.ProviderResourceKind.Playlist => ConvertPlaylistToJellyfinItem(favorite.Playlist()),
            _ => throw new ArgumentException("The saved favorite resource kind is unsupported.", nameof(favorite))
        };
        if (item.GetValueOrDefault("UserData") is IDictionary<string, object?> userData)
            userData["IsFavorite"] = true;
        return item;
    }

    public const string ServerIdItemKey = "allstarr.jellyfin-server-id";

    private string? ServerId => _httpContexts?.HttpContext?.Items[ServerIdItemKey] as string;
    private readonly IHttpContextAccessor? _httpContexts;
    private readonly IPlaybackDeliveryActivitySource? _playbackActivity;

    public JellyfinResponseBuilder(IOptions<JellyfinSettings>? settings = null, IHttpContextAccessor? httpContexts = null,
        IPlaybackDeliveryActivitySource? playbackActivity = null)
    {
        _httpContexts = httpContexts;
        _playbackActivity = playbackActivity;
    }

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

    public IActionResult CreateSongResponse(Song song)
    {
        return CreateJsonResponse(ConvertSongToJellyfinItem(song,
            ExternalTrackPresentation.Preferences(_httpContexts?.HttpContext), null, includeAllFields: true));
    }

    public IActionResult CreateAlbumResponse(Album album)
    {
        var albumItem = BuildAlbumItem(album, includeAllFields: true);

        if (album.IsLocal && album.Songs.Count > 0)
        {
            albumItem["Children"] = album.Songs.Select(ConvertSongToJellyfinItem).ToList();
        }

        return CreateJsonResponse(albumItem);
    }

    public IActionResult CreateArtistResponse(Artist artist, List<Album> albums)
    {
        var artistItem = BuildArtistItem(artist, includeAllFields: true);
        artistItem["AlbumCount"] = artist.AlbumCount ?? albums.Count;
        if (artist.IsLocal) artistItem["Albums"] = albums.Select(ConvertAlbumToJellyfinItem).ToList();

        return CreateJsonResponse(artistItem);
    }

    // Jellyfin clients require playlists projected as MusicAlbum/FileSystem containers.
    public IActionResult CreatePlaylistAsAlbumResponse(ExternalPlaylist playlist, List<Song> tracks)
    {
        var totalDuration = tracks.Sum(s => s.Duration ?? 0);

        var curatorName = !string.IsNullOrEmpty(playlist.CuratorName)
            ? playlist.CuratorName
            : playlist.Provider;

        var artistId = $"ext-{playlist.Provider}-curator-{curatorName.ToLowerInvariant().Replace(" ", "-")}";
        var artistItems = new[]
        {
                new Dictionary<string, object>
                {
                    ["Name"] = curatorName,
                    ["Id"] = artistId
                }
            };

        var genres = tracks
            .Where(s => !string.IsNullOrEmpty(s.Genre))
            .Select(s => s.Genre!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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
            ["Type"] = "MusicAlbum",
            ["ServerId"] = ServerId,
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
            ["ImageBlurHashes"] = null,
            ["LocationType"] = "FileSystem",
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
            }
        };

        return CreateJsonResponse(ApplyExternalFields(albumItem, includeAllFields: true));
    }

    private Dictionary<string, object?> ApplyExternalFields(Dictionary<string, object?> item, bool includeAllFields = false) =>
        JellyfinItemFields.ForRequest(_httpContexts?.HttpContext, includeAllFields: includeAllFields).Apply(item);

    public Dictionary<string, object?> ConvertArtistToSearchHint(Artist artist)
    {
        var item = ConvertArtistToJellyfinItem(artist);
        return new Dictionary<string, object?>
        {
            ["Id"] = item["Id"],
            ["ItemId"] = item["Id"],
            ["Name"] = item["Name"],
            ["Type"] = "MusicArtist",
            ["RunTimeTicks"] = 0,
            ["PrimaryImageAspectRatio"] = 1.0,
            ["ImageTags"] = item["ImageTags"]
        };
    }

    public Dictionary<string, object?> ConvertAlbumToSearchHint(Album album)
    {
        var item = ConvertAlbumToJellyfinItem(album);
        return new Dictionary<string, object?>
        {
            ["Id"] = item["Id"],
            ["ItemId"] = item["Id"],
            ["Name"] = item["Name"],
            ["Type"] = "MusicAlbum",
            ["Album"] = item["Name"],
            ["AlbumArtist"] = item["AlbumArtist"],
            ["ProductionYear"] = item.GetValueOrDefault("ProductionYear"),
            ["RunTimeTicks"] = 0,
            ["ImageTags"] = item["ImageTags"]
        };
    }

    public Dictionary<string, object?> ConvertSongToSearchHint(Song song)
    {
        var item = ConvertSongToJellyfinItem(song);
        return new Dictionary<string, object?>
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
        };
    }

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

    public IActionResult CreateJsonResponse(object data)
    {
        return new JsonResult(data);
    }

    public Dictionary<string, object?> ConvertSongToJellyfinItem(Song song) =>
        ConvertSongToJellyfinItem(song, ExternalTrackPresentation.Preferences(_httpContexts?.HttpContext));

    public Dictionary<string, object?> ConvertSongToJellyfinItem(Song song, allstarr.Core.Settings.ListeningPreferences? preferences) =>
        ConvertSongToJellyfinItem(song, preferences, _httpContexts?.HttpContext?.Request.Query);

    public Dictionary<string, object?> ConvertSongToJellyfinItem(Song song,
        allstarr.Core.Settings.ListeningPreferences? preferences, IQueryCollection? query, bool includeAllFields = false) =>
        BuildSongItem(song, preferences, JellyfinItemFields.ForRequest(_httpContexts?.HttpContext, query, includeAllFields));

    private Dictionary<string, object?> BuildSongItem(Song song,
        allstarr.Core.Settings.ListeningPreferences? preferences, JellyfinItemFields? fields)
    {
        if (song.IsLocal && JellyfinItemSnapshotHelper.TryGetClonedRawItemSnapshot(song, out var original))
        {
            return original;
        }

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
        var download = song.IsLocal ? null : JellyfinDownloadMetadata.ForSong(song);

        if (!song.IsLocal)
        {
            songTitle = ExternalTrackPresentation.Title(song, preferences);

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
            ["ServerId"] = ServerId,
            ["Id"] = song.Id,
            // This capability flag prompts Jellyfin clients to call the lyrics route.
            // The route still returns 404 when every configured provider has a genuine miss.
            ["HasLyrics"] = !song.IsLocal,
            ["Container"] = song.IsLocal ? "flac" : download?.Container,
            ["PremiereDate"] = song.IsLocal
                ? song.Year.HasValue ? $"{song.Year}-01-01T00:00:00.0000000Z" : null
                : ReleaseDate(song.ReleaseDate, song.Year)?.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture),
            ["DateCreated"] = song.Year.HasValue ? $"{song.Year}-01-01T00:00:00.0000000Z" : DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            ["RunTimeTicks"] = runTimeTicks,
            ["ProductionYear"] = song.IsLocal ? song.Year : ReleaseDate(song.ReleaseDate, song.Year)?.Year,
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
            ["ImageBlurHashes"] = song.IsLocal ? new Dictionary<string, object>() : null,
            ["LocationType"] = "FileSystem",
            ["MediaType"] = "Audio",
            ["NormalizationGain"] = 0.0,
            ["Path"] = download?.Path ?? $"/music/{artistName}/{albumName}/{songTitle}.flac",
            ["CanDelete"] = false,
            ["CanDownload"] = true,
            ["SupportsSync"] = true
        };

        if (!song.IsLocal && !string.IsNullOrEmpty(song.ExternalProvider))
        {
            var supportsTranscoding = !ShouldDisableTranscoding(song.ExternalProvider);
            item["Overview"] = ExternalTrackPresentation.PlaybackDescription;

            item["ProviderIds"] = new Dictionary<string, string>
            {
                [song.ExternalProvider] = song.ExternalId ?? ""
            };

            if (!string.IsNullOrEmpty(song.Isrc))
            {
                var providerIds = (Dictionary<string, string>)item["ProviderIds"]!;
                providerIds["ISRC"] = song.Isrc;
            }

            // Synthetic audio needs a complete Jellyfin MediaSource shape for client playback.
            item["MediaSources"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["Protocol"] = "File",
                    ["Id"] = song.Id,
                    ["Path"] = download!.Path,
                    ["DirectStreamUrl"] = $"/Audio/{Uri.EscapeDataString(song.Id)}/stream?static=true",
                    ["TranscodingUrl"] = $"/Audio/{Uri.EscapeDataString(song.Id)}/universal?container=flac&audioCodec=flac",
                    ["Type"] = "Default",
                    ["Container"] = download.Container,
                    ["Name"] = download.Name,
                    ["IsRemote"] = false,
                    ["ETag"] = song.Id,
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
                            ["VideoRange"] = "Unknown",
                            ["VideoRangeType"] = "Unknown",
                            ["AudioSpatialFormat"] = "None",
                            ["LocalizedDefault"] = "Default",
                            ["LocalizedExternal"] = "External",
                            ["LocalizedOriginal"] = "Original",
                            ["IsInterlaced"] = false,
                            ["IsDefault"] = false,
                            ["IsForced"] = false,
                            ["IsHearingImpaired"] = false,
                            ["IsOriginal"] = false,
                            ["Type"] = "Audio",
                            ["Index"] = 0,
                            ["IsExternal"] = false,
                            ["IsTextSubtitleStream"] = false,
                            ["SupportsExternalStream"] = false
                        }
                    },
                    ["MediaAttachments"] = new List<object>(),
                    ["Formats"] = new List<string>(),
                    ["RequiredHttpHeaders"] = new Dictionary<string, string>(),
                    ["TranscodingSubProtocol"] = "http",
                    ["DefaultAudioStreamIndex"] = 0,
                    ["HasSegments"] = false
                }
            };
        }
        else if (song.IsLocal && song.JellyfinMetadata != null && song.JellyfinMetadata.ContainsKey("MediaSources"))
        {
            // Preserve native technical metadata such as bitrate.
            item["MediaSources"] = song.JellyfinMetadata["MediaSources"];
        }

        if (!song.IsLocal)
        {
            JellyfinLyricStreams.AddTo(item);
            if (item.GetValueOrDefault("MediaSources") is Dictionary<string, object?>[] sources)
            {
                ApplyMediaFacts(song, item, sources[0]);
                item["MediaStreams"] = sources[0]["MediaStreams"];
            }
            return fields?.Apply(item) ?? item;
        }
        return item;
    }

    public object CreatePlaybackMediaSources(Song song)
    {
        var item = BuildSongItem(song, ExternalTrackPresentation.Preferences(_httpContexts?.HttpContext), null);
        var sources = item.GetValueOrDefault("MediaSources") ?? Array.Empty<object>();
        if (!song.IsLocal && sources is Dictionary<string, object?>[] externalSources)
        {
            foreach (var source in externalSources)
            {
                if (source.GetValueOrDefault("MediaStreams") is Dictionary<string, object?>[] streams)
                    foreach (var stream in streams)
                    {
                        stream["IsAVC"] = false;
                        stream["Level"] = 0;
                    }
            }
        }
        return sources;
    }

    private void ApplyMediaFacts(Song song, Dictionary<string, object?> item, Dictionary<string, object?> source)
    {
        var http = _httpContexts?.HttpContext;
        var context = http?.GetProtocolExecutionContext();
        var previous = context == null ? null : _playbackActivity?.StreamFor(
            context.Actor?.EffectiveUserId, context.Client.DeviceId, song.Id);
        if (previous?.Matches(context!.Protocol.ToString().ToLowerInvariant(), context.BackendInstanceId) != true)
            previous = null;
        var media = previous?.Media;
        var size = previous != null ? previous.Size : song.Size;
        // Catalog bitrates can describe a different encode from the serving file.
        var bitrate = previous != null ? media?.Bitrate : song.Size is > 0 ? song.Bitrate : null;
        var sampleRate = previous != null ? media?.SampleRate : song.SampleRate;
        var bitDepth = previous != null ? media?.BitDepth : song.BitDepth;
        var channels = previous != null ? media?.Channels : song.Channels;
        var codec = previous != null ? media?.Codec : song.Codec;
        var stream = ((Dictionary<string, object?>[])source["MediaStreams"]!)[0];
        if (!string.IsNullOrWhiteSpace(codec) && codec != "unknown") stream["Codec"] = codec;
        if (sampleRate is > 0)
        {
            stream["SampleRate"] = sampleRate.Value;
            stream["TimeBase"] = $"1/{sampleRate.Value}";
        }
        if (bitDepth is > 0) stream["BitDepth"] = bitDepth.Value;
        if (channels is > 0) stream["Channels"] = channels.Value;
        if (bitrate is > 0) stream["BitRate"] = bitrate.Value;
        if (size is > 0) source["Size"] = size.Value;
        var seconds = song.PreciseDuration?.TotalSeconds ?? song.Duration;
        var averageBitrate = size is > 0 && seconds is > 0 ? size.Value * 8d / seconds.Value : 0;
        if (bitrate is > 0) source["Bitrate"] = bitrate.Value;
        else if (averageBitrate is > 0 and <= int.MaxValue) source["Bitrate"] = (int)averageBitrate;
        var container = previous != null ? media?.Container : song.Container;
        if (!string.IsNullOrWhiteSpace(container) && container != "unknown")
        {
            source["Container"] = container;
            item["Container"] = container;
        }
    }

    private static DateTime? ReleaseDate(string? value, int? year)
    {
        if (value != null && DateTime.TryParseExact(value, ["yyyy-MM-dd", "yyyy-MM", "yyyy"],
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
            return DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        if (value != null && DateTimeOffset.TryParseExact(value,
            ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var timestamp))
            return DateTime.SpecifyKind(timestamp.Date, DateTimeKind.Utc);
        return year is >= 1 and <= 9999 ? new DateTime(year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null;
    }

    private static bool ShouldDisableTranscoding(string provider)
    {
        return provider.Equals("deezer", StringComparison.OrdinalIgnoreCase) ||
               provider.Equals("qobuz", StringComparison.OrdinalIgnoreCase);
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

    public Dictionary<string, object?> ConvertAlbumToJellyfinItem(Album album) => BuildAlbumItem(album);

    private Dictionary<string, object?> BuildAlbumItem(Album album, bool includeAllFields = false)
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
            ["ServerId"] = ServerId,
            ["Id"] = album.Id,
            ["PremiereDate"] = album.IsLocal
                ? album.Year.HasValue ? $"{album.Year}-01-01T05:00:00.0000000Z" : null
                : ReleaseDate(album.ReleaseDate, album.Year)?.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture),
            ["DateCreated"] = album.Year.HasValue ? $"{album.Year}-01-01T05:00:00.0000000Z" : "1970-01-01T00:00:00.0000000Z",
            ["ChannelId"] = (object?)null,
            ["Genres"] = !string.IsNullOrEmpty(album.Genre)
                ? new[] { album.Genre }
                : new string[0],
            ["RunTimeTicks"] = album.Songs.Sum(song => (long)(song.Duration ?? 0)) * TimeSpan.TicksPerSecond,
            ["ProductionYear"] = album.IsLocal ? album.Year : ReleaseDate(album.ReleaseDate, album.Year)?.Year,
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
            ["ImageBlurHashes"] = album.IsLocal ? new Dictionary<string, object>() : null,
            ["LocationType"] = "FileSystem",
            ["MediaType"] = "Unknown",
            ["ChildCount"] = album.SongCount ?? album.Songs.Count
        };

        if (!album.IsLocal && !string.IsNullOrEmpty(album.ExternalProvider))
        {
            item["ProviderIds"] = new Dictionary<string, string>
            {
                [album.ExternalProvider] = album.ExternalId ?? ""
            };
        }

        return album.IsLocal ? item : ApplyExternalFields(item, includeAllFields);
    }

    public Dictionary<string, object?> ConvertArtistToJellyfinItem(Artist artist) => BuildArtistItem(artist);

    private Dictionary<string, object?> BuildArtistItem(Artist artist, bool includeAllFields = false)
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
            ["ServerId"] = ServerId,
            ["Id"] = artist.Id,
            ["ChannelId"] = (object?)null,
            ["Genres"] = new string[0],
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
            ["ImageBlurHashes"] = artist.IsLocal ? new Dictionary<string, object>() : null,
            ["LocationType"] = "FileSystem",
            ["MediaType"] = "Unknown",
            ["AlbumCount"] = artist.AlbumCount ?? 0
        };

        if (!artist.IsLocal && !string.IsNullOrEmpty(artist.ExternalProvider))
        {
            item["ProviderIds"] = new Dictionary<string, string>
            {
                [artist.ExternalProvider] = artist.ExternalId ?? ""
            };
        }

        return artist.IsLocal ? item : ApplyExternalFields(item, includeAllFields);
    }

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

    public Dictionary<string, object?> ConvertPlaylistToJellyfinItem(ExternalPlaylist playlist) =>
        ConvertPlaylistToJellyfinItem(playlist, _httpContexts?.HttpContext?.Request.Query);

    public Dictionary<string, object?> ConvertPlaylistToJellyfinItem(ExternalPlaylist playlist, IQueryCollection? query,
        bool includeAllFields = false)
    {
        var curatorName = !string.IsNullOrEmpty(playlist.CuratorName)
            ? playlist.CuratorName
            : playlist.Provider;

        var item = new Dictionary<string, object?>
        {
            ["Name"] = playlist.Name,
            ["Overview"] = playlist.Description,
            ["ServerId"] = ServerId,
            ["Id"] = playlist.Id,
            ["ChannelId"] = (object?)null,
            ["Genres"] = new string[0],
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
            ["ImageBlurHashes"] = null,
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

        return JellyfinItemFields.ForRequest(_httpContexts?.HttpContext, query, includeAllFields).Apply(item);
    }
    public Dictionary<string, object?> ConvertPlaylistToAlbumItem(ExternalPlaylist playlist)
    {
        var curatorName = !string.IsNullOrEmpty(playlist.CuratorName)
            ? playlist.CuratorName
            : playlist.Provider;

        var item = new Dictionary<string, object?>
        {
            ["Name"] = BuildExternalPlaylistName(playlist.Name, playlist.Provider),
            ["ServerId"] = ServerId,
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
            ["ImageBlurHashes"] = null,
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

        return ApplyExternalFields(item);
    }
}
