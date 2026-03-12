using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Download;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;

namespace allstarr.Services.SquidWTF;

/// <summary>
/// Metadata service implementation using the SquidWTF API (free, no key required).
///
/// SquidWTF is a proxy to Tidal's API that provides free access to Tidal's music catalog.
/// This implementation follows the hifi-api specification documented at the forked repository.
///
/// API Endpoints (per hifi-api spec):
/// - GET /search/?s={query}     - Search tracks (returns data.items array)
/// - GET /search/?a={query}     - Search artists (returns data.artists.items array)
/// - GET /search/?al={query}    - Search albums (returns data.albums.items array, undocumented)
/// - GET /search/?p={query}     - Search playlists (returns data.playlists.items array, undocumented)
/// - GET /info/?id={trackId}    - Get track metadata (returns data object with full track info)
/// - GET /track/?id={trackId}&quality={quality} - Get track download info (returns manifest)
/// - GET /recommendations/?id={trackId} - Get recommended next/similar tracks
/// - GET /album/?id={albumId}   - Get album with tracks (undocumented, returns data.items array)
/// - GET /artist/?f={artistId}  - Get artist with albums (undocumented, returns albums.items array)
/// - GET /playlist/?id={playlistId} - Get playlist with tracks (undocumented)
///
/// Quality Options:
/// - HI_RES_LOSSLESS: 24-bit/192kHz FLAC
/// - LOSSLESS: 16-bit/44.1kHz FLAC
/// - HIGH: 320kbps AAC
/// - LOW: 96kbps AAC
///
/// Response Structure:
/// All responses follow: { "version": "2.0", "data": { ... } }
/// Track objects include: id, title, duration, trackNumber, volumeNumber, explicit, bpm, isrc,
///   artist (singular), artists (array), album (object with id, title, cover UUID)
/// Cover art URLs: https://resources.tidal.com/images/{uuid-with-slashes}/{size}.jpg
///
/// Features:
/// - Round-robin load balancing across multiple mirror endpoints
/// - Automatic failover to backup endpoints on failure
/// - Racing endpoints for fastest response on latency-sensitive operations
/// - Redis caching for albums and artists (24-hour TTL)
/// - Explicit content filtering support
/// - Parallel Spotify ID conversion via Odesli for lyrics matching
/// </summary>

public class SquidWTFMetadataService : TrackParserBase, IMusicMetadataService
{
    private readonly HttpClient _httpClient;
    private readonly SubsonicSettings _settings;
    private readonly ILogger<SquidWTFMetadataService> _logger;
    private readonly RedisCacheService _cache;
    private readonly RoundRobinFallbackHelper _fallbackHelper;
    private readonly GenreEnrichmentService? _genreEnrichment;

    public SquidWTFMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptions<SubsonicSettings> settings,
        IOptions<SquidWTFSettings> squidwtfSettings,
        ILogger<SquidWTFMetadataService> logger,
        RedisCacheService cache,
        List<string> apiUrls,
        GenreEnrichmentService? genreEnrichment = null)
    {
        _httpClient = httpClientFactory.CreateClient();
        _settings = settings.Value;
        _logger = logger;
        _cache = cache;
        _fallbackHelper = new RoundRobinFallbackHelper(apiUrls, logger, "SquidWTF");
        _genreEnrichment = genreEnrichment;

        // Set up default headers
        _httpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:83.0) Gecko/20100101 Firefox/83.0");

        // Increase timeout for large artist/album responses (some artists have 100+ albums)
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
    }



    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var allSongs = new List<Song>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var queryVariant in BuildSearchQueryVariants(query))
        {
            var songs = await SearchSongsSingleQueryAsync(queryVariant, limit, cancellationToken);
            foreach (var song in songs)
            {
                var key = !string.IsNullOrWhiteSpace(song.ExternalId) ? song.ExternalId : song.Id;
                if (string.IsNullOrWhiteSpace(key) || !seenIds.Add(key))
                {
                    continue;
                }

                allSongs.Add(song);
                if (allSongs.Count >= limit)
                {
                    break;
                }
            }

            if (allSongs.Count >= limit)
            {
                break;
            }
        }

        _logger.LogInformation("✓ SQUIDWTF: Song search returned {Count} results", allSongs.Count);
        return allSongs;
    }

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var allAlbums = new List<Album>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var queryVariant in BuildSearchQueryVariants(query))
        {
            var albums = await SearchAlbumsSingleQueryAsync(queryVariant, limit, cancellationToken);
            foreach (var album in albums)
            {
                var key = !string.IsNullOrWhiteSpace(album.ExternalId) ? album.ExternalId : album.Id;
                if (string.IsNullOrWhiteSpace(key) || !seenIds.Add(key))
                {
                    continue;
                }

                allAlbums.Add(album);
                if (allAlbums.Count >= limit)
                {
                    break;
                }
            }

            if (allAlbums.Count >= limit)
            {
                break;
            }
        }

        _logger.LogInformation("✓ SQUIDWTF: Album search returned {Count} results", allAlbums.Count);
        return allAlbums;
    }

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var allArtists = new List<Artist>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var queryVariant in BuildSearchQueryVariants(query))
        {
            var artists = await SearchArtistsSingleQueryAsync(queryVariant, limit, cancellationToken);
            foreach (var artist in artists)
            {
                var key = !string.IsNullOrWhiteSpace(artist.ExternalId) ? artist.ExternalId : artist.Id;
                if (string.IsNullOrWhiteSpace(key) || !seenIds.Add(key))
                {
                    continue;
                }

                allArtists.Add(artist);
                if (allArtists.Count >= limit)
                {
                    break;
                }
            }

            if (allArtists.Count >= limit)
            {
                break;
            }
        }

        _logger.LogInformation("✓ SQUIDWTF: Artist search returned {Count} results", allArtists.Count);
        return allArtists;
    }

    private async Task<List<Song>> SearchSongsSingleQueryAsync(string query, int limit, CancellationToken cancellationToken)
    {
        // Use benchmark-ordered fallback (no endpoint racing).
        return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
        {
            // Use 's' parameter for track search as per hifi-api spec
            var url = $"{baseUrl}/search/?s={Uri.EscapeDataString(query)}";
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            // Check for error in response body
            var result = JsonDocument.Parse(json);
            if (result.RootElement.TryGetProperty("detail", out _) ||
                result.RootElement.TryGetProperty("error", out _))
            {
                throw new HttpRequestException("API returned error response");
            }

            var songs = new List<Song>();
            // Per hifi-api spec: track search returns data.items array
            if (result.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("items", out var items))
            {
                int count = 0;
                foreach (var track in items.EnumerateArray())
                {
                    if (count >= limit) break;

                    var song = ParseTidalTrack(track);
                    if (ExplicitContentFilter.ShouldIncludeSong(song, _settings.ExplicitFilter))
                    {
                        songs.Add(song);
                    }
                    count++;
                }
            }
            else
            {
                throw new InvalidOperationException("SquidWTF song search response did not contain data.items");
            }
            return songs;
        }, new List<Song>());
    }

    private async Task<List<Album>> SearchAlbumsSingleQueryAsync(string query, int limit, CancellationToken cancellationToken)
    {
        // Use benchmark-ordered fallback (no endpoint racing).
        return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
        {
            // Use 'al' parameter for album search
            // a= is for artists, al= is for albums, p= is for playlists
            var url = $"{baseUrl}/search/?al={Uri.EscapeDataString(query)}";
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonDocument.Parse(json);

            var albums = new List<Album>();
            // Per hifi-api spec: album search returns data.albums.items array
            if (result.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("albums", out var albumsObj) &&
                albumsObj.TryGetProperty("items", out var items))
            {
                int count = 0;
                foreach (var album in items.EnumerateArray())
                {
                    if (count >= limit) break;

                    albums.Add(ParseTidalAlbum(album));
                    count++;
                }
            }
            else
            {
                throw new InvalidOperationException("SquidWTF album search response did not contain data.albums.items");
            }

            return albums;
        }, new List<Album>());
    }

    private async Task<List<Artist>> SearchArtistsSingleQueryAsync(string query, int limit, CancellationToken cancellationToken)
    {
        // Use benchmark-ordered fallback (no endpoint racing).
        return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
        {
            // Per hifi-api spec: use 'a' parameter for artist search
            var url = $"{baseUrl}/search/?a={Uri.EscapeDataString(query)}";
            _logger.LogDebug("🔍 SQUIDWTF: Searching artists with URL: {Url}", url);

            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("⚠️ SQUIDWTF: Artist search failed with status {StatusCode}", response.StatusCode);
                throw new HttpRequestException($"HTTP {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonDocument.Parse(json);

            if (result.RootElement.TryGetProperty("detail", out _) ||
                result.RootElement.TryGetProperty("error", out _))
            {
                throw new HttpRequestException("API returned error response");
            }

            var artists = new List<Artist>();
            // Per hifi-api spec: artist search returns data.artists.items array
            if (result.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("artists", out var artistsObj) &&
                artistsObj.TryGetProperty("items", out var items))
            {
                int count = 0;
                foreach (var artist in items.EnumerateArray())
                {
                    if (count >= limit) break;

                    var parsedArtist = ParseTidalArtist(artist);
                    artists.Add(parsedArtist);
                    _logger.LogDebug("🎤 SQUIDWTF: Found artist: {Name} (ID: {Id})", parsedArtist.Name, parsedArtist.ExternalId);
                    count++;
                }
            }
            else
            {
                throw new InvalidOperationException("SquidWTF artist search response did not contain data.artists.items");
            }

            return artists;
        }, new List<Artist>());
    }

    private static IReadOnlyList<string> BuildSearchQueryVariants(string query)
    {
        var variants = new List<string>();

        AddVariant(variants, query);

        if (query.Contains('&'))
        {
            AddVariant(variants, query.Replace("&", " and "));
        }

        return variants;
    }

    private static void AddVariant(List<string> variants, string candidate)
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

	public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
	{
		return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
		{
            // Per hifi-api spec: use 'p' parameter for playlist search
			var url = $"{baseUrl}/search/?p={Uri.EscapeDataString(query)}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonDocument.Parse(json);

            if (result.RootElement.TryGetProperty("detail", out _) ||
                result.RootElement.TryGetProperty("error", out _))
            {
                throw new HttpRequestException("API returned error response");
            }

            var playlists = new List<ExternalPlaylist>();
            // Per hifi-api spec: playlist search returns data.playlists.items array
			if (result.RootElement.TryGetProperty("data", out var data) &&
				data.TryGetProperty("playlists", out var playlistObj) &&
				playlistObj.TryGetProperty("items", out var items))
			{
                int count = 0;
				foreach(var playlist in items.EnumerateArray())
				{
                    if (count >= limit) break;

					try
					{
						playlists.Add(ParseTidalPlaylist(playlist));
                        count++;
					}
					catch (Exception ex)
					{
						_logger.LogWarning(ex, "Failed to parse playlist, skipping");
						// Skip this playlist and continue with others
						}
					}
				}
            else
            {
                throw new InvalidOperationException("SquidWTF playlist search response did not contain data.playlists.items");
            }
			return playlists;
		}, new List<ExternalPlaylist>());
	}

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20, CancellationToken cancellationToken = default)
    {
        // Execute searches in parallel
        var songsTask = SearchSongsAsync(query, songLimit, cancellationToken);
        var albumsTask = SearchAlbumsAsync(query, albumLimit, cancellationToken);
        var artistsTask = SearchArtistsAsync(query, artistLimit, cancellationToken);

        await Task.WhenAll(songsTask, albumsTask, artistsTask);

		var temp = new SearchResult
        {
            Songs = await songsTask,
            Albums = await albumsTask,
            Artists = await artistsTask
        };

		return temp;
    }

    public async Task<Song?> GetSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (externalProvider != "squidwtf") return null;

        return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
        {
            // Per hifi-api spec: GET /info/?id={trackId} returns track metadata
            var url = $"{baseUrl}/info/?id={externalId}";

            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonDocument.Parse(json);

            // Per hifi-api spec: response is { "version": "2.0", "data": { track object } }
			if (!result.RootElement.TryGetProperty("data", out var track))
			{
				throw new InvalidOperationException($"SquidWTF /info response for track {externalId} did not contain data");
			}

			var song = ParseTidalTrackFull(track);

			// Enrich with MusicBrainz genres if missing (SquidWTF/Tidal doesn't provide genres)
			if (_genreEnrichment != null && string.IsNullOrEmpty(song.Genre))
			{
				// Fire-and-forget: don't block the response waiting for genre enrichment
				_ = Task.Run(async () =>
				{
					try
					{
						await _genreEnrichment.EnrichSongGenreAsync(song);
					}
					catch (Exception ex)
					{
						_logger.LogError(ex, "Failed to enrich genre for {Title}", song.Title);
					}
				});
			}

			// NOTE: Spotify ID conversion happens during download (in SquidWTFDownloadService)
			// This avoids redundant conversions and ensures it's done in parallel with the download

			return song;
        }, (Song?)null);
    }

    public async Task<List<Song>> GetTrackRecommendationsAsync(string externalId, int limit = 20, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return new List<Song>();

        return await _fallbackHelper.TryWithFallbackAsync(
            async (baseUrl) =>
            {
                var url = $"{baseUrl}/recommendations/?id={Uri.EscapeDataString(externalId)}";
                if (limit > 0)
                {
                    url += $"&limit={limit}";
                }

                var response = await _httpClient.GetAsync(url, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"SquidWTF recommendations request failed for track {externalId} with status {response.StatusCode}");
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonDocument.Parse(json);

                if (!result.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException(
                        $"SquidWTF recommendations response for track {externalId} did not contain data.items");
                }

                var songs = new List<Song>();
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var recommendation in items.EnumerateArray())
                {
                    JsonElement track;
                    if (recommendation.TryGetProperty("track", out var wrappedTrack))
                    {
                        track = wrappedTrack;
                    }
                    else
                    {
                        track = recommendation;
                    }

                    if (!track.TryGetProperty("id", out _))
                    {
                        continue;
                    }

                    Song song;
                    try
                    {
                        song = ParseTidalTrack(track);
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.Equals(song.ExternalId, externalId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var songKey = !string.IsNullOrWhiteSpace(song.ExternalId) ? song.ExternalId : song.Id;
                    if (string.IsNullOrWhiteSpace(songKey) || !seenIds.Add(songKey))
                    {
                        continue;
                    }

                    if (!ShouldIncludeSong(song))
                    {
                        continue;
                    }

                    songs.Add(song);
                    if (songs.Count >= limit)
                    {
                        break;
                    }
                }

                _logger.LogDebug(
                    "SQUIDWTF: Recommendations returned {Count} songs for track {TrackId} from {BaseUrl}",
                    songs.Count,
                    externalId,
                    baseUrl);
                return songs;
            },
            songs => songs.Count > 0,
            new List<Song>());
    }

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (externalProvider != "squidwtf") return null;

        // Try cache first
        var cacheKey = CacheKeyBuilder.BuildAlbumKey("squidwtf", externalId);
        var cached = await _cache.GetAsync<Album>(cacheKey);
        if (cached != null) return cached;

        return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
        {
            // Note: hifi-api doesn't document album endpoint, but /album/?id={albumId} is commonly used
            var url = $"{baseUrl}/album/?id={externalId}";

            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonDocument.Parse(json);

			// Response structure: { "data": { album object with "items" array of tracks } }
			if (!result.RootElement.TryGetProperty("data", out var albumElement))
			{
				throw new InvalidOperationException($"SquidWTF /album response for album {externalId} did not contain data");
			}

			var album = ParseTidalAlbum(albumElement);

			// Get album tracks from items array
			if (albumElement.TryGetProperty("items", out var tracks))
			{
				foreach (var trackWrapper in tracks.EnumerateArray())
				{
                    // Each item is wrapped: { "item": { track object } }
					if (trackWrapper.TryGetProperty("item", out var track))
					{
						var song = ParseTidalTrack(track);
						if (ExplicitContentFilter.ShouldIncludeSong(song, _settings.ExplicitFilter))
						{
							album.Songs.Add(song);
						}
					}
				}
			}

			// Cache for configurable duration
			await _cache.SetAsync(cacheKey, album, CacheExtensions.MetadataTTL);

			return album;
		}, (Album?)null);
    }

    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
        if (externalProvider != "squidwtf") return null;

        _logger.LogDebug("GetArtistAsync called for SquidWTF artist {ExternalId}", externalId);

        // Try cache first
        var cacheKey = CacheKeyBuilder.BuildArtistKey("squidwtf", externalId);
        var cached = await _cache.GetAsync<Artist>(cacheKey);
        if (cached != null)
        {
            _logger.LogDebug("Returning cached artist {ArtistName}, ImageUrl: {ImageUrl}", cached.Name, cached.ImageUrl ?? "NULL");
            return cached;
        }

        return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
        {
            // Note: hifi-api doesn't document artist endpoint, but /artist/?f={artistId} is commonly used
            var url = $"{baseUrl}/artist/?f={externalId}";
            _logger.LogDebug("Fetching artist from {Url}", url);

            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug("SquidWTF artist response: {Json}", json.Length > 500 ? json.Substring(0, 500) + "..." : json);
            var result = JsonDocument.Parse(json);

			JsonElement? artistSource = null;
			int albumCount = 0;

			// Response structure: { "albums": { "items": [ album objects ] }, "tracks": [ track objects ] }
            // Extract artist info from albums.items[0].artist (most reliable source)
            if (result.RootElement.TryGetProperty("albums", out var albums) &&
				albums.TryGetProperty("items", out var albumItems) &&
				albumItems.GetArrayLength() > 0)
			{
				albumCount = albumItems.GetArrayLength();
				if (albumItems[0].TryGetProperty("artist", out var artistEl))
				{
					artistSource = artistEl;
					_logger.LogDebug("Found artist from albums, albumCount={AlbumCount}", albumCount);
				}
            }

			// Fallback: try to get artist from tracks[0].artists[0]
			if (artistSource == null &&
			    result.RootElement.TryGetProperty("tracks", out var tracks) &&
				tracks.GetArrayLength() > 0 &&
				tracks[0].TryGetProperty("artists", out var artists) &&
				artists.GetArrayLength() > 0)
			{
				artistSource = artists[0];
                _logger.LogInformation("Found artist from tracks");
			}

			if (artistSource == null)
            {
                var keys = string.Join(", ", result.RootElement.EnumerateObject().Select(p => p.Name));
                throw new InvalidOperationException(
                    $"SquidWTF artist response for {externalId} did not contain artist data. Keys: {keys}");
            }

			var artistElement = artistSource.Value;

            // Extract picture UUID (may be null)
            string? pictureUuid = null;
            if (artistElement.TryGetProperty("picture", out var pictureEl) && pictureEl.ValueKind != JsonValueKind.Null)
            {
                pictureUuid = pictureEl.GetString();
            }

            // Normalize artist data to include album count
			var normalizedArtist = new JsonObject
			{
				["id"] = artistElement.GetProperty("id").GetInt64(),
				["name"] = artistElement.GetProperty("name").GetString(),
				["albums_count"] = albumCount,
				["picture"] = pictureUuid
			};

			using var doc = JsonDocument.Parse(normalizedArtist.ToJsonString());
			var artist = ParseTidalArtist(doc.RootElement);

            _logger.LogDebug("Successfully parsed artist {ArtistName} with {AlbumCount} albums", artist.Name, albumCount);

			// Cache for configurable duration
			await _cache.SetAsync(cacheKey, artist, CacheExtensions.MetadataTTL);

			return artist;
        }, (Artist?)null);
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
		if (externalProvider != "squidwtf") return new List<Album>();

		return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
		{
            _logger.LogDebug("GetArtistAlbumsAsync called for SquidWTF artist {ExternalId}", externalId);

            // Note: hifi-api doesn't document artist endpoint, but /artist/?f={artistId} is commonly used
			var url = $"{baseUrl}/artist/?f={externalId}";
			_logger.LogDebug("Fetching artist albums from URL: {Url}", url);
			var response = await _httpClient.GetAsync(url, cancellationToken);

			if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {response.StatusCode}");
            }

			var json = await response.Content.ReadAsStringAsync(cancellationToken);
			_logger.LogDebug("SquidWTF artist albums response for {ExternalId}: {JsonLength} bytes", externalId, json.Length);
			var result = JsonDocument.Parse(json);

			var albums = new List<Album>();

            // Response structure: { "albums": { "items": [ album objects ] } }
			if (result.RootElement.TryGetProperty("albums", out var albumsObj) &&
				albumsObj.TryGetProperty("items", out var items))
			{
				foreach (var album in items.EnumerateArray())
				{
					var parsedAlbum = ParseTidalAlbum(album);
					_logger.LogInformation("Parsed album: {AlbumTitle} by {ArtistName} (ArtistId: {ArtistId})",
						parsedAlbum.Title, parsedAlbum.Artist, parsedAlbum.ArtistId);
					albums.Add(parsedAlbum);
				}
                _logger.LogDebug("Found {AlbumCount} albums for artist {ExternalId}", albums.Count, externalId);
			}
            else
            {
                throw new InvalidOperationException(
                    $"SquidWTF artist albums response for {externalId} did not contain albums.items");
            }

			return albums;
		}, new List<Album>());
	}

    public async Task<List<Song>> GetArtistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    {
		if (externalProvider != "squidwtf") return new List<Song>();

		return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
		{
            _logger.LogDebug("GetArtistTracksAsync called for SquidWTF artist {ExternalId}", externalId);

            // Same endpoint as albums - /artist/?f={artistId} returns both albums and tracks
			var url = $"{baseUrl}/artist/?f={externalId}";
			_logger.LogDebug("Fetching artist tracks from URL: {Url}", url);
			var response = await _httpClient.GetAsync(url, cancellationToken);

			if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {response.StatusCode}");
            }

			var json = await response.Content.ReadAsStringAsync(cancellationToken);
			_logger.LogDebug("SquidWTF artist tracks response for {ExternalId}: {JsonLength} bytes", externalId, json.Length);
			var result = JsonDocument.Parse(json);

			var tracks = new List<Song>();

            // Response structure: { "tracks": [ track objects ] }
			if (result.RootElement.TryGetProperty("tracks", out var tracksArray))
			{
				foreach (var track in tracksArray.EnumerateArray())
				{
					var parsedTrack = ParseTidalTrack(track);
					tracks.Add(parsedTrack);
				}
                _logger.LogDebug("Found {TrackCount} tracks for artist {ExternalId}", tracks.Count, externalId);
			}
            else
            {
                throw new InvalidOperationException(
                    $"SquidWTF artist tracks response for {externalId} did not contain tracks");
            }

			return tracks;
		}, new List<Song>());
	}

    public async Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
    	{
    		if (externalProvider != "squidwtf") return null;

    		return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
    		{
                // Note: hifi-api doesn't document playlist endpoint, but /playlist/?id={playlistId} is commonly used
    			var url = $"{baseUrl}/playlist/?id={externalId}";
    			var response = await _httpClient.GetAsync(url, cancellationToken);
    			if (!response.IsSuccessStatusCode)
    			{
    				throw new HttpRequestException($"HTTP {response.StatusCode}");
    			}

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var rootElement = JsonDocument.Parse(json).RootElement;

                // Check for error response
                if (rootElement.TryGetProperty("error", out _))
                {
                    throw new InvalidOperationException($"SquidWTF playlist response for {externalId} contained an error payload");
                }

                // Response structure: { "playlist": { playlist object }, "items": [ track wrappers ] }
    			// Extract the playlist object from the response
    			if (!rootElement.TryGetProperty("playlist", out var playlistElement))
    			{
    				throw new InvalidOperationException($"SquidWTF playlist response for {externalId} did not contain playlist");
    			}

    			return ParseTidalPlaylist(playlistElement);
    		}, (ExternalPlaylist?)null);
    	}

    public async Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default)
	{
		if (externalProvider != "squidwtf") return new List<Song>();

		return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
		{
            // Note: hifi-api doesn't document playlist endpoint, but /playlist/?id={playlistId} is commonly used
			var url = $"{baseUrl}/playlist/?id={externalId}";
			var response = await _httpClient.GetAsync(url, cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				throw new HttpRequestException($"HTTP {response.StatusCode}");
			}

			var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var playlistElement = JsonDocument.Parse(json).RootElement;

            // Check for error response
			if (playlistElement.TryGetProperty("error", out _))
			{
				throw new InvalidOperationException($"SquidWTF playlist tracks response for {externalId} contained an error payload");
			}

			JsonElement? playlist = null;
			JsonElement? tracks = null;

            // Response structure: { "playlist": { playlist object }, "items": [ track wrappers ] }
			if (playlistElement.TryGetProperty("playlist", out var playlistEl))
			{
				playlist = playlistEl;
			}

			if (playlistElement.TryGetProperty("items", out var tracksEl))
			{
				tracks = tracksEl;
			}

            if (!tracks.HasValue)
            {
                throw new InvalidOperationException(
                    $"SquidWTF playlist tracks response for {externalId} did not contain items");
            }

			var songs = new List<Song>();

			// Get playlist name for album field
			var playlistName = playlist?.TryGetProperty("title", out var titleEl) == true
				? titleEl.GetString() ?? "Unknown Playlist"
				: "Unknown Playlist";

			if (tracks.HasValue)
			{
				int trackIndex = 1;
				foreach (var entry in tracks.Value.EnumerateArray())
				{
                    // Each item is wrapped: { "item": { track object } }
					if (!entry.TryGetProperty("item", out var track))
						continue;

					// For playlists, use the track's own artist (not a single album artist)
					var song = ParseTidalTrack(track, trackIndex);

					// Override album name to be the playlist name
					song.Album = playlistName;

					// Playlists should not have disc numbers - always set to null
					// This prevents Jellyfin from splitting the playlist into multiple "discs"
					song.DiscNumber = null;

					if (ExplicitContentFilter.ShouldIncludeSong(song, _settings.ExplicitFilter))
					{
						songs.Add(song);
					}
					trackIndex++;
				}
			}
			return songs;
		}, new List<Song>());
	}

	// --- Parser functions start here ---

    private static string? BuildTidalImageUrl(string? imageId, string size)
    {
        if (string.IsNullOrWhiteSpace(imageId))
        {
            return null;
        }

        return $"https://resources.tidal.com/images/{imageId.Replace("-", "/")}/{size}.jpg";
    }

    /// <summary>
    /// Parses a Tidal track object from hifi-api search/album/playlist responses.
    /// Per hifi-api spec, track objects contain: id, title, duration, trackNumber, volumeNumber,
    /// explicit, artist (singular), artists (array), album (object with id, title, cover).
    /// </summary>
    /// <param name="track">JSON element containing track data</param>
    /// <param name="fallbackTrackNumber">Optional track number to use if not present in JSON</param>
    /// <returns>Parsed Song object</returns>
    private Song ParseTidalTrack(JsonElement track, int? fallbackTrackNumber = null)
    {
        var externalId = track.GetProperty("id").GetInt64().ToString();

		int? explicitContentLyrics =
			track.TryGetProperty("explicit", out var ecl) && ecl.ValueKind == JsonValueKind.True
				? 1
				: 0;

        var title = track.GetProperty("title").GetString() ?? "";
        if (track.TryGetProperty("version", out var version))
        {
            var versionStr = version.GetString();
            if (!string.IsNullOrWhiteSpace(versionStr))
            {
                title = $"{title} ({versionStr})";
            }
        }

        int? trackNumber = track.TryGetProperty("trackNumber", out var trackNum) && trackNum.ValueKind == JsonValueKind.Number
            ? trackNum.GetInt32()
            : fallbackTrackNumber;

        int? discNumber = track.TryGetProperty("volumeNumber", out var volNum) && volNum.ValueKind == JsonValueKind.Number
            ? volNum.GetInt32()
            : null;

        int? bpm = track.TryGetProperty("bpm", out var bpmVal) && bpmVal.ValueKind == JsonValueKind.Number
            ? (int)bpmVal.GetDouble()
            : null;

        string? isrc = track.TryGetProperty("isrc", out var isrcVal) && isrcVal.ValueKind == JsonValueKind.String
            ? isrcVal.GetString()
            : null;

        string? releaseDate = track.TryGetProperty("streamStartDate", out var streamStartDate) && streamStartDate.ValueKind == JsonValueKind.String
            ? streamStartDate.GetString()
            : null;
        int? year = ParseYearFromDateString(releaseDate);

        // Get all artists - Tidal provides both "artist" (singular) and "artists" (plural array)
        var allArtists = new List<string>();
        var allArtistIds = new List<string>();
        string artistName = "";
        string? artistId = null;

        // Prefer the "artists" array as it includes all collaborators
        if (track.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array && artists.GetArrayLength() > 0)
        {
            foreach (var artistEl in artists.EnumerateArray())
            {
                if (!artistEl.TryGetProperty("name", out var nameElement) ||
                    !artistEl.TryGetProperty("id", out var idElement))
                {
                    continue;
                }

                var name = nameElement.GetString();
                var id = GetIdAsString(idElement);
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                {
                    allArtists.Add(name);
                    allArtistIds.Add(BuildExternalArtistId("squidwtf", id));
                }
            }

            if (allArtists.Count > 0)
            {
                artistName = allArtists[0];
                artistId = allArtistIds[0];
            }
        }
        // Fallback to singular "artist" field
        else if (track.TryGetProperty("artist", out var artist))
        {
            artistName = artist.TryGetProperty("name", out var artistNameEl) ? artistNameEl.GetString() ?? "" : "";
            if (artist.TryGetProperty("id", out var artistIdEl))
            {
                var externalArtistId = GetIdAsString(artistIdEl);
                if (!string.IsNullOrWhiteSpace(externalArtistId))
                {
                    artistId = BuildExternalArtistId("squidwtf", externalArtistId);
                }
            }

            if (!string.IsNullOrWhiteSpace(artistName))
            {
                allArtists.Add(artistName);
            }

            if (!string.IsNullOrWhiteSpace(artistId))
            {
                allArtistIds.Add(artistId);
            }
        }

        var contributors = allArtists
            .Skip(1)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Get album info
        string albumTitle = "";
        string? albumId = null;
        string? coverArt = null;
        string? coverArtLarge = null;
        string? albumArtist = null;
        int? totalTracks = null;
        string? copyright = track.TryGetProperty("copyright", out var copyrightVal) && copyrightVal.ValueKind == JsonValueKind.String
            ? copyrightVal.GetString()
            : null;

        if (track.TryGetProperty("album", out var album))
        {
            if (album.TryGetProperty("title", out var albumTitleEl))
            {
                albumTitle = albumTitleEl.GetString() ?? "";
            }

            if (album.TryGetProperty("id", out var albumIdEl))
            {
                var externalAlbumId = GetIdAsString(albumIdEl);
                if (!string.IsNullOrWhiteSpace(externalAlbumId))
                {
                    albumId = BuildExternalAlbumId("squidwtf", externalAlbumId);
                }
            }

            if (album.TryGetProperty("cover", out var cover))
            {
                var coverId = cover.GetString();
                coverArt = BuildTidalImageUrl(coverId, "320x320");
                coverArtLarge = BuildTidalImageUrl(coverId, "1280x1280");
            }

            if (album.TryGetProperty("numberOfTracks", out var numberOfTracks) && numberOfTracks.ValueKind == JsonValueKind.Number)
            {
                totalTracks = numberOfTracks.GetInt32();
            }

            if (album.TryGetProperty("releaseDate", out var albumReleaseDate) && albumReleaseDate.ValueKind == JsonValueKind.String)
            {
                var albumReleaseDateValue = albumReleaseDate.GetString();
                if (!string.IsNullOrWhiteSpace(albumReleaseDateValue))
                {
                    releaseDate = albumReleaseDateValue;
                    year = ParseYearFromDateString(albumReleaseDateValue);
                }
            }

            if (album.TryGetProperty("artist", out var albumArtistEl) &&
                albumArtistEl.TryGetProperty("name", out var albumArtistNameEl))
            {
                albumArtist = albumArtistNameEl.GetString();
            }
            else if (album.TryGetProperty("artists", out var albumArtistsEl) &&
                     albumArtistsEl.ValueKind == JsonValueKind.Array &&
                     albumArtistsEl.GetArrayLength() > 0 &&
                     albumArtistsEl[0].TryGetProperty("name", out var firstAlbumArtistNameEl))
            {
                albumArtist = firstAlbumArtistNameEl.GetString();
            }

            if (string.IsNullOrWhiteSpace(copyright) &&
                album.TryGetProperty("copyright", out var albumCopyright) &&
                albumCopyright.ValueKind == JsonValueKind.String)
            {
                copyright = albumCopyright.GetString();
            }
        }

        if (string.IsNullOrWhiteSpace(albumArtist))
        {
            albumArtist = artistName;
        }

        return new Song
        {
            Id = BuildExternalSongId("squidwtf", externalId),
            Title = title,
            Artist = artistName,
            ArtistId = artistId,
            Artists = allArtists,
            ArtistIds = allArtistIds,
            Album = albumTitle,
            AlbumId = albumId,
            AlbumArtist = albumArtist,
            Duration = track.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number
                ? duration.GetInt32()
                : null,
            Track = trackNumber,
            DiscNumber = discNumber,
            TotalTracks = totalTracks,
            Year = year,
            ReleaseDate = releaseDate,
            Bpm = bpm,
            Isrc = isrc,
            CoverArtUrl = coverArt,
            CoverArtUrlLarge = coverArtLarge,
            Contributors = contributors,
            Copyright = copyright,
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = externalId,
            ExplicitContentLyrics = explicitContentLyrics
        };
    }

    /// <summary>
    /// Parses a full Tidal track object from hifi-api /info/ endpoint.
    /// Per hifi-api spec, full track objects include additional metadata: bpm, isrc, key, keyScale,
    /// streamStartDate (for year), copyright, replayGain, peak, audioQuality, audioModes.
    /// </summary>
    /// <param name="track">JSON element containing full track data</param>
    /// <returns>Parsed Song object with extended metadata</returns>
    private Song ParseTidalTrackFull(JsonElement track)
    {
        // Full track payloads include all fields handled by ParseTidalTrack.
        return ParseTidalTrack(track);
    }

    /// <summary>
    /// Parses a Tidal album object from hifi-api responses.
    /// Per hifi-api spec, album objects contain: id, title, releaseDate, numberOfTracks,
    /// cover (UUID), artist (object) or artists (array).
    /// </summary>
    /// <param name="album">JSON element containing album data</param>
    /// <returns>Parsed Album object</returns>
    private Album ParseTidalAlbum(JsonElement album)
    {
        var externalId = album.GetProperty("id").GetInt64().ToString();

        var title = album.GetProperty("title").GetString() ?? "";
        if (album.TryGetProperty("version", out var version))
        {
            var versionStr = version.GetString();
            if (!string.IsNullOrWhiteSpace(versionStr))
            {
                title = $"{title} ({versionStr})";
            }
        }

        int? year = null;
        if (album.TryGetProperty("releaseDate", out var releaseDate))
        {
            year = ParseYearFromDateString(releaseDate.GetString());
        }
        else if (album.TryGetProperty("streamStartDate", out var streamStartDate))
        {
            year = ParseYearFromDateString(streamStartDate.GetString());
        }

        string? coverArt = null;
        if (album.TryGetProperty("cover", out var cover))
        {
            coverArt = BuildTidalImageUrl(cover.GetString(), "320x320");
        }

        // Get artist name
        string artistName = "";
        string? artistId = null;
        if (album.TryGetProperty("artist", out var artist))
        {
            artistName = artist.GetProperty("name").GetString() ?? "";
            artistId = BuildExternalArtistId("squidwtf", GetIdAsString(artist.GetProperty("id")));
        }
        else if (album.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
        {
            artistName = artists[0].GetProperty("name").GetString() ?? "";
            artistId = BuildExternalArtistId("squidwtf", GetIdAsString(artists[0].GetProperty("id")));
        }

        return new Album
        {
            Id = BuildExternalAlbumId("squidwtf", externalId),
            Title = title,
            Artist = artistName,
            ArtistId = artistId,
            Year = year,
            SongCount = album.TryGetProperty("numberOfTracks", out var trackCount) && trackCount.ValueKind == JsonValueKind.Number
                ? trackCount.GetInt32()
                : null,
            CoverArtUrl = coverArt,
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = externalId
        };
    }

	/// <summary>
    /// Parses a Tidal artist object from hifi-api responses.
    /// Per hifi-api spec, artist objects contain: id, name, picture (UUID).
    /// Note: albums_count is not in the standard API response but is added by GetArtistAsync.
    /// </summary>
    /// <param name="artist">JSON element containing artist data</param>
    /// <returns>Parsed Artist object</returns>
    private Artist ParseTidalArtist(JsonElement artist)
    {
        var externalId = artist.GetProperty("id").GetInt64().ToString();
        var artistName = artist.GetProperty("name").GetString() ?? "";

        var imageUrl = artist.TryGetProperty("picture", out var picture)
            ? BuildTidalImageUrl(picture.GetString(), "320x320")
            : null;

        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            _logger.LogDebug("Artist {ArtistName} picture: {ImageUrl}", artistName, imageUrl);
        }

        return new Artist
        {
            Id = BuildExternalArtistId("squidwtf", externalId),
            Name = artistName,
            ImageUrl = imageUrl,
			AlbumCount = artist.TryGetProperty("albums_count", out var albumsCount)
				? albumsCount.GetInt32()
				: null,
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = externalId
        };
    }

	/// <summary>
    /// Parses a Tidal playlist from hifi-api /playlist/ endpoint response.
    /// Per hifi-api spec (undocumented), response structure is:
    /// { "playlist": { uuid, title, description, creator, created, numberOfTracks, duration, squareImage },
    ///   "items": [ { "item": { track object } } ] }
    /// </summary>
    /// <param name="playlistElement">Root JSON element containing playlist and items</param>
    /// <returns>Parsed ExternalPlaylist object</returns>
    private ExternalPlaylist ParseTidalPlaylist(JsonElement playlistElement)
    	{
    		// The playlistElement IS the playlist data directly from the API
    		// No need to look for a "playlist" property wrapper

    		var externalId = playlistElement.GetProperty("uuid").GetString()!;

            // Get curator/creator name
            string? curatorName = null;
            if (playlistElement.TryGetProperty("creator", out var creator))
            {
                // Try to get the name first, fall back to id if name doesn't exist
                if (creator.TryGetProperty("name", out var name))
                {
                    curatorName = name.GetString();
                }
                else if (creator.TryGetProperty("id", out var id))
                {
                    // Handle both string and number types for creator.id
                    var idValue = id.ValueKind == JsonValueKind.Number
                        ? id.GetInt32().ToString()
                        : id.GetString();

                    // If creator ID is 0/empty, treat as unknown and allow promotedArtists fallback.
                    if (idValue == "0" || string.IsNullOrEmpty(idValue))
                    {
                        curatorName = null;
                    }
                    else
                    {
                        curatorName = idValue;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(curatorName) &&
                playlistElement.TryGetProperty("promotedArtists", out var promotedArtists) &&
                promotedArtists.ValueKind == JsonValueKind.Array &&
                promotedArtists.GetArrayLength() > 0 &&
                promotedArtists[0].TryGetProperty("name", out var promotedArtistName))
            {
                curatorName = promotedArtistName.GetString();
            }

            // Final fallback: if still no curator name, use TIDAL
            if (string.IsNullOrEmpty(curatorName))
            {
                curatorName = "TIDAL";
            }

    		// Get creation date
            DateTime? createdDate = null;
            if (playlistElement.TryGetProperty("created", out var creationDateEl))
            {
                var dateStr = creationDateEl.GetString();
                if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var date))
                {
                    createdDate = date;
                }
            }

            if (createdDate == null &&
                playlistElement.TryGetProperty("lastUpdated", out var lastUpdatedEl) &&
                DateTime.TryParse(lastUpdatedEl.GetString(), out var lastUpdatedDate))
            {
                createdDate = lastUpdatedDate;
            }

            if (createdDate == null &&
                playlistElement.TryGetProperty("lastItemAddedAt", out var lastItemAddedAtEl) &&
                DateTime.TryParse(lastItemAddedAtEl.GetString(), out var lastItemAddedAtDate))
            {
                createdDate = lastItemAddedAtDate;
            }

    		// Get playlist image URL
    		string? imageUrl = null;
            if (playlistElement.TryGetProperty("squareImage", out var picture))
            {
                imageUrl = BuildTidalImageUrl(picture.GetString(), "1080x1080");
            }

            if (string.IsNullOrWhiteSpace(imageUrl) &&
                playlistElement.TryGetProperty("image", out var image))
            {
                imageUrl = BuildTidalImageUrl(image.GetString(), "1080x1080");
            }

    		return new ExternalPlaylist
            {
                Id = Common.PlaylistIdHelper.CreatePlaylistId("squidwtf", externalId),
                Name = playlistElement.GetProperty("title").GetString() ?? "",
                Description = playlistElement.TryGetProperty("description", out var desc)
                    ? desc.GetString()
                    : null,
                CuratorName = curatorName,
                Provider = "squidwtf",
                ExternalId = externalId,
                TrackCount = playlistElement.TryGetProperty("numberOfTracks", out var nbTracks)
                    ? nbTracks.GetInt32()
                    : 0,
                Duration = playlistElement.TryGetProperty("duration", out var duration)
                    ? duration.GetInt32()
                    : 0,
                CoverUrl = imageUrl,
                CreatedDate = createdDate
            };

    	}

    /// <summary>
    /// Determines whether a song should be included based on the explicit content filter setting
    /// </summary>
    /// <param name="song">The song to check</param>
    /// <returns>True if the song should be included, false otherwise</returns>
    private bool ShouldIncludeSong(Song song)
    {
        // If no explicit content info, include the song
        if (song.ExplicitContentLyrics == null)
            return true;

        return _settings.ExplicitFilter switch
        {
            // All: No filtering, include everything
            ExplicitFilter.All => true,

            // ExplicitOnly: Exclude clean/edited versions (value 3)
            // Include: 0 (naturally clean), 1 (explicit), 2 (not applicable), 6/7 (unknown)
            ExplicitFilter.ExplicitOnly => song.ExplicitContentLyrics != 3,

            // CleanOnly: Only show clean content
            // Include: 0 (naturally clean), 3 (clean/edited version)
            // Exclude: 1 (explicit)
            ExplicitFilter.CleanOnly => song.ExplicitContentLyrics != 1,

            _ => true
        };
    }

    /// <summary>
    /// Searches for multiple songs in parallel across all available endpoints.
    /// Each endpoint processes songs sequentially. Failed endpoints are blacklisted.
    /// </summary>
    public async Task<List<Song?>> SearchSongsInParallelAsync(List<string> queries, int limit = 10, CancellationToken cancellationToken = default)
    {
        return await _fallbackHelper.ProcessInParallelAsync(
            queries,
            async (baseUrl, query, ct) =>
            {
                try
                {
                    var url = $"{baseUrl}/search/?s={Uri.EscapeDataString(query)}";
                    var response = await _httpClient.GetAsync(url, ct);

                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    var json = await response.Content.ReadAsStringAsync(ct);
                    var result = JsonDocument.Parse(json);

                    if (result.RootElement.TryGetProperty("detail", out _) ||
                        result.RootElement.TryGetProperty("error", out _))
                    {
                        return null;
                    }

                    if (result.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("items", out var items))
                    {
                        foreach (var track in items.EnumerateArray())
                        {
                            var song = ParseTidalTrack(track);
                            if (ExplicitContentFilter.ShouldIncludeSong(song, _settings.ExplicitFilter))
                            {
                                return song; // Return first matching song
                            }
                        }
                    }

                    return null;
                }
                catch
                {
                    throw; // Let the parallel processor handle blacklisting
                }
            },
            cancellationToken);
    }

}
