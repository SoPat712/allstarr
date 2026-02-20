using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Spotify;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    #region Helpers

    /// <summary>
    /// Helper to handle proxy responses with proper status code handling.
    /// </summary>
    private IActionResult HandleProxyResponse(JsonDocument? result, int statusCode, object? fallbackValue = null)
    {
        if (result != null)
        {
            return new JsonResult(JsonSerializer.Deserialize<object>(result.RootElement.GetRawText()));
        }

        // Handle error status codes
        if (statusCode == 401)
        {
            return Unauthorized();
        }
        else if (statusCode == 403)
        {
            return Forbid();
        }
        else if (statusCode == 404)
        {
            return NotFound();
        }
        else if (statusCode >= 400)
        {
            return StatusCode(statusCode);
        }

        // Success with no body - return fallback or empty
        if (fallbackValue != null)
        {
            return new JsonResult(fallbackValue);
        }

        return NoContent();
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
                        _logger.LogInformation("Found Spotify playlist: {Id}", playlistId);

                        // This is a Spotify playlist - get the actual track count
                        var playlistConfig = _spotifySettings.GetPlaylistByJellyfinId(playlistId);

                        if (playlistConfig != null)
                        {
                            _logger.LogInformation(
                                "Found playlist config for Jellyfin ID {JellyfinId}: {Name} (Spotify ID: {SpotifyId})",
                                playlistId, playlistConfig.Name, playlistConfig.Id);
                            var playlistName = playlistConfig.Name;

                            // Get matched external tracks (tracks that were successfully downloaded/matched)
                            var matchedTracksKey = CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlistName);
                            var matchedTracks = await _cache.GetAsync<List<MatchedTrack>>(matchedTracksKey);

                            _logger.LogInformation("Cache lookup for {Key}: {Count} matched tracks",
                                matchedTracksKey, matchedTracks?.Count ?? 0);

                            // Fallback to legacy cache format
                            if (matchedTracks == null || matchedTracks.Count == 0)
                            {
                                var legacyKey = $"spotify:matched:{playlistName}";
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

                            // Try loading from file cache if Redis is empty
                            if (matchedTracks == null || matchedTracks.Count == 0)
                            {
                                var fileItems = await LoadPlaylistItemsFromFile(playlistName);
                                if (fileItems != null && fileItems.Count > 0)
                                {
                                    _logger.LogDebug(
                                        "💿 Loaded {Count} playlist items from file cache for count update",
                                        fileItems.Count);
                                    // Use file cache count directly
                                    itemDict["ChildCount"] = fileItems.Count;
                                    modified = true;
                                }
                            }

                            // Only fetch from Jellyfin if we didn't get count from file cache
                            if (!itemDict.ContainsKey("ChildCount") ||
                                (itemDict["ChildCount"] is JsonElement childCountElement &&
                                 childCountElement.GetInt32() == 0) ||
                                (itemDict["ChildCount"] is int childCountInt && childCountInt == 0))
                            {
                                // Get local tracks count from Jellyfin
                                var localTracksCount = 0;
                                try
                                {
                                    // Include UserId parameter to avoid 401 Unauthorized
                                    var userId = _settings.UserId;
                                    var playlistItemsUrl = $"Playlists/{playlistId}/Items";
                                    var queryParams = new Dictionary<string, string>();
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
                                        localTracksCount = localItems.GetArrayLength();
                                        _logger.LogDebug("Found {Count} total items in Jellyfin playlist {Name}",
                                            localTracksCount, playlistName);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to get local tracks count for {Name}", playlistName);
                                }

                                // Count external matched tracks (not local)
                                var externalMatchedCount = 0;
                                if (matchedTracks != null)
                                {
                                    externalMatchedCount = matchedTracks.Count(t =>
                                        t.MatchedSong != null && !t.MatchedSong.IsLocal);
                                }

                                // Total available tracks = local tracks in Jellyfin + external matched tracks
                                // This represents what users will actually hear when playing the playlist
                                var totalAvailableCount = localTracksCount + externalMatchedCount;

                                if (totalAvailableCount > 0)
                                {
                                    // Update ChildCount to show actual available tracks
                                    itemDict["ChildCount"] = totalAvailableCount;
                                    modified = true;
                                    _logger.LogDebug(
                                        "✓ Updated ChildCount for Spotify playlist {Name} to {Total} ({Local} local + {External} external)",
                                        playlistName, totalAvailableCount, localTracksCount, externalMatchedCount);
                                }
                                else
                                {
                                    _logger.LogWarning(
                                        "No tracks found for {Name} ({Local} local + {External} external = {Total} total)",
                                        playlistName, localTracksCount, externalMatchedCount, totalAvailableCount);
                                }
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
                _logger.LogInformation("No Spotify playlists found to update");
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
                return JsonDocument.Parse(updatedJson);
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Spotify playlist counts");
            return response;
        }
    }

    /// <summary>
    /// Logs endpoint usage to a file for analysis.
    /// Creates a CSV file with timestamp, method, path, and query string.
    /// </summary>
    private async Task LogEndpointUsageAsync(string path, string method)
    {
        try
        {
            var logDir = "/app/cache/endpoint-usage";
            Directory.CreateDirectory(logDir);

            var logFile = Path.Combine(logDir, "endpoints.csv");
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            var queryString = Request.QueryString.HasValue ? Request.QueryString.Value : "";

            // Sanitize path and query for CSV (remove commas, quotes, newlines)
            var sanitizedPath = path.Replace(",", ";").Replace("\"", "'").Replace("\n", " ").Replace("\r", " ");
            var sanitizedQuery = queryString.Replace(",", ";").Replace("\"", "'").Replace("\n", " ").Replace("\r", " ");

            var logLine = $"{timestamp},{method},{sanitizedPath},{sanitizedQuery}\n";

            // Append to file (thread-safe)
            await System.IO.File.AppendAllTextAsync(logFile, logLine);
        }
        catch (Exception ex)
        {
            // Don't let logging failures break the request
            _logger.LogError(ex, "Failed to log endpoint usage");
        }
    }

    private static string[]? ParseItemTypes(string? includeItemTypes)
    {
        if (string.IsNullOrWhiteSpace(includeItemTypes))
        {
            return null;
        }

        return includeItemTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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