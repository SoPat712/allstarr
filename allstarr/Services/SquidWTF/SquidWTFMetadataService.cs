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

public class SquidWTFMetadataService : IMusicMetadataService
{
    private readonly HttpClient _httpClient;
    private readonly SubsonicSettings _settings;
    private readonly ILogger<SquidWTFMetadataService> _logger;
    private readonly RedisCacheService _cache;
    private readonly RoundRobinFallbackHelper _fallbackHelper;

    public SquidWTFMetadataService(
        IHttpClientFactory httpClientFactory, 
        IOptions<SubsonicSettings> settings,
        IOptions<SquidWTFSettings> squidwtfSettings,
        ILogger<SquidWTFMetadataService> logger,
        RedisCacheService cache,
        List<string> apiUrls)
    {
        _httpClient = httpClientFactory.CreateClient();
        _settings = settings.Value;
        _logger = logger;
        _cache = cache;
        _fallbackHelper = new RoundRobinFallbackHelper(apiUrls, logger, "SquidWTF");
        
        // Set up default headers
        _httpClient.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:83.0) Gecko/20100101 Firefox/83.0");
        
        // Increase timeout for large artist/album responses (some artists have 100+ albums)
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
    }
    
    
	
    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
    {
        // Race all endpoints for fastest search results
        return await _fallbackHelper.RaceAllEndpointsAsync(async (baseUrl, ct) =>
        {
            // Use 's' parameter for track search as per hifi-api spec
            var url = $"{baseUrl}/search/?s={Uri.EscapeDataString(query)}";
            var response = await _httpClient.GetAsync(url, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {response.StatusCode}");
            }
            
            var json = await response.Content.ReadAsStringAsync(ct);
            
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
                    if (ShouldIncludeSong(song))
                    {
                        songs.Add(song);
                    }
                    count++;
                }
            }
            return songs;
        });
    }
	
    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20)
    {
        // Race all endpoints for fastest search results
        return await _fallbackHelper.RaceAllEndpointsAsync(async (baseUrl, ct) =>
        {
            // Note: hifi-api doesn't document album search, but 'al' parameter is commonly used
            var url = $"{baseUrl}/search/?al={Uri.EscapeDataString(query)}";
            var response = await _httpClient.GetAsync(url, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {response.StatusCode}");
            }
            
            var json = await response.Content.ReadAsStringAsync(ct);
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
            
            return albums;
        });
    }

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20)
    {
        // Race all endpoints for fastest search results
        return await _fallbackHelper.RaceAllEndpointsAsync(async (baseUrl, ct) =>
        {
            // Per hifi-api spec: use 'a' parameter for artist search
            var url = $"{baseUrl}/search/?a={Uri.EscapeDataString(query)}";
            _logger.LogInformation("🔍 SQUIDWTF: Searching artists with URL: {Url}", url);
            
            var response = await _httpClient.GetAsync(url, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("⚠️ SQUIDWTF: Artist search failed with status {StatusCode}", response.StatusCode);
                throw new HttpRequestException($"HTTP {response.StatusCode}");
            }
            
            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonDocument.Parse(json);
            
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

            _logger.LogInformation("✓ SQUIDWTF: Artist search returned {Count} results", artists.Count);
            return artists;
        });
    }
	
	public async Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20)
	{
		return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
		{
            // Per hifi-api spec: use 'p' parameter for playlist search
			var url = $"{baseUrl}/search/?p={Uri.EscapeDataString(query)}";
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return new List<ExternalPlaylist>();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(json);

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
						_logger.LogDebug(ex, "Failed to parse playlist, skipping");
						// Skip this playlist and continue with others
					}
				}
			}
			return playlists;
		}, new List<ExternalPlaylist>());
	}

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
    {
        // Execute searches in parallel
        var songsTask = SearchSongsAsync(query, songLimit);
        var albumsTask = SearchAlbumsAsync(query, albumLimit);
        var artistsTask = SearchArtistsAsync(query, artistLimit);
        
        await Task.WhenAll(songsTask, albumsTask, artistsTask);
		
		var temp = new SearchResult
        {			
            Songs = await songsTask,
            Albums = await albumsTask,
            Artists = await artistsTask
        };

		return temp;
    }

    public async Task<Song?> GetSongAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "squidwtf") return null;
        
        return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
        {
            // Per hifi-api spec: GET /info/?id={trackId} returns track metadata
            var url = $"{baseUrl}/info/?id={externalId}";
						
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(json);
            
            // Per hifi-api spec: response is { "version": "2.0", "data": { track object } }
			if (!result.RootElement.TryGetProperty("data", out var track))
				return null;

			var song = ParseTidalTrackFull(track);
			
			// NOTE: Spotify ID conversion happens during download (in SquidWTFDownloadService)
			// This avoids redundant conversions and ensures it's done in parallel with the download
			
			return song;
        }, (Song?)null);
    }

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "squidwtf") return null;
        
        // Try cache first
        var cacheKey = $"squidwtf:album:{externalId}";
        var cached = await _cache.GetAsync<Album>(cacheKey);
        if (cached != null) return cached;
        
        return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
        {
            // Note: hifi-api doesn't document album endpoint, but /album/?id={albumId} is commonly used
            var url = $"{baseUrl}/album/?id={externalId}";
			
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;
            
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonDocument.Parse(json);
            
			// Response structure: { "data": { album object with "items" array of tracks } }
			if (!result.RootElement.TryGetProperty("data", out var albumElement))
				return null;

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
						if (ShouldIncludeSong(song))
						{
							album.Songs.Add(song);
						}
					}
				}
			}
			
			// Cache for 24 hours
			await _cache.SetAsync(cacheKey, album, TimeSpan.FromHours(24));
			
			return album;	
		}, (Album?)null);
    }
	
    public async Task<Artist?> GetArtistAsync(string externalProvider, string externalId)
    {
        if (externalProvider != "squidwtf") return null;
        
        _logger.LogInformation("GetArtistAsync called for SquidWTF artist {ExternalId}", externalId);
        
        // Try cache first
        var cacheKey = $"squidwtf:artist:{externalId}";
        var cached = await _cache.GetAsync<Artist>(cacheKey);
        if (cached != null)
        {
            _logger.LogInformation("Returning cached artist {ArtistName}", cached.Name);
            return cached;
        }
  
        return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
        {
            // Note: hifi-api doesn't document artist endpoint, but /artist/?f={artistId} is commonly used
            var url = $"{baseUrl}/artist/?f={externalId}"; 
            _logger.LogInformation("Fetching artist from {Url}", url);

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("SquidWTF artist request failed with status {StatusCode}", response.StatusCode);
                return null;
            }
            
            var json = await response.Content.ReadAsStringAsync();
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
					_logger.LogInformation("Found artist from albums, albumCount={AlbumCount}", albumCount);
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
                _logger.LogDebug("Could not find artist data in response. Response keys: {Keys}", 
                    string.Join(", ", result.RootElement.EnumerateObject().Select(p => p.Name)));
                return null;
            }

			var artistElement = artistSource.Value;
            // Normalize artist data to include album count
			var normalizedArtist = new JsonObject
			{
				["id"] = artistElement.GetProperty("id").GetInt64(),
				["name"] = artistElement.GetProperty("name").GetString(),
				["albums_count"] = albumCount,
				["picture"] = artistElement.GetProperty("picture").GetString()
			};

			using var doc = JsonDocument.Parse(normalizedArtist.ToJsonString());
			var artist = ParseTidalArtist(doc.RootElement);
			
            _logger.LogInformation("Successfully parsed artist {ArtistName} with {AlbumCount} albums", artist.Name, albumCount);
            
			// Cache for 24 hours
			await _cache.SetAsync(cacheKey, artist, TimeSpan.FromHours(24));
			
			return artist;
        }, (Artist?)null);
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId)
    {
		if (externalProvider != "squidwtf") return new List<Album>();
		
		return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
		{
            _logger.LogInformation("GetArtistAlbumsAsync called for SquidWTF artist {ExternalId}", externalId);
            
            // Note: hifi-api doesn't document artist endpoint, but /artist/?f={artistId} is commonly used
			var url = $"{baseUrl}/artist/?f={externalId}";
			_logger.LogInformation("Fetching artist albums from URL: {Url}", url);
			var response = await _httpClient.GetAsync(url);
			
			if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("SquidWTF artist albums request failed with status {StatusCode}", response.StatusCode);
                return new List<Album>();
            }
			
			var json = await response.Content.ReadAsStringAsync();
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
                _logger.LogInformation("Found {AlbumCount} albums for artist {ExternalId}", albums.Count, externalId);
			}
            else
            {
                _logger.LogWarning("No albums found in response for artist {ExternalId}", externalId);
            }
			
			return albums;
		}, new List<Album>());
	}

    public async Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId)
	{
		if (externalProvider != "squidwtf") return null;
		
		return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
		{
            // Note: hifi-api doesn't document playlist endpoint, but /playlist/?id={playlistId} is commonly used
			var url = $"{baseUrl}/playlist/?id={externalId}";
			var response = await _httpClient.GetAsync(url);
			if (!response.IsSuccessStatusCode) return null;
			
            var json = await response.Content.ReadAsStringAsync();
            var playlistElement = JsonDocument.Parse(json).RootElement;
			
            // Check for error response
            if (playlistElement.TryGetProperty("error", out _)) return null;
            
            // Response structure: { "playlist": { playlist object }, "items": [ track wrappers ] }
			return ParseTidalPlaylist(playlistElement);
		}, (ExternalPlaylist?)null);
	}
	
    public async Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId)
	{
		if (externalProvider != "squidwtf") return new List<Song>();
		
		return await _fallbackHelper.TryWithFallbackAsync(async (baseUrl) =>
		{
            // Note: hifi-api doesn't document playlist endpoint, but /playlist/?id={playlistId} is commonly used
			var url = $"{baseUrl}/playlist/?id={externalId}";
			var response = await _httpClient.GetAsync(url);
			if (!response.IsSuccessStatusCode) return new List<Song>();
			
			var json = await response.Content.ReadAsStringAsync();
            var playlistElement = JsonDocument.Parse(json).RootElement;
            
            // Check for error response
			if (playlistElement.TryGetProperty("error", out _)) return new List<Song>();
			
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
					
					if (ShouldIncludeSong(song))
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

		// Explicit content lyrics value - idk if this will work
		int? explicitContentLyrics =
			track.TryGetProperty("explicit", out var ecl) && ecl.ValueKind == JsonValueKind.True
				? 1
				: 0;
        
        int? trackNumber = track.TryGetProperty("trackNumber", out var trackNum) 
            ? trackNum.GetInt32() 
            : fallbackTrackNumber;
        
        int? discNumber = track.TryGetProperty("volumeNumber", out var volNum)
            ? volNum.GetInt32()
            : null;
        
<<<<<<< HEAD
        // Get all artists - Tidal provides both "artist" (singular) and "artists" (plural array)
        var allArtists = new List<string>();
||||||| bc4e5d9
        // Get artist name - handle both single artist and artists array
=======
        // Get all artists - Tidal provides both "artist" (singular) and "artists" (plural array)
        var allArtists = new List<string>();
        var allArtistIds = new List<string>();
>>>>>>> dev
        string artistName = "";
<<<<<<< HEAD
        string? artistId = null;
        
        // Prefer the "artists" array as it includes all collaborators
        if (track.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
        {
            foreach (var artistEl in artists.EnumerateArray())
            {
                var name = artistEl.GetProperty("name").GetString();
                if (!string.IsNullOrEmpty(name))
                {
                    allArtists.Add(name);
                }
            }
            
            // First artist is the main artist
            if (allArtists.Count > 0)
            {
                artistName = allArtists[0];
                artistId = $"ext-squidwtf-artist-{artists[0].GetProperty("id").GetInt64()}";
            }
        }
        // Fallback to singular "artist" field
        else if (track.TryGetProperty("artist", out var artist))
||||||| bc4e5d9
        if (track.TryGetProperty("artist", out var artist))
=======
        string? artistId = null;
        
        // Prefer the "artists" array as it includes all collaborators
        if (track.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
        {
            foreach (var artistEl in artists.EnumerateArray())
            {
                var name = artistEl.GetProperty("name").GetString();
                var id = artistEl.GetProperty("id").GetInt64();
                if (!string.IsNullOrEmpty(name))
                {
                    allArtists.Add(name);
                    allArtistIds.Add($"ext-squidwtf-artist-{id}");
                }
            }
            
            // First artist is the main artist
            if (allArtists.Count > 0)
            {
                artistName = allArtists[0];
                artistId = allArtistIds[0];
            }
        }
        // Fallback to singular "artist" field
        else if (track.TryGetProperty("artist", out var artist))
>>>>>>> dev
        {
            artistName = artist.GetProperty("name").GetString() ?? "";
<<<<<<< HEAD
            artistId = $"ext-squidwtf-artist-{artist.GetProperty("id").GetInt64()}";
            allArtists.Add(artistName);
||||||| bc4e5d9
        }
        else if (track.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
        {
            artistName = artists[0].GetProperty("name").GetString() ?? "";
        }
        
        // Get artist ID
        string? artistId = null;
        if (track.TryGetProperty("artist", out var artistForId))
        {
            artistId = $"ext-squidwtf-artist-{artistForId.GetProperty("id").GetInt64()}";
        }
        else if (track.TryGetProperty("artists", out var artistsForId) && artistsForId.GetArrayLength() > 0)
        {
            artistId = $"ext-squidwtf-artist-{artistsForId[0].GetProperty("id").GetInt64()}";
=======
            artistId = $"ext-squidwtf-artist-{artist.GetProperty("id").GetInt64()}";
            allArtists.Add(artistName);
            allArtistIds.Add(artistId);
>>>>>>> dev
        }
        
        // Get album info
        string albumTitle = "";
        string? albumId = null;
        string? coverArt = null;
        
        if (track.TryGetProperty("album", out var album))
        {
            albumTitle = album.GetProperty("title").GetString() ?? "";
            albumId = $"ext-squidwtf-album-{album.GetProperty("id").GetInt64()}";
            
            if (album.TryGetProperty("cover", out var cover))
            {
                var coverGuid = cover.GetString()?.Replace("-", "/");
                coverArt = $"https://resources.tidal.com/images/{coverGuid}/320x320.jpg";
            }
        }
        
        return new Song
        {
            Id = $"ext-squidwtf-song-{externalId}",
            Title = track.GetProperty("title").GetString() ?? "",
            Artist = artistName,
            ArtistId = artistId,
<<<<<<< HEAD
            Artists = allArtists,
||||||| bc4e5d9
=======
            Artists = allArtists,
            ArtistIds = allArtistIds,
>>>>>>> dev
            Album = albumTitle,
            AlbumId = albumId,
            Duration = track.TryGetProperty("duration", out var duration) 
                ? duration.GetInt32() 
                : null,
            Track = trackNumber,
            DiscNumber = discNumber,
            CoverArtUrl = coverArt,
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
        var externalId = track.GetProperty("id").GetInt64().ToString();

		// Explicit content lyrics value - idk if this will work
		int? explicitContentLyrics =
			track.TryGetProperty("explicit", out var ecl) && ecl.ValueKind == JsonValueKind.True
				? 1
				: 0;

		
        int? trackNumber = track.TryGetProperty("trackNumber", out var trackNum) 
            ? trackNum.GetInt32() 
            : null;
        
        int? discNumber = track.TryGetProperty("volumeNumber", out var volNum)
            ? volNum.GetInt32()
            : null;
        
        int? bpm = track.TryGetProperty("bpm", out var bpmVal) && bpmVal.ValueKind == JsonValueKind.Number
            ? bpmVal.GetInt32() 
            : null;
        
        string? isrc = track.TryGetProperty("isrc", out var isrcVal) 
            ? isrcVal.GetString() 
            : null;
        
        int? year = null;
        if (track.TryGetProperty("streamStartDate", out var streamDate))
        {
            var dateStr = streamDate.GetString();
            if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 4)
            {
                if (int.TryParse(dateStr.Substring(0, 4), out var y))
                    year = y;
            }
        }
        
<<<<<<< HEAD
        // Get all artists - prefer "artists" array for collaborations
        var allArtists = new List<string>();
        string artistName = "";
        long artistIdNum = 0;
        
        if (track.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
        {
            foreach (var artistEl in artists.EnumerateArray())
            {
                var name = artistEl.GetProperty("name").GetString();
                if (!string.IsNullOrEmpty(name))
                {
                    allArtists.Add(name);
                }
            }
            
            if (allArtists.Count > 0)
            {
                artistName = allArtists[0];
                artistIdNum = artists[0].GetProperty("id").GetInt64();
            }
        }
        else if (track.TryGetProperty("artist", out var artist))
        {
            artistName = artist.GetProperty("name").GetString() ?? "";
            artistIdNum = artist.GetProperty("id").GetInt64();
            allArtists.Add(artistName);
        }
||||||| bc4e5d9
        // Get artist info
        string artistName = track.GetProperty("artist").GetProperty("name").GetString() ?? "";
        long artistIdNum = track.GetProperty("artist").GetProperty("id").GetInt64();
=======
        // Get all artists - prefer "artists" array for collaborations
        var allArtists = new List<string>();
        var allArtistIds = new List<string>();
        string artistName = "";
        long artistIdNum = 0;
        
        if (track.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
        {
            foreach (var artistEl in artists.EnumerateArray())
            {
                var name = artistEl.GetProperty("name").GetString();
                var id = artistEl.GetProperty("id").GetInt64();
                if (!string.IsNullOrEmpty(name))
                {
                    allArtists.Add(name);
                    allArtistIds.Add($"ext-squidwtf-artist-{id}");
                }
            }
            
            if (allArtists.Count > 0)
            {
                artistName = allArtists[0];
                artistIdNum = artists[0].GetProperty("id").GetInt64();
            }
        }
        else if (track.TryGetProperty("artist", out var artist))
        {
            artistName = artist.GetProperty("name").GetString() ?? "";
            artistIdNum = artist.GetProperty("id").GetInt64();
            allArtists.Add(artistName);
            allArtistIds.Add($"ext-squidwtf-artist-{artistIdNum}");
        }
>>>>>>> dev
        
        // Album artist - same as main artist for Tidal tracks
        string? albumArtist = artistName;
        
        // Get album info
        var album = track.GetProperty("album");
        string albumTitle = album.GetProperty("title").GetString() ?? "";
        long albumIdNum = album.GetProperty("id").GetInt64();
        
        // Cover art URLs
        string? coverArt = null;
        string? coverArtLarge = null;
        if (album.TryGetProperty("cover", out var cover))
        {
            var coverGuid = cover.GetString()?.Replace("-", "/");
            coverArt = $"https://resources.tidal.com/images/{coverGuid}/320x320.jpg";
            coverArtLarge = $"https://resources.tidal.com/images/{coverGuid}/1280x1280.jpg";
        }
        
        // Copyright
        string? copyright = track.TryGetProperty("copyright", out var copyrightVal)
            ? copyrightVal.GetString()
            : null;
        
        // Explicit content
        bool isExplicit = track.TryGetProperty("explicit", out var explicitVal) && explicitVal.GetBoolean();
        
        return new Song
        {
            Id = $"ext-squidwtf-song-{externalId}",
            Title = track.GetProperty("title").GetString() ?? "",
            Artist = artistName,
            ArtistId = $"ext-squidwtf-artist-{artistIdNum}",
<<<<<<< HEAD
            Artists = allArtists,
||||||| bc4e5d9
=======
            Artists = allArtists,
            ArtistIds = allArtistIds,
>>>>>>> dev
            Album = albumTitle,
            AlbumId = $"ext-squidwtf-album-{albumIdNum}",
            AlbumArtist = albumArtist,
            Duration = track.TryGetProperty("duration", out var duration) 
                ? duration.GetInt32() 
                : null,
            Track = trackNumber,
            DiscNumber = discNumber,
            Year = year,
            Bpm = bpm,
            Isrc = isrc,
            CoverArtUrl = coverArt,
            CoverArtUrlLarge = coverArtLarge,
            Label = copyright, // Store copyright in label field
            IsLocal = false,
            ExternalProvider = "squidwtf",
            ExternalId = externalId,
            ExplicitContentLyrics = explicitContentLyrics
        };
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
        
        int? year = null;
        if (album.TryGetProperty("releaseDate", out var releaseDate))
        {
            var dateStr = releaseDate.GetString();
            if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 4)
            {
                if (int.TryParse(dateStr.Substring(0, 4), out var y))
                    year = y;
            }
        }
        
        string? coverArt = null;
        if (album.TryGetProperty("cover", out var cover))
        {
            var coverGuid = cover.GetString()?.Replace("-", "/");
            coverArt = $"https://resources.tidal.com/images/{coverGuid}/320x320.jpg";
        }
        
        // Get artist name
        string artistName = "";
        string? artistId = null;
        if (album.TryGetProperty("artist", out var artist))
        {
            artistName = artist.GetProperty("name").GetString() ?? "";
            artistId = $"ext-squidwtf-artist-{artist.GetProperty("id").GetInt64()}";
        }
        else if (album.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
        {
            artistName = artists[0].GetProperty("name").GetString() ?? "";
            artistId = $"ext-squidwtf-artist-{artists[0].GetProperty("id").GetInt64()}";
        }
        
        return new Album
        {
            Id = $"ext-squidwtf-album-{externalId}",
            Title = album.GetProperty("title").GetString() ?? "",
            Artist = artistName,
            ArtistId = artistId,
            Year = year,
            SongCount = album.TryGetProperty("numberOfTracks", out var trackCount) 
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
        
        string? imageUrl = null;
        if (artist.TryGetProperty("picture", out var picture))
        {
            var pictureGuid = picture.GetString()?.Replace("-", "/");
            imageUrl = $"https://resources.tidal.com/images/{pictureGuid}/320x320.jpg";
        }
		
        return new Artist
        {
            Id = $"ext-squidwtf-artist-{externalId}",
            Name = artist.GetProperty("name").GetString() ?? "",
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
		JsonElement? playlist = null;
		JsonElement? tracks = null;

		if (playlistElement.TryGetProperty("playlist", out var playlistEl))
		{
			playlist = playlistEl;
		}
		
		if (playlistElement.TryGetProperty("items", out var tracksEl))
		{
			tracks = tracksEl;
		}
		
		if (!playlist.HasValue)
		{
			throw new InvalidOperationException("Playlist data is missing");
		}
		
		var externalId = playlist.Value.GetProperty("uuid").GetString()!;
		
        // Get curator/creator name
        string? curatorName = null;
        if (playlist.Value.TryGetProperty("creator", out var creator) &&
            creator.TryGetProperty("id", out var id))
        {
            curatorName = id.GetString();
        }
		
		// Get creation date
        DateTime? createdDate = null;
        if (playlist.Value.TryGetProperty("created", out var creationDateEl))
        {
            var dateStr = creationDateEl.GetString();
            if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var date))
            {
                createdDate = date;
            }
        }
		
		// Get playlist image URL
		string? imageUrl = null;
        if (playlist.Value.TryGetProperty("squareImage", out var picture))
        {
            var pictureGuid = picture.GetString()?.Replace("-", "/");
            imageUrl = $"https://resources.tidal.com/images/{pictureGuid}/1080x1080.jpg";
			// Maybe later add support for potentential fallbacks if this size isn't available
        }

		return new ExternalPlaylist
        {
            Id = Common.PlaylistIdHelper.CreatePlaylistId("squidwtf", externalId),
            Name = playlist.Value.GetProperty("title").GetString() ?? "",
            Description = playlist.Value.TryGetProperty("description", out var desc) 
                ? desc.GetString() 
                : null,
            CuratorName = curatorName,
            Provider = "squidwtf",
            ExternalId = externalId,
            TrackCount = playlist.Value.TryGetProperty("numberOfTracks", out var nbTracks) 
                ? nbTracks.GetInt32() 
                : 0,
            Duration = playlist.Value.TryGetProperty("duration", out var duration) 
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

}