using System.Text.Json;
using System.Text;
using System.Net.Http;
using allstarr.Models.Domain;
using allstarr.Models.Spotify;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    #region Helpers

    private static readonly HashSet<string> PassthroughResponseHeadersToSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "Content-Type",
        "Content-Length"
    };

    /// <summary>
    /// Helper to handle proxy responses with proper status code handling.
    /// </summary>
    private IActionResult HandleProxyResponse(JsonDocument? result, int statusCode, object? fallbackValue = null)
    {
        return ProxyResponseResultFactory.Create(result, statusCode, fallbackValue);
    }

    private async Task<IActionResult> ProxyJsonPassthroughAsync(string endpoint)
    {
        try
        {
            // Match the previous proxy semantics for client compatibility.
            // Some Jellyfin clients/proxies cancel the ASP.NET request token aggressively
            // even though the upstream request would still complete successfully.
            var upstreamResponse = await _proxyService.GetPassthroughResponseAsync(
                endpoint,
                Request.Headers);

            HttpContext.Response.RegisterForDispose(upstreamResponse);
            HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            Response.StatusCode = (int)upstreamResponse.StatusCode;
            Response.Headers["X-Accel-Buffering"] = "no";

            CopyPassthroughResponseHeaders(upstreamResponse);

            if (upstreamResponse.Content.Headers.ContentLength.HasValue)
            {
                Response.ContentLength = upstreamResponse.Content.Headers.ContentLength.Value;
            }

            var contentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/json";
            var stream = await upstreamResponse.Content.ReadAsStreamAsync();

            return new FileStreamResult(stream, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to transparently proxy Jellyfin request for {Endpoint}", endpoint);
            return StatusCode(502, new { error = "Failed to connect to Jellyfin server" });
        }
    }

    private void CopyPassthroughResponseHeaders(HttpResponseMessage upstreamResponse)
    {
        foreach (var header in upstreamResponse.Headers)
        {
            if (!PassthroughResponseHeadersToSkip.Contains(header.Key))
            {
                Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        foreach (var header in upstreamResponse.Content.Headers)
        {
            if (!PassthroughResponseHeadersToSkip.Contains(header.Key))
            {
                Response.Headers[header.Key] = header.Value.ToArray();
            }
        }
    }

    /// <summary>
    /// Updates ChildCount for Spotify playlists in the response to show total tracks (local + matched).
    /// </summary>
    private async Task<JsonDocument> UpdateSpotifyPlaylistCounts(JsonDocument response)
    {
        try
        {
            if (!response.RootElement.TryGetProperty("Items", out var items))
            {
                return response;
            }

            var itemsArray = items.EnumerateArray().ToList();
            var modified = false;
            var updatedItems = new List<Dictionary<string, object>>();
            var spotifyPlaylistCreatedDates = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);

            _logger.LogDebug("Checking {Count} items for Spotify playlists", itemsArray.Count);

            foreach (var item in itemsArray)
            {
                var itemDict = JsonSerializer.Deserialize<Dictionary<string, object>>(item.GetRawText());
                if (itemDict == null)
                {
                    continue;
                }

                // Check if this is a Spotify playlist
                if (item.TryGetProperty("Id", out var idProp))
                {
                    var playlistId = idProp.GetString();
                    _logger.LogDebug("Checking item with ID: {Id}", playlistId);

                    if (!string.IsNullOrEmpty(playlistId) && _spotifySettings.IsSpotifyPlaylist(playlistId))
                    {
                        _logger.LogDebug("Found Spotify playlist: {Id}", playlistId);

                        // This is a Spotify playlist - get the actual track count
                        var playlistConfig = _spotifySettings.GetPlaylistByJellyfinId(playlistId);

                        if (playlistConfig != null)
                        {
                            _logger.LogDebug(
                                "Found playlist config for Jellyfin ID {JellyfinId}: {Name} (Spotify ID: {SpotifyId})",
                                playlistId, playlistConfig.Name, playlistConfig.Id);
                            var playlistName = playlistConfig.Name;

                            if (!spotifyPlaylistCreatedDates.TryGetValue(playlistName, out var playlistCreatedDate))
                            {
                                playlistCreatedDate = await ResolveSpotifyPlaylistCreatedDateAsync(playlistName);
                                spotifyPlaylistCreatedDates[playlistName] = playlistCreatedDate;
                            }

                            if (ApplySpotifyPlaylistCreatedDate(itemDict, playlistCreatedDate))
                            {
                                modified = true;
                            }

                            // Get matched external tracks (tracks that were successfully downloaded/matched)
                            var matchedTracksKey = CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlistName);
                            var matchedTracks = await _cache.GetAsync<List<MatchedTrack>>(matchedTracksKey);

                            _logger.LogDebug("Cache lookup for {Key}: {Count} matched tracks",
                                matchedTracksKey, matchedTracks?.Count ?? 0);

                            // Fallback to legacy cache format
                            if (matchedTracks == null || matchedTracks.Count == 0)
                            {
                                var legacyKey =
                                    CacheKeyBuilder.BuildSpotifyLegacyMatchedTracksKey(playlistName);
                                var legacySongs = await _cache.GetAsync<List<Song>>(legacyKey);
                                if (legacySongs != null && legacySongs.Count > 0)
                                {
                                    matchedTracks = legacySongs.Select((s, i) => new MatchedTrack
                                    {
                                        Position = i,
                                        MatchedSong = s
                                    }).ToList();
                                    _logger.LogDebug("Loaded {Count} tracks from legacy cache", matchedTracks.Count);
                                }
                            }

                            // Prefer the currently served playlist items cache when available.
                            // This most closely matches what the injected playlist endpoint will return.
                            var exactServedCount = 0;
                            var playlistItemsKey = CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(playlistName);
                            var cachedPlaylistItems = await _cache.GetAsync<List<Dictionary<string, object?>>>(playlistItemsKey);
                            var exactServedRunTimeTicks = 0L;
                            if (cachedPlaylistItems != null &&
                                cachedPlaylistItems.Count > 0 &&
                                !InjectedPlaylistItemHelper.ContainsSyntheticLocalItems(cachedPlaylistItems))
                            {
                                exactServedCount = cachedPlaylistItems.Count;
                                exactServedRunTimeTicks =
                                    SpotifyPlaylistCountHelper.SumCachedPlaylistRunTimeTicks(cachedPlaylistItems);
                                _logger.LogDebug(
                                    "Using shared playlist cache metrics for {Playlist}: count={Count}, runtimeTicks={RunTimeTicks}",
                                    playlistName, exactServedCount, exactServedRunTimeTicks);
                            }

                            if (exactServedCount > 0)
                            {
                                itemDict["ChildCount"] = exactServedCount;
                                itemDict["RunTimeTicks"] = exactServedRunTimeTicks;
                                modified = true;
                            }
                            else
                            {
                                // Recompute ChildCount for injected playlists instead of trusting
                                // Jellyfin/plugin values, which only reflect local tracks.
                                var localTracksCount = 0;
                                var localRunTimeTicks = 0L;
                                try
                                {
                                    var userId = _settings.UserId;
                                    var playlistItemsUrl = $"Playlists/{playlistId}/Items";
                                    var queryParams = new Dictionary<string, string>
                                    {
                                        ["Fields"] = "Id,RunTimeTicks",
                                        ["Limit"] = "10000"
                                    };

                                    if (!string.IsNullOrEmpty(userId))
                                    {
                                        queryParams["UserId"] = userId;
                                    }

                                    var (localTracksResponse, _) = await _proxyService.GetJsonAsyncInternal(
                                        playlistItemsUrl,
                                        queryParams);

                                    if (localTracksResponse != null &&
                                        localTracksResponse.RootElement.TryGetProperty("Items", out var localItems))
                                    {
                                        foreach (var localItem in localItems.EnumerateArray())
                                        {
                                            localTracksCount++;
                                            localRunTimeTicks += SpotifyPlaylistCountHelper.ExtractRunTimeTicks(
                                                localItem.TryGetProperty("RunTimeTicks", out var runTimeTicks)
                                                    ? runTimeTicks
                                                    : null);
                                        }

                                        _logger.LogDebug("Found {Count} local Jellyfin items in playlist {Name}",
                                            localTracksCount, playlistName);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to get local tracks count for {Name}", playlistName);
                                }

                                var totalAvailableCount = SpotifyPlaylistCountHelper.ComputeServedItemCount(
                                    exactServedCount > 0 ? exactServedCount : null,
                                    localTracksCount,
                                    matchedTracks);
                                var totalRunTimeTicks = SpotifyPlaylistCountHelper.ComputeServedRunTimeTicks(
                                    exactServedCount > 0 ? exactServedRunTimeTicks : null,
                                    localRunTimeTicks,
                                    matchedTracks);

                                itemDict["ChildCount"] = totalAvailableCount;
                                itemDict["RunTimeTicks"] = totalRunTimeTicks;
                                modified = true;
                                _logger.LogDebug(
                                    "✓ Updated Spotify playlist metrics for {Name}: count={Total} ({Local} local + {External} external), runtimeTicks={RunTimeTicks}",
                                    playlistName,
                                    totalAvailableCount,
                                    localTracksCount,
                                    SpotifyPlaylistCountHelper.CountExternalMatchedTracks(matchedTracks),
                                    totalRunTimeTicks);
                            }
                        }
                        else
                        {
                            _logger.LogWarning(
                                "No playlist config found for Jellyfin ID {JellyfinId} - skipping count update",
                                playlistId);
                        }
                    }
                }

                updatedItems.Add(itemDict);
            }

            if (!modified)
            {
                _logger.LogDebug("No Spotify playlists found to update");
                return response;
            }

            _logger.LogDebug("Modified {Count} Spotify playlists, rebuilding response",
                updatedItems.Count(i => i.ContainsKey("ChildCount")));

            // Rebuild the response with updated items
            var responseDict =
                JsonSerializer.Deserialize<Dictionary<string, object>>(response.RootElement.GetRawText());
            if (responseDict != null)
            {
                responseDict["Items"] = updatedItems;
                var updatedJson = JsonSerializer.Serialize(responseDict);

                // Parse new document and dispose the old one to prevent memory leak
                var newDocument = JsonDocument.Parse(updatedJson);
                response.Dispose();
                return newDocument;
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Spotify playlist counts");
            return response;
        }
    }

    private async Task<DateTime?> ResolveSpotifyPlaylistCreatedDateAsync(string playlistName)
    {
        try
        {
            var cacheKey = CacheKeyBuilder.BuildSpotifyPlaylistKey(playlistName);
            var cachedPlaylist = await _cache.GetAsync<SpotifyPlaylist>(cacheKey);
            var createdAt = GetCreatedDateFromSpotifyPlaylist(cachedPlaylist);
            if (createdAt.HasValue)
            {
                return createdAt.Value;
            }

            if (_spotifyPlaylistFetcher == null)
            {
                return null;
            }

            var tracks = await _spotifyPlaylistFetcher.GetPlaylistTracksAsync(playlistName);
            var earliestTrackAddedAt = tracks
                .Where(t => t.AddedAt.HasValue)
                .Select(t => t.AddedAt!.Value.ToUniversalTime())
                .OrderBy(t => t)
                .FirstOrDefault();
            return earliestTrackAddedAt == default ? null : earliestTrackAddedAt;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve created date for Spotify playlist {PlaylistName}", playlistName);
            return null;
        }
    }

    private static DateTime? GetCreatedDateFromSpotifyPlaylist(SpotifyPlaylist? playlist)
    {
        if (playlist == null)
        {
            return null;
        }

        if (playlist.CreatedAt.HasValue)
        {
            return playlist.CreatedAt.Value.ToUniversalTime();
        }

        var earliestTrackAddedAt = playlist.Tracks
            .Where(t => t.AddedAt.HasValue)
            .Select(t => t.AddedAt!.Value.ToUniversalTime())
            .OrderBy(t => t)
            .FirstOrDefault();
        return earliestTrackAddedAt == default ? null : earliestTrackAddedAt;
    }

    private static bool ApplySpotifyPlaylistCreatedDate(Dictionary<string, object> itemDict, DateTime? playlistCreatedDate)
    {
        if (!playlistCreatedDate.HasValue)
        {
            return false;
        }

        var createdUtc = playlistCreatedDate.Value.ToUniversalTime();
        var createdAtIso = createdUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

        itemDict["DateCreated"] = createdAtIso;
        itemDict["PremiereDate"] = createdAtIso;
        itemDict["ProductionYear"] = createdUtc.Year;
        return true;
    }

    /// <summary>
    /// Logs endpoint usage to a file for analysis.
    /// Creates a CSV file with timestamp, method, and path only.
    /// Query strings are intentionally excluded to avoid persisting sensitive data.
    /// </summary>
    private async Task LogEndpointUsageAsync(string path, string method)
    {
        try
        {
            var logDir = EndpointUsagePathResolver.GetDirectory(_configuration);
            Directory.CreateDirectory(logDir);

            var logFile = EndpointUsagePathResolver.GetLogFile(_configuration);
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

            // Sanitize path for CSV (remove commas, quotes, newlines)
            var sanitizedPath = path.Replace(",", ";").Replace("\"", "'").Replace("\n", " ").Replace("\r", " ");

            var logLine = $"{timestamp},{method},{sanitizedPath}\n";

            // Append to file (thread-safe)
            await System.IO.File.AppendAllTextAsync(logFile, logLine);
        }
        catch (Exception ex)
        {
            // Don't let logging failures break the request
            _logger.LogError(ex, "Failed to log endpoint usage");
        }
    }

    // Redacts security-sensitive query params before any logging or analytics persistence.
    private static string MaskSensitiveQueryString(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString))
        {
            return string.Empty;
        }

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryString);
        var parts = new List<string>();

        foreach (var kv in query)
        {
            var key = kv.Key;
            var value = kv.Value.ToString();
            if (string.Equals(key, "api_key", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "token", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "auth", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "authorization", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "x-emby-token", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "x-emby-authorization", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("auth", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"{key}=<redacted>");
            }
            else
            {
                parts.Add($"{key}={value}");
            }
        }

        return parts.Count > 0 ? "?" + string.Join("&", parts) : string.Empty;
    }

    private static string[]? ParseItemTypes(string? includeItemTypes)
    {
        if (string.IsNullOrWhiteSpace(includeItemTypes))
        {
            return null;
        }

        return includeItemTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? GetExactPlaylistItemsRequestId(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 ||
            !parts[0].Equals("playlists", StringComparison.OrdinalIgnoreCase) ||
            !parts[2].Equals("items", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return parts[1];
    }

    private static string? ExtractImageTag(JsonElement item, string imageType)
    {
        if (item.TryGetProperty("ImageTags", out var imageTags) &&
            imageTags.ValueKind == JsonValueKind.Object)
        {
            foreach (var imageTag in imageTags.EnumerateObject())
            {
                if (string.Equals(imageTag.Name, imageType, StringComparison.OrdinalIgnoreCase))
                {
                    return imageTag.Value.GetString();
                }
            }
        }

        if (string.Equals(imageType, "Primary", StringComparison.OrdinalIgnoreCase) &&
            item.TryGetProperty("PrimaryImageTag", out var primaryImageTag))
        {
            return primaryImageTag.GetString();
        }

        return null;
    }

    /// <summary>
    /// Determines whether Spotify playlist count enrichment should run for a response.
    /// We only run enrichment for playlist-oriented payloads to avoid mutating unrelated item lists
    /// (for example, album browse responses requested by clients like Finer).
    /// </summary>
    private bool ShouldProcessSpotifyPlaylistCounts(JsonDocument response, string? includeItemTypes)
    {
        if (!_spotifySettings.Enabled)
        {
            return false;
        }

        if (response.RootElement.ValueKind != JsonValueKind.Object ||
            !response.RootElement.TryGetProperty("Items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var requestedTypes = ParseItemTypes(includeItemTypes);
        if (requestedTypes != null && requestedTypes.Length > 0)
        {
            return requestedTypes.Contains("Playlist", StringComparer.OrdinalIgnoreCase);
        }

        // If the request did not explicitly constrain types, inspect payload types.
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("Type", out var typeProp))
            {
                continue;
            }

            if (string.Equals(typeProp.GetString(), "Playlist", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Recovers SearchTerm directly from raw query string.
    /// Handles malformed clients that do not URL-encode '&' inside SearchTerm.
    /// </summary>
    private static string? RecoverSearchTermFromRawQuery(string? rawQueryString)
    {
        if (string.IsNullOrWhiteSpace(rawQueryString))
        {
            return null;
        }

        var query = rawQueryString[0] == '?' ? rawQueryString[1..] : rawQueryString;
        const string key = "SearchTerm=";
        var start = query.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var valueStart = start + key.Length;
        if (valueStart >= query.Length)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var i = valueStart;
        while (i < query.Length)
        {
            var ch = query[i];
            if (ch == '&')
            {
                var next = i + 1;
                var equalsIndex = query.IndexOf('=', next);
                var nextAmp = query.IndexOf('&', next);

                var isParameterDelimiter = equalsIndex > next &&
                                           (nextAmp < 0 || equalsIndex < nextAmp);

                if (isParameterDelimiter)
                {
                    break;
                }
            }

            sb.Append(ch);
            i++;
        }

        var encoded = sb.ToString();
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return string.Empty;
        }

        var plusAsSpace = encoded.Replace("+", " ");
        return Uri.UnescapeDataString(plusAsSpace);
    }

    /// <summary>
    /// Uses model-bound SearchTerm when valid; falls back to raw query recovery when needed.
    /// </summary>
    private static string? GetEffectiveSearchTerm(string? boundSearchTerm, string? rawQueryString)
    {
        var recovered = RecoverSearchTermFromRawQuery(rawQueryString);
        if (string.IsNullOrWhiteSpace(recovered))
        {
            return boundSearchTerm;
        }

        if (string.IsNullOrWhiteSpace(boundSearchTerm))
        {
            return recovered;
        }

        // Prefer recovered when it is meaningfully longer (common malformed '&' case).
        var boundTrimmed = boundSearchTerm.Trim();
        var recoveredTrimmed = recovered.Trim();
        return recoveredTrimmed.Length > boundTrimmed.Length
            ? recoveredTrimmed
            : boundSearchTerm;
    }

    private static string GetContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".aac" => "audio/aac",
            _ => "audio/mpeg"
        };
    }

    /// <summary>
    /// Scores search results based on fuzzy matching against the query.
    /// Returns items with their relevance scores.
    /// External results get a small boost to prioritize the larger catalog.
    /// </summary>
    private static List<(T Item, int Score)> ScoreSearchResults<T>(
        string query,
        List<T> items,
        Func<T, string> titleField,
        Func<T, string?> artistField,
        Func<T, string?> albumField,
        bool isExternal = false)
    {
        return items.Select(item =>
        {
            var title = titleField(item) ?? "";
            var artist = artistField(item) ?? "";
            var album = albumField(item) ?? "";

            // Token-based fuzzy matching: split query and fields into words
            var queryTokens = query.ToLower()
                .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            var fieldText = $"{title} {artist} {album}".ToLower();
            var fieldTokens = fieldText
                .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            if (queryTokens.Count == 0) return (item, 0);

            // Count how many query tokens match field tokens (with fuzzy tolerance)
            var matchedTokens = 0;
            foreach (var queryToken in queryTokens)
            {
                // Check if any field token matches this query token
                var hasMatch = fieldTokens.Any(fieldToken =>
                {
                    // Exact match or substring match
                    if (fieldToken.Contains(queryToken) || queryToken.Contains(fieldToken))
                        return true;

                    // Fuzzy match with Levenshtein distance
                    var similarity = FuzzyMatcher.CalculateSimilarity(queryToken, fieldToken);
                    return similarity >= 70; // 70% similarity threshold for individual words
                });

                if (hasMatch) matchedTokens++;
            }

            // Score = percentage of query tokens that matched
            var baseScore = (matchedTokens * 100) / queryTokens.Count;

            // Give external results a small boost (+5 points) to prioritize the larger catalog
            var finalScore = isExternal ? Math.Min(100, baseScore + 5) : baseScore;

            return (item, finalScore);
        }).ToList();
    }

    #endregion
}
