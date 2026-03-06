using System.Text.Json;
using allstarr.Models.Scrobbling;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;

namespace allstarr.Services.Scrobbling;

/// <summary>
/// Helper methods for extracting scrobble track information from Jellyfin items.
/// </summary>
public class ScrobblingHelper
{
    private readonly JellyfinProxyService _proxyService;
    private readonly ILocalLibraryService _localLibraryService;
    private readonly ILogger<ScrobblingHelper> _logger;
    
    public ScrobblingHelper(
        JellyfinProxyService proxyService,
        ILocalLibraryService localLibraryService,
        ILogger<ScrobblingHelper> logger)
    {
        _proxyService = proxyService;
        _localLibraryService = localLibraryService;
        _logger = logger;
    }
    
    /// <summary>
    /// Extracts scrobble track information from a Jellyfin item.
    /// Fetches item details from Jellyfin if needed.
    /// </summary>
    public async Task<ScrobbleTrack?> GetScrobbleTrackFromItemIdAsync(
        string itemId, 
        Microsoft.AspNetCore.Http.IHeaderDictionary headers,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Fetch item details from Jellyfin
            var (itemResult, statusCode) = await _proxyService.GetJsonAsync($"Items/{itemId}", null, headers);
            
            if (itemResult == null || statusCode != 200)
            {
                _logger.LogWarning("Failed to fetch item details for scrobbling: {ItemId} (status: {StatusCode})", 
                    itemId, statusCode);
                return null;
            }
            
            return ExtractScrobbleTrackFromJson(itemResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching item for scrobbling: {ItemId}", itemId);
            return null;
        }
    }
    
    /// <summary>
    /// Extracts scrobble track information from a Jellyfin JSON item.
    /// </summary>
    public ScrobbleTrack? ExtractScrobbleTrackFromJson(JsonDocument itemJson)
    {
        try
        {
            var item = itemJson.RootElement;
            
            // Extract required fields
            var title = item.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
            var artist = ExtractArtist(item);
            
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(artist))
            {
                _logger.LogDebug("Cannot create scrobble track - missing title or artist");
                return null;
            }
            
            // Extract optional fields
            var album = item.TryGetProperty("Album", out var albumProp) ? albumProp.GetString() : null;
            var albumArtist = ExtractAlbumArtist(item);
            var durationSeconds = ExtractDurationSeconds(item);
            var musicBrainzId = ExtractMusicBrainzId(item);
            
            // Determine if track is external by checking the Path
            var isExternal = false;
            if (item.TryGetProperty("Path", out var pathProp))
            {
                var path = pathProp.GetString();
                if (!string.IsNullOrEmpty(path))
                {
                    // External tracks have paths like: ext-deezer-song-123456
                    // or are in the "kept" directory for favorited external tracks
                    isExternal = path.StartsWith("ext-") || path.Contains("/kept/") || path.Contains("\\kept\\");
                }
            }
            
            return new ScrobbleTrack
            {
                Title = title,
                Artist = artist,
                Album = album,
                AlbumArtist = albumArtist,
                DurationSeconds = durationSeconds,
                MusicBrainzId = musicBrainzId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                IsExternal = isExternal,
                StartPositionSeconds = 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting scrobble track from JSON");
            return null;
        }
    }
    
    /// <summary>
    /// Creates a scrobble track from external track metadata.
    /// </summary>
    public ScrobbleTrack? CreateScrobbleTrackFromExternal(
        string title,
        string artist,
        string? album = null,
        string? albumArtist = null,
        int? durationSeconds = null,
        int? startPositionSeconds = null)
    {
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(artist))
        {
            return null;
        }
        
        return new ScrobbleTrack
        {
            Title = title,
            Artist = artist,
            Album = album,
            AlbumArtist = albumArtist,
            DurationSeconds = durationSeconds,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            IsExternal = true, // Explicitly mark as external
            StartPositionSeconds = startPositionSeconds
        };
    }
    
    #region Private Helper Methods
    
    /// <summary>
    /// Checks if a track is long enough to be scrobbled according to Last.fm rules.
    /// Tracks must be at least 30 seconds long.
    /// </summary>
    public static bool IsTrackLongEnoughToScrobble(int durationSeconds)
    {
        return durationSeconds >= 30;
    }
    
    /// <summary>
    /// Checks if enough of the track has been listened to for scrobbling.
    /// Last.fm rules: Must listen to at least 50% of track OR 4 minutes (whichever comes first).
    /// </summary>
    public static bool HasListenedEnoughToScrobble(int trackDurationSeconds, int playedSeconds)
    {
        var halfDuration = trackDurationSeconds / 2.0;
        var fourMinutes = 240;
        var threshold = Math.Min(halfDuration, fourMinutes);
        return playedSeconds >= threshold;
    }
    
    /// <summary>
    /// Checks if a track has the minimum required metadata for scrobbling.
    /// Requires at minimum: track title and artist name.
    /// </summary>
    public static bool HasRequiredMetadata(string? title, string? artist)
    {
        return !string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist);
    }
    
    /// <summary>
    /// Formats a track for display in logs.
    /// </summary>
    public static string FormatTrackForDisplay(string title, string artist)
    {
        return $"{title} - {artist}";
    }
    
    /// <summary>
    /// Extracts artist name from Jellyfin item.
    /// Tries Artists array first, then AlbumArtist, then ArtistItems.
    /// </summary>
    private string? ExtractArtist(JsonElement item)
    {
        // Try Artists array (most common)
        if (item.TryGetProperty("Artists", out var artistsProp) && artistsProp.ValueKind == JsonValueKind.Array)
        {
            var firstArtist = artistsProp.EnumerateArray().FirstOrDefault();
            if (firstArtist.ValueKind == JsonValueKind.String)
            {
                return firstArtist.GetString();
            }
        }
        
        // Try AlbumArtist
        if (item.TryGetProperty("AlbumArtist", out var albumArtistProp))
        {
            return albumArtistProp.GetString();
        }
        
        // Try ArtistItems array
        if (item.TryGetProperty("ArtistItems", out var artistItemsProp) && artistItemsProp.ValueKind == JsonValueKind.Array)
        {
            var firstArtistItem = artistItemsProp.EnumerateArray().FirstOrDefault();
            if (firstArtistItem.TryGetProperty("Name", out var artistNameProp))
            {
                return artistNameProp.GetString();
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Extracts album artist from Jellyfin item.
    /// </summary>
    private string? ExtractAlbumArtist(JsonElement item)
    {
        if (item.TryGetProperty("AlbumArtist", out var albumArtistProp))
        {
            return albumArtistProp.GetString();
        }
        
        // Fall back to first artist
        return ExtractArtist(item);
    }
    
    /// <summary>
    /// Extracts track duration in seconds from Jellyfin item.
    /// </summary>
    private int? ExtractDurationSeconds(JsonElement item)
    {
        if (item.TryGetProperty("RunTimeTicks", out var ticksProp))
        {
            var ticks = ticksProp.GetInt64();
            return (int)(ticks / TimeSpan.TicksPerSecond);
        }
        
        return null;
    }
    
    /// <summary>
    /// Extracts MusicBrainz Track ID from Jellyfin item.
    /// </summary>
    private string? ExtractMusicBrainzId(JsonElement item)
    {
        if (item.TryGetProperty("ProviderIds", out var providerIdsProp))
        {
            if (providerIdsProp.TryGetProperty("MusicBrainzTrack", out var mbidProp))
            {
                return mbidProp.GetString();
            }
        }
        
        return null;
    }
    
    #endregion
}
