using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using allstarr.Models.Scrobbling;
using allstarr.Models.Settings;

namespace allstarr.Services.Scrobbling;

/// <summary>
/// Last.fm scrobbling service implementation.
/// Follows the Scrobbling 2.0 API specification.
/// </summary>
public class LastFmScrobblingService : IScrobblingService
{
    private const string ApiRoot = "https://ws.audioscrobbler.com/2.0/";
    private const int MaxBatchSize = 50;

    private readonly LastFmSettings _settings;
    private readonly ScrobblingSettings _globalSettings;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LastFmScrobblingService> _logger;

    public string ServiceName => "Last.fm";
    public bool IsEnabled => _settings.Enabled &&
                             !string.IsNullOrEmpty(_settings.ApiKey) &&
                             !string.IsNullOrEmpty(_settings.SharedSecret) &&
                             !LastFmSettings.IsLegacyJellyfinPluginApiKey(_settings.ApiKey) &&
                             !string.IsNullOrEmpty(_settings.SessionKey);

    public LastFmScrobblingService(
        IOptions<ScrobblingSettings> settings,
        IHttpClientFactory httpClientFactory,
        ILogger<LastFmScrobblingService> logger)
    {
        _globalSettings = settings.Value;
        _settings = settings.Value.LastFm;
        _httpClient = httpClientFactory.CreateClient("LastFm");
        _logger = logger;
    }

    public async Task<ScrobbleResult> UpdateNowPlayingAsync(ScrobbleTrack track, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return ScrobbleResult.CreateError("Last.fm scrobbling not enabled or configured");
        }

        // Only scrobble external tracks (unless local tracks are enabled)
        if (!track.IsExternal && !_globalSettings.LocalTracksEnabled)
        {
            return ScrobbleResult.CreateIgnored("Local library tracks are not scrobbled (LocalTracksEnabled=false)", 0);
        }

        _logger.LogDebug("→ Updating Now Playing on Last.fm: {Artist} - {Track}", track.Artist, track.Title);

        try
        {
            var parameters = BuildBaseParameters("track.updateNowPlaying");
            AddTrackParameters(parameters, track, includeTimestamp: false);

            var response = await SendRequestAsync(parameters, cancellationToken);
            var result = ParseResponse(response, isScrobble: false);

            if (result.Success && !result.Ignored)
            {
                _logger.LogDebug("✓ Now Playing updated on Last.fm: {Artist} - {Track}",
                    track.Artist, track.Title);
            }
            else if (result.Ignored)
            {
                _logger.LogWarning("⚠️ Now Playing ignored by Last.fm: {Reason}", result.IgnoredReason);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to update Now Playing on Last.fm");
            return ScrobbleResult.CreateError($"Exception: {ex.Message}");
        }
    }

    public async Task<ScrobbleResult> ScrobbleAsync(ScrobbleTrack track, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return ScrobbleResult.CreateError("Last.fm scrobbling not enabled or configured");
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

        _logger.LogDebug("→ Scrobbling to Last.fm: {Artist} - {Track}", track.Artist, track.Title);

        try
        {
            var parameters = BuildBaseParameters("track.scrobble");
            AddTrackParameters(parameters, track, includeTimestamp: true);

            var response = await SendRequestAsync(parameters, cancellationToken);
            var result = ParseResponse(response, isScrobble: true);

            if (result.Success && !result.Ignored)
            {
                _logger.LogDebug("✓ Scrobbled to Last.fm: {Artist} - {Track}",
                    track.Artist, track.Title);

                if (result.ArtistCorrected || result.TrackCorrected || result.AlbumCorrected)
                {
                    _logger.LogDebug("📝 Last.fm corrections: Artist={Artist}, Track={Track}, Album={Album}",
                        result.CorrectedArtist ?? track.Artist,
                        result.CorrectedTrack ?? track.Title,
                        result.CorrectedAlbum ?? track.Album);
                }
            }
            else if (result.Ignored)
            {
                _logger.LogWarning("⚠️ Scrobble ignored by Last.fm: {Reason} (code: {Code})",
                    result.IgnoredReason, result.IgnoredCode);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to scrobble to Last.fm");
            return ScrobbleResult.CreateError($"Exception: {ex.Message}");
        }
    }

    public async Task<List<ScrobbleResult>> ScrobbleBatchAsync(List<ScrobbleTrack> tracks, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return tracks.Select(_ => ScrobbleResult.CreateError("Last.fm scrobbling not enabled or configured")).ToList();
        }

        if (tracks.Count == 0)
        {
            return new List<ScrobbleResult>();
        }

        // Filter out local tracks (unless local tracks are enabled)
        var allowedTracks = tracks.Where(t => t.IsExternal || _globalSettings.LocalTracksEnabled).ToList();
        var filteredTracks = tracks.Where(t => !t.IsExternal && !_globalSettings.LocalTracksEnabled).ToList();

        var results = new List<ScrobbleResult>();

        // Add ignored results for filtered local tracks
        results.AddRange(filteredTracks.Select(_ =>
            ScrobbleResult.CreateIgnored("Local library tracks are not scrobbled (LocalTracksEnabled=false)", 0)));

        if (allowedTracks.Count == 0)
        {
            return results;
        }

        if (allowedTracks.Count > MaxBatchSize)
        {
            _logger.LogWarning("Batch size {Count} exceeds maximum {Max}, splitting into multiple requests",
                allowedTracks.Count, MaxBatchSize);

            for (int i = 0; i < allowedTracks.Count; i += MaxBatchSize)
            {
                var batch = allowedTracks.Skip(i).Take(MaxBatchSize).ToList();
                var batchResults = await ScrobbleBatchAsync(batch, cancellationToken);
                results.AddRange(batchResults);
            }
            return results;
        }

        _logger.LogDebug("→ Scrobbling batch of {Count} tracks to Last.fm", allowedTracks.Count);

        try
        {
            var parameters = BuildBaseParameters("track.scrobble");

            // Add parameters for each track with index suffix
            for (int i = 0; i < allowedTracks.Count; i++)
            {
                AddTrackParameters(parameters, allowedTracks[i], includeTimestamp: true, index: i);
            }

            var response = await SendRequestAsync(parameters, cancellationToken);
            var batchResults = ParseBatchResponse(response, allowedTracks.Count);

            var accepted = batchResults.Count(r => r.Success && !r.Ignored);
            var ignored = batchResults.Count(r => r.Ignored);
            var failed = batchResults.Count(r => !r.Success);

            _logger.LogDebug("✓ Batch scrobble complete: {Accepted} accepted, {Ignored} ignored, {Failed} failed",
                accepted, ignored, failed);

            results.AddRange(batchResults);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to scrobble batch to Last.fm");
            results.AddRange(allowedTracks.Select(_ => ScrobbleResult.CreateError($"Exception: {ex.Message}")));
            return results;
        }
    }

    #region Helper Methods

    /// <summary>
    /// Builds base parameters for all API requests (api_key, method, sk).
    /// </summary>
    private Dictionary<string, string> BuildBaseParameters(string method)
    {
        return new Dictionary<string, string>
        {
            ["api_key"] = _settings.ApiKey,
            ["method"] = method,
            ["sk"] = _settings.SessionKey
        };
    }

    /// <summary>
    /// Adds track-specific parameters to the request.
    /// </summary>
    private void AddTrackParameters(Dictionary<string, string> parameters, ScrobbleTrack track, bool includeTimestamp, int? index = null)
    {
        var suffix = index.HasValue ? $"[{index}]" : "";

        parameters[$"artist{suffix}"] = track.Artist;
        parameters[$"track{suffix}"] = track.Title;

        if (!string.IsNullOrEmpty(track.Album))
            parameters[$"album{suffix}"] = track.Album;

        if (!string.IsNullOrEmpty(track.AlbumArtist))
            parameters[$"albumArtist{suffix}"] = track.AlbumArtist;

        if (track.DurationSeconds.HasValue)
            parameters[$"duration{suffix}"] = track.DurationSeconds.Value.ToString();

        if (!string.IsNullOrEmpty(track.MusicBrainzId))
            parameters[$"mbid{suffix}"] = track.MusicBrainzId;

        if (includeTimestamp && track.Timestamp.HasValue)
            parameters[$"timestamp{suffix}"] = track.Timestamp.Value.ToString();

        // Only include chosenByUser if it's false (default is true)
        if (!track.ChosenByUser)
            parameters[$"chosenByUser{suffix}"] = "0";
    }

    /// <summary>
    /// Generates MD5 signature for API request.
    /// Format: api_key{value}method{value}...{shared_secret}
    /// </summary>
    private string GenerateSignature(Dictionary<string, string> parameters)
    {
        // Sort parameters alphabetically by key
        var sorted = parameters.OrderBy(kvp => kvp.Key);

        // Build signature string: key1value1key2value2...secret
        var signatureString = new StringBuilder();
        foreach (var kvp in sorted)
        {
            signatureString.Append(kvp.Key);
            signatureString.Append(kvp.Value);
        }
        signatureString.Append(_settings.SharedSecret);

        // Generate MD5 hash
        var bytes = Encoding.UTF8.GetBytes(signatureString.ToString());
        var hash = MD5.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Sends HTTP POST request to Last.fm API.
    /// </summary>
    private async Task<string> SendRequestAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        // Add signature
        parameters["api_sig"] = GenerateSignature(parameters);

        // Create form content
        var content = new FormUrlEncodedContent(parameters);

        // Send request
        using var response = await _httpClient.PostAsync(ApiRoot, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        // Log request/response for debugging
        _logger.LogTrace("Last.fm request: {Method}, Response: {StatusCode}",
            parameters["method"], response.StatusCode);

        // Always inspect response body, even if HTTP status is not 200
        return responseBody;
    }

    /// <summary>
    /// Parses Last.fm XML response for single scrobble/now playing.
    /// </summary>
    private ScrobbleResult ParseResponse(string xml, bool isScrobble)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root;

            if (root == null)
            {
                return ScrobbleResult.CreateError("Invalid XML response");
            }

            var status = root.Attribute("status")?.Value;

            // Check for error
            if (status == "failed")
            {
                var errorElement = root.Element("error");
                var errorCode = int.Parse(errorElement?.Attribute("code")?.Value ?? "0");
                var errorMessage = errorElement?.Value ?? "Unknown error";

                // Determine if should retry based on error code
                var shouldRetry = errorCode == 11 || errorCode == 16; // Service offline or temporarily unavailable

                // Error code 9 means session key is invalid - log prominently
                if (errorCode == 9)
                {
                    _logger.LogError("❌ Last.fm session key is invalid - please re-authenticate");
                }

                if (IsSuspendedApiKeyError(errorMessage))
                {
                    LogSuspendedApiKeyGuidance();
                }

                return ScrobbleResult.CreateError(errorMessage, errorCode, shouldRetry);
            }

            // Success - check for ignored message
            if (isScrobble)
            {
                var scrobbleElement = root.Descendants("scrobble").FirstOrDefault();
                if (scrobbleElement != null)
                {
                    return ParseScrobbleElement(scrobbleElement);
                }
            }

            return ScrobbleResult.CreateSuccess();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Last.fm response: {Xml}", xml);
            return ScrobbleResult.CreateError($"Parse error: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses Last.fm XML response for batch scrobble.
    /// </summary>
    private List<ScrobbleResult> ParseBatchResponse(string xml, int expectedCount)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root;

            if (root == null)
            {
                return Enumerable.Repeat(ScrobbleResult.CreateError("Invalid XML response"), expectedCount).ToList();
            }

            var status = root.Attribute("status")?.Value;

            // Check for error
            if (status == "failed")
            {
                var errorElement = root.Element("error");
                var errorCode = int.Parse(errorElement?.Attribute("code")?.Value ?? "0");
                var errorMessage = errorElement?.Value ?? "Unknown error";
                var shouldRetry = errorCode == 11 || errorCode == 16;

                if (IsSuspendedApiKeyError(errorMessage))
                {
                    LogSuspendedApiKeyGuidance();
                }

                return Enumerable.Repeat(ScrobbleResult.CreateError(errorMessage, errorCode, shouldRetry), expectedCount).ToList();
            }

            // Parse individual scrobble results
            var scrobbleElements = root.Descendants("scrobble").ToList();
            return scrobbleElements.Select(ParseScrobbleElement).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Last.fm batch response: {Xml}", xml);
            return Enumerable.Repeat(ScrobbleResult.CreateError($"Parse error: {ex.Message}"), expectedCount).ToList();
        }
    }

    /// <summary>
    /// Parses a single scrobble element from XML response.
    /// </summary>
    private ScrobbleResult ParseScrobbleElement(XElement scrobbleElement)
    {
        var result = new ScrobbleResult { Success = true };

        // Check for ignored message
        var ignoredElement = scrobbleElement.Element("ignoredmessage");
        if (ignoredElement != null)
        {
            var ignoredCode = int.Parse(ignoredElement.Attribute("code")?.Value ?? "0");
            if (ignoredCode > 0)
            {
                return ScrobbleResult.CreateIgnored(ignoredElement.Value.Trim(), ignoredCode);
            }
        }

        // Check for corrections
        var artistElement = scrobbleElement.Element("artist");
        if (artistElement != null && artistElement.Attribute("corrected")?.Value == "1")
        {
            result = result with
            {
                ArtistCorrected = true,
                CorrectedArtist = artistElement.Value
            };
        }

        var trackElement = scrobbleElement.Element("track");
        if (trackElement != null && trackElement.Attribute("corrected")?.Value == "1")
        {
            result = result with
            {
                TrackCorrected = true,
                CorrectedTrack = trackElement.Value
            };
        }

        var albumElement = scrobbleElement.Element("album");
        if (albumElement != null && albumElement.Attribute("corrected")?.Value == "1")
        {
            result = result with
            {
                AlbumCorrected = true,
                CorrectedAlbum = albumElement.Value
            };
        }

        return result;
    }

    private static bool IsSuspendedApiKeyError(string errorMessage) =>
        errorMessage.Contains("suspended", StringComparison.OrdinalIgnoreCase) ||
        errorMessage.Contains("not allowed to make requests", StringComparison.OrdinalIgnoreCase);

    private void LogSuspendedApiKeyGuidance()
    {
        _logger.LogError(
            "Last.fm rejected requests because the API key is suspended. Register your own app at " +
            "https://www.last.fm/api/account/create, set SCROBBLING_LASTFM_API_KEY and " +
            "SCROBBLING_LASTFM_SHARED_SECRET, restart Allstarr, then re-authenticate in Admin → Scrobbling.");
    }

    #endregion
}
