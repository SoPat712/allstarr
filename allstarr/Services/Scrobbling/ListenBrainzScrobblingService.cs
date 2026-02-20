using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using allstarr.Models.Scrobbling;
using allstarr.Models.Settings;

namespace allstarr.Services.Scrobbling;

/// <summary>
/// ListenBrainz scrobbling service implementation.
/// Follows the ListenBrainz API specification.
/// Only scrobbles external tracks (not local library tracks).
/// </summary>
public class ListenBrainzScrobblingService : IScrobblingService
{
    private const string ApiRoot = "https://api.listenbrainz.org/1";
    private const int MaxBatchSize = 1000; // ListenBrainz supports up to 1000 listens per request
    
    private readonly ListenBrainzSettings _settings;
    private readonly ScrobblingSettings _globalSettings;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ListenBrainzScrobblingService> _logger;
    
    public string ServiceName => "ListenBrainz";
    public bool IsEnabled => _settings.Enabled && !string.IsNullOrEmpty(_settings.UserToken);
    
    public ListenBrainzScrobblingService(
        IOptions<ScrobblingSettings> settings,
        IHttpClientFactory httpClientFactory,
        ILogger<ListenBrainzScrobblingService> logger)
    {
        _globalSettings = settings.Value;
        _settings = settings.Value.ListenBrainz;
        _httpClient = httpClientFactory.CreateClient("ListenBrainz");
        _logger = logger;
        
        // Debug logging
        _logger.LogInformation("ListenBrainz Service Configuration:");
        _logger.LogInformation("  Enabled: {Enabled}", _settings.Enabled);
        _logger.LogInformation("  UserToken: {Token}", string.IsNullOrEmpty(_settings.UserToken) ? "(empty)" : "***" + _settings.UserToken[^Math.Min(8, _settings.UserToken.Length)..]);
        _logger.LogInformation("  IsEnabled: {IsEnabled}", IsEnabled);
        
        if (IsEnabled)
        {
            _logger.LogInformation("🎵 ListenBrainz scrobbling enabled");
        }
        else
        {
            _logger.LogWarning("⚠️ ListenBrainz scrobbling NOT enabled (Enabled={Enabled}, HasToken={HasToken})", 
                _settings.Enabled, !string.IsNullOrEmpty(_settings.UserToken));
        }
    }
    
    public async Task<ScrobbleResult> UpdateNowPlayingAsync(ScrobbleTrack track, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return ScrobbleResult.CreateError("ListenBrainz scrobbling not enabled or configured");
        }
        
        // Only scrobble external tracks (unless local tracks are enabled)
        if (!track.IsExternal && !_globalSettings.LocalTracksEnabled)
        {
            return ScrobbleResult.CreateIgnored("Local library tracks are not scrobbled (LocalTracksEnabled=false)", 0);
        }
        
        _logger.LogDebug("→ Updating Now Playing on ListenBrainz: {Artist} - {Track}", track.Artist, track.Title);
        
        try
        {
            var payload = BuildListenPayload("playing_now", new[] { track });
            var response = await SendRequestAsync("/submit-listens", payload, cancellationToken);
            
            if (response.Success)
            {
                _logger.LogDebug("✓ Now Playing updated on ListenBrainz: {Artist} - {Track}", 
                    track.Artist, track.Title);
            }
            
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to update Now Playing on ListenBrainz");
            return ScrobbleResult.CreateError($"Exception: {ex.Message}");
        }
    }
    
    public async Task<ScrobbleResult> ScrobbleAsync(ScrobbleTrack track, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return ScrobbleResult.CreateError("ListenBrainz scrobbling not enabled or configured");
        }
        
        // Only scrobble external tracks (unless local tracks are enabled)
        if (!track.IsExternal && !_globalSettings.LocalTracksEnabled)
        {
            return ScrobbleResult.CreateIgnored("Local library tracks are not scrobbled (LocalTracksEnabled=false)", 0);
        }
        
        if (track.Timestamp == null)
        {
            return ScrobbleResult.CreateError("Timestamp is required for scrobbling");
        }
        
        _logger.LogDebug("→ Scrobbling to ListenBrainz: {Artist} - {Track}", track.Artist, track.Title);
        
        try
        {
            var payload = BuildListenPayload("single", new[] { track });
            var response = await SendRequestAsync("/submit-listens", payload, cancellationToken);
            
            if (response.Success)
            {
                _logger.LogDebug("✓ Scrobbled to ListenBrainz: {Artist} - {Track}", 
                    track.Artist, track.Title);
            }
            
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to scrobble to ListenBrainz");
            return ScrobbleResult.CreateError($"Exception: {ex.Message}");
        }
    }
    
    public async Task<List<ScrobbleResult>> ScrobbleBatchAsync(List<ScrobbleTrack> tracks, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return tracks.Select(_ => ScrobbleResult.CreateError("ListenBrainz scrobbling not enabled or configured")).ToList();
        }
        
        if (tracks.Count == 0)
        {
            return new List<ScrobbleResult>();
        }
        
        // Filter out local tracks (unless local tracks are enabled)
        var externalTracks = tracks.Where(t => t.IsExternal || _globalSettings.LocalTracksEnabled).ToList();
        var localTracks = tracks.Where(t => !t.IsExternal && !_globalSettings.LocalTracksEnabled).ToList();
        
        var results = new List<ScrobbleResult>();
        
        // Add ignored results for local tracks
        results.AddRange(localTracks.Select(_ => 
            ScrobbleResult.CreateIgnored("Local library tracks are not scrobbled", 0)));
        
        if (externalTracks.Count == 0)
        {
            return results;
        }
        
        if (externalTracks.Count > MaxBatchSize)
        {
            _logger.LogWarning("Batch size {Count} exceeds maximum {Max}, splitting into multiple requests", 
                externalTracks.Count, MaxBatchSize);
            
            for (int i = 0; i < externalTracks.Count; i += MaxBatchSize)
            {
                var batch = externalTracks.Skip(i).Take(MaxBatchSize).ToList();
                var batchResults = await ScrobbleBatchAsync(batch, cancellationToken);
                results.AddRange(batchResults);
            }
            return results;
        }
        
        _logger.LogDebug("→ Scrobbling batch of {Count} tracks to ListenBrainz", externalTracks.Count);
        
        try
        {
            var payload = BuildListenPayload("import", externalTracks);
            var response = await SendRequestAsync("/submit-listens", payload, cancellationToken);
            
            if (response.Success)
            {
                _logger.LogDebug("✓ Batch scrobble complete: {Count} tracks submitted to ListenBrainz", 
                    externalTracks.Count);
                
                // ListenBrainz doesn't provide per-track results, so return success for all
                results.AddRange(externalTracks.Select(_ => ScrobbleResult.CreateSuccess()));
            }
            else
            {
                // If batch fails, all tracks fail
                results.AddRange(externalTracks.Select(_ => response));
            }
            
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to scrobble batch to ListenBrainz");
            results.AddRange(externalTracks.Select(_ => ScrobbleResult.CreateError($"Exception: {ex.Message}")));
            return results;
        }
    }
    
    #region Helper Methods
    
    /// <summary>
    /// Builds the JSON payload for ListenBrainz API.
    /// </summary>
    private string BuildListenPayload(string listenType, IEnumerable<ScrobbleTrack> tracks)
    {
        var listens = tracks.Select(track =>
        {
            var additionalInfo = new Dictionary<string, object>();
            
            // Only add MusicBrainz recording ID if available (must be valid UUID format)
            if (!string.IsNullOrEmpty(track.MusicBrainzId))
            {
                additionalInfo["recording_mbid"] = track.MusicBrainzId;
            }
            
            if (track.DurationSeconds.HasValue)
            {
                additionalInfo["duration_ms"] = track.DurationSeconds.Value * 1000;
            }
            
            // For single and import, include timestamp
            if (listenType != "playing_now" && track.Timestamp.HasValue)
            {
                return (object)new
                {
                    listened_at = track.Timestamp.Value,
                    track_metadata = new
                    {
                        artist_name = track.Artist,
                        track_name = track.Title,
                        release_name = track.Album,
                        additional_info = additionalInfo
                    }
                };
            }
            
            return (object)new
            {
                track_metadata = new
                {
                    artist_name = track.Artist,
                    track_name = track.Title,
                    release_name = track.Album,
                    additional_info = additionalInfo
                }
            };
        }).ToList();
        
        var payload = new
        {
            listen_type = listenType,
            payload = listens
        };
        
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }
    
    /// <summary>
    /// Sends HTTP POST request to ListenBrainz API.
    /// </summary>
    private async Task<ScrobbleResult> SendRequestAsync(string endpoint, string jsonPayload, CancellationToken cancellationToken)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiRoot}{endpoint}")
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };
            
            request.Headers.Add("Authorization", $"Token {_settings.UserToken}");
            
            var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            
            _logger.LogTrace("ListenBrainz request: {Endpoint}, Response: {StatusCode}", 
                endpoint, response.StatusCode);
            
            if (response.IsSuccessStatusCode)
            {
                return ScrobbleResult.CreateSuccess();
            }
            
            // Parse error response
            try
            {
                var errorDoc = JsonDocument.Parse(responseBody);
                var errorMessage = errorDoc.RootElement.GetProperty("error").GetString() ?? "Unknown error";
                var errorCode = (int)response.StatusCode;
                
                // Determine if should retry based on status code
                var shouldRetry = errorCode == 429 || errorCode >= 500;
                
                if (errorCode == 401)
                {
                    _logger.LogError("❌ ListenBrainz user token is invalid - please check your token");
                }
                
                return ScrobbleResult.CreateError(errorMessage, errorCode, shouldRetry);
            }
            catch
            {
                return ScrobbleResult.CreateError($"HTTP {response.StatusCode}: {responseBody}", 
                    (int)response.StatusCode, (int)response.StatusCode >= 500);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request failed");
            return ScrobbleResult.CreateError($"HTTP error: {ex.Message}", shouldRetry: true);
        }
    }
    
    #endregion
}
