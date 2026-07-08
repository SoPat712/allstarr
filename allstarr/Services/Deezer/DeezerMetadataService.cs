using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Download;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Deezer;

/// <summary>
/// Metadata service implementation using the Deezer API (free, no key required)
/// </summary>
public class DeezerMetadataService : TrackParserBase, IConcreteMetadataService
{
    private readonly HttpClient _httpClient;
    private readonly SubsonicSettings _settings;
    private readonly GenreEnrichmentService? _genreEnrichment;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly int _minRequestIntervalMs;
    private DateTime _lastRequestTime = DateTime.MinValue;
    private const string BaseUrl = "https://api.deezer.com";
    private const string DeezerApiHost = "api.deezer.com";
    private const int MetadataPageSize = 100;

    public DeezerMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptions<SubsonicSettings> settings,
        GenreEnrichmentService? genreEnrichment = null,
        IOptions<DeezerSettings>? deezerSettings = null)
    {
        _httpClient = httpClientFactory.CreateClient();
        _settings = settings.Value;
        _genreEnrichment = genreEnrichment;
        _minRequestIntervalMs = Math.Max(
            0,
            deezerSettings?.Value.MinRequestIntervalMs ?? new DeezerSettings().MinRequestIntervalMs);
    }

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var normalizedLimit = NormalizeSearchLimit(limit);
        var allSongs = new List<Song>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var queryVariant in BuildSearchQueryVariants(query))
        {
            var songs = await SearchSongsSingleQueryAsync(queryVariant, normalizedLimit, cancellationToken);
            foreach (var song in songs)
            {
                var key = !string.IsNullOrWhiteSpace(song.ExternalId) ? song.ExternalId : song.Id;
                if (string.IsNullOrWhiteSpace(key) || !seenIds.Add(key))
                {
                    continue;
                }

                allSongs.Add(song);
                if (allSongs.Count >= normalizedLimit)
                {
                    break;
                }
            }

            if (allSongs.Count >= normalizedLimit)
            {
                break;
            }
        }

        return allSongs;
    }

    private async Task<List<Song>> SearchSongsSingleQueryAsync(string query, int limit, CancellationToken cancellationToken)
    {
        try
        {
            var url = BuildRankedSearchUrl("track", query, limit);
            var response = await GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode) return new List<Song>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonDocument.Parse(json);

            var songs = new List<Song>();
            if (result.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var track in data.EnumerateArray())
                {
                    var song = ParseDeezerTrack(track);
                    if (ExplicitContentFilter.ShouldIncludeSong(song, _settings.ExplicitFilter))
                    {
                        songs.Add(song);
                    }
                }
            }

            return songs;
        }
        catch
        {
            return new List<Song>();
        }
    }

    public async Task<Song?> FindSongByIsrcAsync(string isrc, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        try
        {
            var normalizedIsrc = isrc.Trim();
            var url = $"{BaseUrl}/track/isrc:{Uri.EscapeDataString(normalizedIsrc)}";
            var response = await GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var result = JsonDocument.Parse(json);
            if (result.RootElement.TryGetProperty("error", out _) ||
                !result.RootElement.TryGetProperty("id", out _))
            {
                return null;
            }

            var song = ParseDeezerTrackFull(result.RootElement);
            return string.Equals(song.Isrc, normalizedIsrc, StringComparison.OrdinalIgnoreCase)
                ? song
                : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var normalizedLimit = NormalizeSearchLimit(limit);
        var allAlbums = new List<Album>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var queryVariant in BuildSearchQueryVariants(query))
        {
            var albums = await SearchAlbumsSingleQueryAsync(queryVariant, normalizedLimit, cancellationToken);
            foreach (var album in albums)
            {
                var key = !string.IsNullOrWhiteSpace(album.ExternalId) ? album.ExternalId : album.Id;
                if (string.IsNullOrWhiteSpace(key) || !seenIds.Add(key))
                {
                    continue;
                }

                allAlbums.Add(album);
                if (allAlbums.Count >= normalizedLimit)
                {
                    break;
                }
            }

            if (allAlbums.Count >= normalizedLimit)
            {
                break;
            }
        }

        return allAlbums;
    }

    private async Task<List<Album>> SearchAlbumsSingleQueryAsync(string query, int limit, CancellationToken cancellationToken)
    {
        try
        {
            var url = BuildRankedSearchUrl("album", query, limit);
            var response = await GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode) return new List<Album>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonDocument.Parse(json);

            var albums = new List<Album>();
            if (result.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var album in data.EnumerateArray())
                {
                    albums.Add(ParseDeezerAlbum(album));
                }
            }

            return albums;
        }
        catch
        {
            return new List<Album>();
        }
    }

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var normalizedLimit = NormalizeSearchLimit(limit);
        var allArtists = new List<Artist>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var queryVariant in BuildSearchQueryVariants(query))
        {
            var artists = await SearchArtistsSingleQueryAsync(queryVariant, normalizedLimit, cancellationToken);
            foreach (var artist in artists)
            {
                var key = !string.IsNullOrWhiteSpace(artist.ExternalId) ? artist.ExternalId : artist.Id;
                if (string.IsNullOrWhiteSpace(key) || !seenIds.Add(key))
                {
                    continue;
                }

                allArtists.Add(artist);
                if (allArtists.Count >= normalizedLimit)
                {
                    break;
                }
            }

            if (allArtists.Count >= normalizedLimit)
            {
                break;
            }
        }

        return allArtists;
    }

    private async Task<List<Artist>> SearchArtistsSingleQueryAsync(string query, int limit, CancellationToken cancellationToken)
    {
        try
        {
            var url = BuildRankedSearchUrl("artist", query, limit);
            var response = await GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode) return new List<Artist>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonDocument.Parse(json);

            var artists = new List<Artist>();
            if (result.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var artist in data.EnumerateArray())
                {
                    artists.Add(ParseDeezerArtist(artist));
                }
            }

            return artists;
        }
        catch
        {
            return new List<Artist>();
        }
    }

    private static IReadOnlyList<string> BuildSearchQueryVariants(string query)
    {
        var variants = new List<string>();

        AddQueryVariant(variants, query);

        if (query.Contains('&'))
        {
            AddQueryVariant(variants, query.Replace("&", " and "));
        }

        return variants;
    }

    private static void AddQueryVariant(List<string> variants, string candidate)
    {
        var normalized = System.Text.RegularExpressions.Regex.Replace(candidate, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!variants.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            variants.Add(normalized);
        }
    }

    private static int NormalizeSearchLimit(int limit)
    {
        return Math.Max(1, limit);
    }

    private static string BuildRankedSearchUrl(string searchType, string query, int limit)
    {
        return $"{BaseUrl}/search/{searchType}?q={Uri.EscapeDataString(query)}&limit={limit}&order=RANKING";
    }

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20, CancellationToken cancellationToken = default)
    {
        var songsTask = songLimit > 0
            ? SearchSongsAsync(query, songLimit, cancellationToken)
            : Task.FromResult(new List<Song>());
        var albumsTask = albumLimit > 0
            ? SearchAlbumsAsync(query, albumLimit, cancellationToken)
            : Task.FromResult(new List<Album>());
        var artistsTask = artistLimit > 0
            ? SearchArtistsAsync(query, artistLimit, cancellationToken)
            : Task.FromResult(new List<Artist>());

        await Task.WhenAll(songsTask, albumsTask, artistsTask);

        return new SearchResult
        {
            Songs = await songsTask,
            Albums = await albumsTask,
            Artists = await artistsTask
        };
    }

    public async Task<Song?> GetSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(externalProvider, "deezer", StringComparison.OrdinalIgnoreCase)) return null;

        var url = $"{BaseUrl}/track/{externalId}";
        var response = await GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var track = JsonDocument.Parse(json).RootElement;

        if (track.TryGetProperty("error", out _)) return null;

        // For an individual track, get full metadata
        var song = ParseDeezerTrackFull(track);

        // Get additional info from album (genre, total track count, label, copyright)
        if (track.TryGetProperty("album", out var albumRef) &&
            albumRef.TryGetProperty("id", out var albumIdEl))
        {
            var albumId = albumIdEl.GetInt64().ToString();
            try
            {
                var albumUrl = $"{BaseUrl}/album/{albumId}";
                var albumResponse = await GetAsync(albumUrl, cancellationToken);
                if (albumResponse.IsSuccessStatusCode)
                {
                    var albumJson = await albumResponse.Content.ReadAsStringAsync(cancellationToken);
                    var albumData = JsonDocument.Parse(albumJson).RootElement;

                    // Genre
                    if (albumData.TryGetProperty("genres", out var genres) &&
                        genres.TryGetProperty("data", out var genresData) &&
                        genresData.GetArrayLength() > 0 &&
                        genresData[0].TryGetProperty("name", out var genreName))
                    {
                        song.Genre = genreName.GetString();
                    }

                    // Total track count
                    if (albumData.TryGetProperty("nb_tracks", out var nbTracks))
                    {
                        song.TotalTracks = nbTracks.GetInt32();
                    }

                    // Label
                    if (albumData.TryGetProperty("label", out var label))
                    {
                        song.Label = label.GetString();
                    }

                    // Cover art XL if not already set
                    if (string.IsNullOrEmpty(song.CoverArtUrlLarge))
                    {
                        if (albumData.TryGetProperty("cover_xl", out var coverXl))
                        {
                            song.CoverArtUrlLarge = coverXl.GetString();
                        }
                        else if (albumData.TryGetProperty("cover_big", out var coverBig))
                        {
                            song.CoverArtUrlLarge = coverBig.GetString();
                        }
                    }
                }
            }
            catch
            {
                // If we can't get the album, continue with track info only
            }
        }

        // Enrich with MusicBrainz genres if missing
        if (_genreEnrichment != null && string.IsNullOrEmpty(song.Genre))
        {
            // Fire-and-forget: don't block the response waiting for genre enrichment
            _ = Task.Run(async () =>
            {
                try
                {
                    await _genreEnrichment.EnrichSongGenreAsync(song);
                }
                catch
                {
                    // Silently ignore genre enrichment failures
                }
            });
        }

        return song;
    }

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(externalProvider, "deezer", StringComparison.OrdinalIgnoreCase)) return null;

        var url = $"{BaseUrl}/album/{externalId}";
        var response = await GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var albumDocument = JsonDocument.Parse(json);
        var albumElement = albumDocument.RootElement;

        if (albumElement.TryGetProperty("error", out _)) return null;

        var album = ParseDeezerAlbum(albumElement);

        var trackIndex = 1;
        var embeddedTrackCount = 0;

        void AddTrack(JsonElement track, List<Song> songs)
        {
            // Pass the album artist to ensure proper folder organization
            var song = ParseDeezerTrack(track, trackIndex, album.Artist);

            // Ensure album metadata is set (tracks in album response may not have full album object)
            song.Album = album.Title;
            song.AlbumId = album.Id;
            song.AlbumArtist = album.Artist;

            if (ExplicitContentFilter.ShouldIncludeSong(song, _settings.ExplicitFilter))
            {
                songs.Add(song);
            }

            trackIndex++;
        }

        // Deezer album details embed the first page of tracks.
        if (albumElement.TryGetProperty("tracks", out var tracks) &&
            tracks.TryGetProperty("data", out var tracksData))
        {
            foreach (var track in tracksData.EnumerateArray())
            {
                embeddedTrackCount++;
                AddTrack(track, album.Songs);
            }
        }

        if (album.SongCount.HasValue && embeddedTrackCount < album.SongCount.Value)
        {
            var pagedSongs = new List<Song>();
            trackIndex = 1;

            var pagedTrackCount = await ReadPagedDataAsync(
                index => BuildMetadataPageUrl($"album/{Uri.EscapeDataString(externalId)}/tracks", index),
                track => AddTrack(track, pagedSongs),
                cancellationToken);

            if (pagedTrackCount > embeddedTrackCount)
            {
                album.Songs.Clear();
                album.Songs.AddRange(pagedSongs);
            }
        }

        return album;
    }

    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(externalProvider, "deezer", StringComparison.OrdinalIgnoreCase)) return null;

        var url = $"{BaseUrl}/artist/{externalId}";
        var response = await GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var artist = JsonDocument.Parse(json).RootElement;

        if (artist.TryGetProperty("error", out _)) return null;

        return ParseDeezerArtist(artist);
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(externalProvider, "deezer", StringComparison.OrdinalIgnoreCase)) return new List<Album>();

        var albums = new List<Album>();
        await ReadPagedDataAsync(
            index => BuildMetadataPageUrl($"artist/{Uri.EscapeDataString(externalId)}/albums", index),
            album => albums.Add(ParseDeezerAlbum(album)),
            cancellationToken);

        return albums;
    }

    public async Task<List<Song>> GetArtistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(externalProvider, "deezer", StringComparison.OrdinalIgnoreCase)) return new List<Song>();

        var url = $"{BaseUrl}/artist/{externalId}/top?limit=50";
        var response = await GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode) return new List<Song>();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonDocument.Parse(json);

        var tracks = new List<Song>();
        if (result.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var track in data.EnumerateArray())
            {
                tracks.Add(ParseDeezerTrack(track));
            }
        }

        return tracks;
    }

    private Song ParseDeezerTrack(JsonElement track, int? fallbackTrackNumber = null, string? albumArtist = null)
    {
        var externalId = track.GetProperty("id").GetInt64().ToString();

        // Try to get track_position from API, fallback to provided index
        int? trackNumber = track.TryGetProperty("track_position", out var trackPos)
            ? trackPos.GetInt32()
            : fallbackTrackNumber;

        // Explicit content lyrics value
        int? explicitContentLyrics = track.TryGetProperty("explicit_content_lyrics", out var ecl)
            ? ecl.GetInt32()
            : null;

        return new Song
        {
            Id = BuildExternalSongId("deezer", externalId),
            Title = track.GetProperty("title").GetString() ?? "",
            Artist = track.TryGetProperty("artist", out var artist)
                ? artist.GetProperty("name").GetString() ?? ""
                : "",
            ArtistId = track.TryGetProperty("artist", out var artistForId)
                ? BuildExternalArtistId("deezer", artistForId.GetProperty("id").GetInt64().ToString())
                : null,
            Album = track.TryGetProperty("album", out var album)
                ? album.GetProperty("title").GetString() ?? ""
                : "",
            AlbumId = track.TryGetProperty("album", out var albumForId)
                ? BuildExternalAlbumId("deezer", albumForId.GetProperty("id").GetInt64().ToString())
                : null,
            Duration = track.TryGetProperty("duration", out var duration)
                ? duration.GetInt32()
                : null,
            Track = trackNumber,
            CoverArtUrl = track.TryGetProperty("album", out var albumForCover) &&
                          albumForCover.TryGetProperty("cover_medium", out var cover)
                ? cover.GetString()
                : null,
            AlbumArtist = albumArtist,
            Isrc = track.TryGetProperty("isrc", out var isrc)
                ? isrc.GetString()
                : null,
            IsLocal = false,
            ExternalProvider = "deezer",
            ExternalId = externalId,
            ExplicitContentLyrics = explicitContentLyrics
        };
    }

    /// <summary>
    /// Parses a Deezer track with all available metadata
    /// Used for GetSongAsync which returns complete data
    /// </summary>
    private Song ParseDeezerTrackFull(JsonElement track)
    {
        var externalId = track.GetProperty("id").GetInt64().ToString();

        // Track position et disc number
        int? trackNumber = track.TryGetProperty("track_position", out var trackPos)
            ? trackPos.GetInt32()
            : null;
        int? discNumber = track.TryGetProperty("disk_number", out var diskNum)
            ? diskNum.GetInt32()
            : null;

        // BPM
        int? bpm = track.TryGetProperty("bpm", out var bpmVal) && bpmVal.ValueKind == JsonValueKind.Number
            ? (int)bpmVal.GetDouble()
            : null;

        // ISRC
        string? isrc = track.TryGetProperty("isrc", out var isrcVal)
            ? isrcVal.GetString()
            : null;

        // Release date from album
        string? releaseDate = null;
        int? year = null;
        if (track.TryGetProperty("release_date", out var relDate))
        {
            releaseDate = relDate.GetString();
            year = ParseYearFromDateString(releaseDate);
        }
        else if (track.TryGetProperty("album", out var albumForDate) &&
                 albumForDate.TryGetProperty("release_date", out var albumRelDate))
        {
            releaseDate = albumRelDate.GetString();
            year = ParseYearFromDateString(releaseDate);
        }

        // Contributors (all artists including features)
        var contributors = new List<string>();
        var contributorIds = new List<string>();
        if (track.TryGetProperty("contributors", out var contribs))
        {
            foreach (var contrib in contribs.EnumerateArray())
            {
                if (contrib.TryGetProperty("name", out var contribName) &&
                    contrib.TryGetProperty("id", out var contribId))
                {
                    var name = contribName.GetString();
                    var id = contribId.GetInt64();
                    if (!string.IsNullOrEmpty(name))
                    {
                        contributors.Add(name);
                        contributorIds.Add(BuildExternalArtistId("deezer", id.ToString()));
                    }
                }
            }
        }

        // Album artist (first artist from album, or main track artist)
        string? albumArtist = null;
        if (track.TryGetProperty("album", out var albumForArtist) &&
            albumForArtist.TryGetProperty("artist", out var albumArtistEl))
        {
            albumArtist = albumArtistEl.TryGetProperty("name", out var aName)
                ? aName.GetString()
                : null;
        }

        // Cover art URLs (different sizes)
        string? coverMedium = null;
        string? coverLarge = null;
        if (track.TryGetProperty("album", out var albumForCover))
        {
            coverMedium = albumForCover.TryGetProperty("cover_medium", out var cm)
                ? cm.GetString()
                : null;
            coverLarge = albumForCover.TryGetProperty("cover_xl", out var cxl)
                ? cxl.GetString()
                : (albumForCover.TryGetProperty("cover_big", out var cb) ? cb.GetString() : null);
        }

        // Explicit content lyrics value
        int? explicitContentLyrics = track.TryGetProperty("explicit_content_lyrics", out var ecl)
            ? ecl.GetInt32()
            : null;

        return new Song
        {
            Id = BuildExternalSongId("deezer", externalId),
            Title = track.GetProperty("title").GetString() ?? "",
            Artist = track.TryGetProperty("artist", out var artist)
                ? artist.GetProperty("name").GetString() ?? ""
                : "",
            ArtistId = track.TryGetProperty("artist", out var artistForId)
                ? BuildExternalArtistId("deezer", artistForId.GetProperty("id").GetInt64().ToString())
                : null,
            Artists = contributors.Count > 0 ? contributors : new List<string>(),
            ArtistIds = contributorIds.Count > 0 ? contributorIds : new List<string>(),
            Album = track.TryGetProperty("album", out var album)
                ? album.GetProperty("title").GetString() ?? ""
                : "",
            AlbumId = track.TryGetProperty("album", out var albumForId)
                ? BuildExternalAlbumId("deezer", albumForId.GetProperty("id").GetInt64().ToString())
                : null,
            Duration = track.TryGetProperty("duration", out var duration)
                ? duration.GetInt32()
                : null,
            Track = trackNumber,
            DiscNumber = discNumber,
            Year = year,
            Bpm = bpm,
            Isrc = isrc,
            ReleaseDate = releaseDate,
            AlbumArtist = albumArtist,
            Contributors = contributors,
            CoverArtUrl = coverMedium,
            CoverArtUrlLarge = coverLarge,
            IsLocal = false,
            ExternalProvider = "deezer",
            ExternalId = externalId,
            ExplicitContentLyrics = explicitContentLyrics
        };
    }

    private Album ParseDeezerAlbum(JsonElement album)
    {
        var externalId = album.GetProperty("id").GetInt64().ToString();

        return new Album
        {
            Id = BuildExternalAlbumId("deezer", externalId),
            Title = album.GetProperty("title").GetString() ?? "",
            Artist = album.TryGetProperty("artist", out var artist)
                ? artist.GetProperty("name").GetString() ?? ""
                : "",
            ArtistId = album.TryGetProperty("artist", out var artistForId)
                ? BuildExternalArtistId("deezer", artistForId.GetProperty("id").GetInt64().ToString())
                : null,
            Year = album.TryGetProperty("release_date", out var releaseDate)
                ? ParseYearFromDateString(releaseDate.GetString())
                : null,
            SongCount = album.TryGetProperty("nb_tracks", out var nbTracks)
                ? nbTracks.GetInt32()
                : null,
            CoverArtUrl = album.TryGetProperty("cover_medium", out var cover)
                ? cover.GetString()
                : null,
            Genre = album.TryGetProperty("genres", out var genres) &&
                    genres.TryGetProperty("data", out var genresData) &&
                    genresData.GetArrayLength() > 0
                ? genresData[0].GetProperty("name").GetString()
                : null,
            IsLocal = false,
            ExternalProvider = "deezer",
            ExternalId = externalId
        };
    }

    private Artist ParseDeezerArtist(JsonElement artist)
    {
        var externalId = artist.GetProperty("id").GetInt64().ToString();

        return new Artist
        {
            Id = BuildExternalArtistId("deezer", externalId),
            Name = artist.GetProperty("name").GetString() ?? "",
            ImageUrl = artist.TryGetProperty("picture_medium", out var picture)
                ? picture.GetString()
                : null,
            AlbumCount = artist.TryGetProperty("nb_album", out var nbAlbum)
                ? nbAlbum.GetInt32()
                : null,
            IsLocal = false,
            ExternalProvider = "deezer",
            ExternalId = externalId
        };
    }

    public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var normalizedLimit = NormalizeSearchLimit(limit);
        var allPlaylists = new List<ExternalPlaylist>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var queryVariant in BuildSearchQueryVariants(query))
        {
            var playlists = await SearchPlaylistsSingleQueryAsync(queryVariant, normalizedLimit, cancellationToken);
            foreach (var playlist in playlists)
            {
                var key = !string.IsNullOrWhiteSpace(playlist.ExternalId) ? playlist.ExternalId : playlist.Id;
                if (string.IsNullOrWhiteSpace(key) || !seenIds.Add(key))
                {
                    continue;
                }

                allPlaylists.Add(playlist);
                if (allPlaylists.Count >= normalizedLimit)
                {
                    break;
                }
            }

            if (allPlaylists.Count >= normalizedLimit)
            {
                break;
            }
        }

        return allPlaylists;
    }

    private async Task<List<ExternalPlaylist>> SearchPlaylistsSingleQueryAsync(string query, int limit, CancellationToken cancellationToken)
    {
        try
        {
            var url = BuildRankedSearchUrl("playlist", query, limit);
            var response = await GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode) return new List<ExternalPlaylist>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonDocument.Parse(json);

            var playlists = new List<ExternalPlaylist>();
            if (result.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var playlist in data.EnumerateArray())
                {
                    playlists.Add(ParseDeezerPlaylist(playlist));
                }
            }

            return playlists;
        }
        catch
        {
            return new List<ExternalPlaylist>();
        }
    }

    public async Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(externalProvider, "deezer", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            var url = $"{BaseUrl}/playlist/{externalId}";
            var response = await GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var playlistElement = JsonDocument.Parse(json).RootElement;

            if (playlistElement.TryGetProperty("error", out _)) return null;

            return ParseDeezerPlaylist(playlistElement);
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(externalProvider, "deezer", StringComparison.OrdinalIgnoreCase)) return new List<Song>();

        try
        {
            var url = $"{BaseUrl}/playlist/{externalId}";
            var response = await GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode) return new List<Song>();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var playlistElement = JsonDocument.Parse(json).RootElement;

            if (playlistElement.TryGetProperty("error", out _)) return new List<Song>();

            var songs = new List<Song>();

            // Get playlist name for album field
            var playlistName = playlistElement.TryGetProperty("title", out var titleEl)
                ? titleEl.GetString() ?? "Unknown Playlist"
                : "Unknown Playlist";
            var trackIndex = 1;
            var embeddedTrackCount = 0;

            void AddTrack(JsonElement track, List<Song> tracks)
            {
                // For playlists, use the track's own artist (not a single album artist)
                var song = ParseDeezerTrack(track, trackIndex);

                // Override album name to be the playlist name
                song.Album = playlistName;

                // Playlists should not have disc numbers - always set to null.
                // This prevents Jellyfin from splitting the playlist into multiple "discs".
                song.DiscNumber = null;

                if (ExplicitContentFilter.ShouldIncludeSong(song, _settings.ExplicitFilter))
                {
                    tracks.Add(song);
                }

                trackIndex++;
            }

            if (playlistElement.TryGetProperty("tracks", out var tracks) &&
                tracks.TryGetProperty("data", out var tracksData))
            {
                foreach (var track in tracksData.EnumerateArray())
                {
                    embeddedTrackCount++;
                    AddTrack(track, songs);
                }
            }

            if (playlistElement.TryGetProperty("nb_tracks", out var trackCountElement) &&
                trackCountElement.TryGetInt32(out var trackCount) &&
                embeddedTrackCount < trackCount)
            {
                var pagedSongs = new List<Song>();
                trackIndex = 1;

                var pagedTrackCount = await ReadPagedDataAsync(
                    index => BuildMetadataPageUrl($"playlist/{Uri.EscapeDataString(externalId)}/tracks", index),
                    track => AddTrack(track, pagedSongs),
                    cancellationToken);

                if (pagedTrackCount > embeddedTrackCount)
                {
                    songs.Clear();
                    songs.AddRange(pagedSongs);
                }
            }

            return songs;
        }
        catch
        {
            return new List<Song>();
        }
    }

    private async Task<int> ReadPagedDataAsync(
        Func<int, string> buildIndexedPageUrl,
        Action<JsonElement> addItem,
        CancellationToken cancellationToken)
    {
        string? pageUrl = buildIndexedPageUrl(0);
        var itemCount = 0;
        var seenPageUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (IsOfficialDeezerApiUrl(pageUrl) && seenPageUrls.Add(pageUrl!))
        {
            var response = await GetAsync(pageUrl!, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                break;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var result = JsonDocument.Parse(json);
            if (result.RootElement.TryGetProperty("error", out _) ||
                !result.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var pageItemCount = 0;
            foreach (var item in data.EnumerateArray())
            {
                addItem(item);
                itemCount++;
                pageItemCount++;
            }

            pageUrl = GetNextPageUrl(result.RootElement) ??
                      GetIndexedNextPageUrl(result.RootElement, buildIndexedPageUrl, itemCount, pageItemCount);
        }

        return itemCount;
    }

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            if (_lastRequestTime != DateTime.MinValue && _minRequestIntervalMs > 0)
            {
                var elapsedMs = (DateTime.UtcNow - _lastRequestTime).TotalMilliseconds;
                if (elapsedMs < _minRequestIntervalMs)
                {
                    await Task.Delay((int)(_minRequestIntervalMs - elapsedMs), cancellationToken);
                }
            }

            _lastRequestTime = DateTime.UtcNow;
            return await _httpClient.GetAsync(url, cancellationToken);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private static string BuildMetadataPageUrl(string endpoint, int index)
    {
        return $"{BaseUrl}/{endpoint.TrimStart('/')}?index={index}&limit={MetadataPageSize}";
    }

    private static string? GetNextPageUrl(JsonElement result)
    {
        if (!result.TryGetProperty("next", out var next) ||
            next.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var pageUrl = next.GetString();
        return IsOfficialDeezerApiUrl(pageUrl) ? pageUrl : null;
    }

    private static string? GetIndexedNextPageUrl(
        JsonElement result,
        Func<int, string> buildIndexedPageUrl,
        int itemCount,
        int pageItemCount)
    {
        if (pageItemCount == 0)
        {
            return null;
        }

        if (result.TryGetProperty("total", out var total) &&
            total.TryGetInt32(out var totalCount))
        {
            return itemCount < totalCount
                ? buildIndexedPageUrl(itemCount)
                : null;
        }

        return pageItemCount >= MetadataPageSize
            ? buildIndexedPageUrl(itemCount)
            : null;
    }

    private static bool IsOfficialDeezerApiUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.Host, DeezerApiHost, StringComparison.OrdinalIgnoreCase);
    }

    private ExternalPlaylist ParseDeezerPlaylist(JsonElement playlist)
    {
        var externalId = playlist.GetProperty("id").GetInt64().ToString();

        // Get curator/creator name
        string? curatorName = null;
        if (playlist.TryGetProperty("user", out var user) &&
            user.TryGetProperty("name", out var userName))
        {
            curatorName = userName.GetString();
        }
        else if (playlist.TryGetProperty("creator", out var creator) &&
                 creator.TryGetProperty("name", out var creatorName))
        {
            curatorName = creatorName.GetString();
        }

        // Get creation date
        DateTime? createdDate = null;
        if (playlist.TryGetProperty("creation_date", out var creationDateEl))
        {
            var dateStr = creationDateEl.GetString();
            if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var date))
            {
                createdDate = date;
            }
        }

        return new ExternalPlaylist
        {
            Id = Common.PlaylistIdHelper.CreatePlaylistId("deezer", externalId),
            Name = playlist.GetProperty("title").GetString() ?? "",
            Description = playlist.TryGetProperty("description", out var desc)
                ? desc.GetString()
                : null,
            CuratorName = curatorName,
            Provider = "deezer",
            ExternalId = externalId,
            TrackCount = playlist.TryGetProperty("nb_tracks", out var nbTracks)
                ? nbTracks.GetInt32()
                : 0,
            Duration = playlist.TryGetProperty("duration", out var duration)
                ? duration.GetInt32()
                : 0,
            CoverUrl = playlist.TryGetProperty("picture_medium", out var picture)
                ? picture.GetString()
                : (playlist.TryGetProperty("picture_big", out var pictureBig)
                    ? pictureBig.GetString()
                    : null),
            CreatedDate = createdDate
        };
    }
}
