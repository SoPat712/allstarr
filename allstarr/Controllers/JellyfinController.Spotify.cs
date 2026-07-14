using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Spotify;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{

    #region Spotify Playlist Injection

    /// <summary>
    /// Gets tracks for a Spotify playlist by matching missing tracks against external providers
    /// and merging with existing local tracks from Jellyfin.
    ///
    /// Supports two modes:
    /// 1. Direct Spotify API (new): Uses SpotifyPlaylistFetcher for ordered tracks with ISRC matching
    /// 2. Jellyfin Plugin (legacy): Uses MissingTrack data from Jellyfin Spotify Import plugin
    /// </summary>
    private async Task<IActionResult> GetSpotifyPlaylistTracksAsync(string spotifyPlaylistName, string playlistId)
    {
        try
        {
            // Only inject tracks if Spotify API is enabled
            if (_spotifyApiSettings.Enabled && _spotifyPlaylistFetcher != null)
            {
                var orderedResult = await GetSpotifyPlaylistTracksOrderedAsync(spotifyPlaylistName, playlistId);
                if (orderedResult != null) return orderedResult;
            }

            // Spotify API not enabled or no ordered tracks - proxy through without modification
            _logger.LogDebug("Spotify API not enabled or no tracks found, proxying playlist {PlaylistName} without modification",
                spotifyPlaylistName);

            var endpoint = $"Playlists/{playlistId}/Items";
            if (Request.QueryString.HasValue)
            {
                endpoint = $"{endpoint}{Request.QueryString.Value}";
            }

            var (result, statusCode) = await _proxyService.GetJsonAsync(endpoint, null, Request.Headers);
            return HandleProxyResponse(result, statusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Spotify playlist tracks {PlaylistName}", spotifyPlaylistName);
            return _responseBuilder.CreateError(500, "Failed to get Spotify playlist tracks");
        }
    }

    /// <summary>
    /// New mode: Gets playlist tracks with correct ordering using direct Spotify API data.
    /// Optimized to only re-match when Jellyfin playlist changes (cheap check).
    /// </summary>
    private async Task<IActionResult?> GetSpotifyPlaylistTracksOrderedAsync(string spotifyPlaylistName,
        string playlistId)
    {
        // Check if Jellyfin playlist has changed (cheap API call)
        var jellyfinSignatureCacheKey = $"spotify:playlist:jellyfin-signature:{spotifyPlaylistName}";
        var currentJellyfinSignature = await GetJellyfinPlaylistSignatureAsync(playlistId);
        var cachedJellyfinSignature = await _cache.GetAsync<string>(jellyfinSignatureCacheKey);

        var jellyfinPlaylistChanged = cachedJellyfinSignature != currentJellyfinSignature;
        var requestNeedsGenreMetadata = RequestIncludesField("Genres");

        // Check Redis cache first for fast serving (only if Jellyfin playlist hasn't changed)
        var cacheKey = CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(spotifyPlaylistName);
        var cachedItems = await _cache.GetAsync<List<Dictionary<string, object?>>>(cacheKey);

        if (cachedItems != null && cachedItems.Count > 0 &&
            InjectedPlaylistItemHelper.ContainsSyntheticLocalItems(cachedItems))
        {
            _logger.LogWarning(
                "Ignoring Redis playlist cache for {Playlist}: found synthesized local items that should have remained raw Jellyfin objects",
                spotifyPlaylistName);
            await _cache.DeleteAsync(cacheKey);
            cachedItems = null;
        }

        if (cachedItems != null && cachedItems.Count > 0 &&
            InjectedPlaylistItemHelper.ContainsLegacyExternalSourceLabels(cachedItems))
        {
            _logger.LogInformation(
                "Ignoring Redis playlist cache for {Playlist}: external items still use legacy source labels",
                spotifyPlaylistName);
            await _cache.DeleteAsync(cacheKey);
            cachedItems = null;
        }

        if (cachedItems != null && cachedItems.Count > 0 &&
            requestNeedsGenreMetadata &&
            InjectedPlaylistItemHelper.ContainsLocalItemsMissingGenreMetadata(cachedItems))
        {
            _logger.LogWarning(
                "Ignoring Redis playlist cache for {Playlist}: local items are missing genre metadata required by this request",
                spotifyPlaylistName);
            await _cache.DeleteAsync(cacheKey);
            cachedItems = null;
        }

        if (cachedItems != null && cachedItems.Count > 0 && !jellyfinPlaylistChanged)
        {
            _logger.LogDebug("✅ Loaded {Count} playlist items from Redis cache for {Playlist} (Jellyfin unchanged)",
                cachedItems.Count, spotifyPlaylistName);

            return new JsonResult(new
            {
                Items = cachedItems,
                TotalRecordCount = cachedItems.Count,
                StartIndex = 0
            });
        }

        if (jellyfinPlaylistChanged)
        {
            _logger.LogInformation("🔄 Jellyfin playlist changed for {Playlist} - re-matching tracks",
                spotifyPlaylistName);
        }

        // Check file cache as fallback
        var fileItems = await LoadPlaylistItemsFromFile(spotifyPlaylistName);
        if (fileItems != null && fileItems.Count > 0 &&
            InjectedPlaylistItemHelper.ContainsSyntheticLocalItems(fileItems))
        {
            _logger.LogWarning(
                "Ignoring file playlist cache for {Playlist}: found synthesized local items that should have remained raw Jellyfin objects",
                spotifyPlaylistName);
            fileItems = null;
        }

        if (fileItems != null && fileItems.Count > 0 &&
            InjectedPlaylistItemHelper.ContainsLegacyExternalSourceLabels(fileItems))
        {
            _logger.LogInformation(
                "Ignoring file playlist cache for {Playlist}: external items still use legacy source labels",
                spotifyPlaylistName);
            fileItems = null;
        }

        if (fileItems != null && fileItems.Count > 0 &&
            requestNeedsGenreMetadata &&
            InjectedPlaylistItemHelper.ContainsLocalItemsMissingGenreMetadata(fileItems))
        {
            _logger.LogWarning(
                "Ignoring file playlist cache for {Playlist}: local items are missing genre metadata required by this request",
                spotifyPlaylistName);
            fileItems = null;
        }

        if (fileItems != null && fileItems.Count > 0 && !jellyfinPlaylistChanged)
        {
            _logger.LogDebug("✅ Loaded {Count} playlist items from file cache for {Playlist}",
                fileItems.Count, spotifyPlaylistName);

            // Restore to Redis cache
            await _cache.SetAsync(cacheKey, fileItems, CacheExtensions.SpotifyPlaylistItemsTTL);

            return new JsonResult(new
            {
                Items = fileItems,
                TotalRecordCount = fileItems.Count,
                StartIndex = 0
            });
        }

        // Check for ordered matched tracks from SpotifyTrackMatchingService
        var orderedCacheKey = CacheKeyBuilder.BuildSpotifyMatchedTracksKey(spotifyPlaylistName);
        var orderedTracks = await _cache.GetAsync<List<MatchedTrack>>(orderedCacheKey);

        if (orderedTracks == null || orderedTracks.Count == 0)
        {
            _logger.LogInformation("No ordered matched tracks in cache for {Playlist}, checking if we can fetch",
                spotifyPlaylistName);
            return null; // Fall back to legacy mode
        }

        _logger.LogDebug("Using {Count} ordered matched tracks for {Playlist}",
            orderedTracks.Count, spotifyPlaylistName);

        // Get existing Jellyfin playlist items (RAW - don't convert!)
        // CRITICAL: Must include UserId parameter or Jellyfin returns empty results
        var userId = _settings.UserId;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogError(
                "❌ JELLYFIN_USER_ID is NOT configured! Cannot fetch playlist tracks. Set it in .env or admin UI.");
            return null; // Fall back to legacy mode
        }

        // Pass through all requested fields from the original request
        var queryString = Request.QueryString.Value ?? "";
        var playlistItemsUrl = $"Playlists/{playlistId}/Items?UserId={userId}";

        // Append the original query string (which includes Fields parameter)
        if (!string.IsNullOrEmpty(queryString))
        {
            // Remove the leading ? if present
            queryString = queryString.TrimStart('?');
            playlistItemsUrl = $"{playlistItemsUrl}&{queryString}";
        }

        _logger.LogDebug("🔍 Fetching existing tracks from Jellyfin playlist {PlaylistId} with UserId {UserId}",
            playlistId, userId);

        var (existingTracksResponse, statusCode) = await _proxyService.GetJsonAsync(
            playlistItemsUrl,
            null,
            Request.Headers);

        if (statusCode != 200)
        {
            _logger.LogError(
                "❌ Failed to fetch Jellyfin playlist items: HTTP {StatusCode}. Check JELLYFIN_USER_ID is correct.",
                statusCode);
            return null;
        }

        // Keep raw Jellyfin items - don't convert to Song objects!
        var jellyfinItems = new List<JsonElement>();
        var jellyfinItemsByName = new Dictionary<string, JsonElement>();

        if (existingTracksResponse != null &&
            existingTracksResponse.RootElement.TryGetProperty("Items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                jellyfinItems.Add(item);

                // Index by title+artist for matching
                var title = item.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var artist = "";
                if (item.TryGetProperty("Artists", out var artistsEl) && artistsEl.GetArrayLength() > 0)
                {
                    artist = artistsEl[0].GetString() ?? "";
                }
                else if (item.TryGetProperty("AlbumArtist", out var albumArtistEl))
                {
                    artist = albumArtistEl.GetString() ?? "";
                }

                var key = $"{title}|{artist}".ToLowerInvariant();
                if (!jellyfinItemsByName.ContainsKey(key))
                {
                    jellyfinItemsByName[key] = item;
                }
            }

            _logger.LogDebug("✅ Found {Count} existing LOCAL tracks in Jellyfin playlist", jellyfinItems.Count);
        }
        else
        {
            _logger.LogWarning("⚠️ No existing tracks found in Jellyfin playlist {PlaylistId} - playlist may be empty",
                playlistId);
        }

        // Get the full playlist from Spotify to know the correct order
        var spotifyTracks = await _spotifyPlaylistFetcher!.GetPlaylistTracksAsync(spotifyPlaylistName);
        if (spotifyTracks.Count == 0)
        {
            _logger.LogWarning("Could not get Spotify playlist tracks for {Playlist}", spotifyPlaylistName);
            return null; // Fall back to legacy
        }

        // Build the final track list in correct Spotify order
        var finalItems = new List<Dictionary<string, object?>>();
        var usedJellyfinItems = new HashSet<string>();
        var localUsedCount = 0;
        var externalUsedCount = 0;
        var unresolvedLocalCount = 0;

        _logger.LogDebug("🔍 Building playlist in Spotify order with {SpotifyCount} positions...", spotifyTracks.Count);

        foreach (var spotifyTrack in spotifyTracks.OrderBy(t => t.Position))
        {
            // Try to find matching Jellyfin item by fuzzy matching
            JsonElement? matchedJellyfinItem = null;
            string? matchedKey = null;
            double bestScore = 0;

            foreach (var kvp in jellyfinItemsByName)
            {
                if (usedJellyfinItems.Contains(kvp.Key)) continue;

                var item = kvp.Value;
                var title = item.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var artist = "";
                if (item.TryGetProperty("Artists", out var artistsEl) && artistsEl.GetArrayLength() > 0)
                {
                    artist = artistsEl[0].GetString() ?? "";
                }

                var titleScore = FuzzyMatcher.CalculateSimilarity(spotifyTrack.Title, title);
                var artistScore = FuzzyMatcher.CalculateSimilarity(spotifyTrack.PrimaryArtist, artist);
                var totalScore = (titleScore * 0.7) + (artistScore * 0.3);

                if (totalScore > bestScore && totalScore >= 70)
                {
                    bestScore = totalScore;
                    matchedJellyfinItem = item;
                    matchedKey = kvp.Key;
                }
            }

            if (matchedJellyfinItem.HasValue && matchedKey != null)
            {
                // Use the raw Jellyfin item (preserves ALL metadata including MediaSources!)
                var itemDict = JsonElementToDictionary(matchedJellyfinItem.Value);
                ProviderIdsEnricher.EnsureSpotifyProviderIds(itemDict, spotifyTrack.SpotifyId, spotifyTrack.AlbumId);
                ApplySpotifyAddedAtDateCreated(itemDict, spotifyTrack.AddedAt);
                finalItems.Add(itemDict);
                usedJellyfinItems.Add(matchedKey);
                localUsedCount++;
                _logger.LogDebug("✅ Position #{Pos}: '{Title}' → LOCAL (score: {Score:F1}%)",
                    spotifyTrack.Position, spotifyTrack.Title, bestScore);
            }
            else
            {
                // No local match via fuzzy matching - try to find in orderedTracks cache
                var matched = orderedTracks?.FirstOrDefault(t => t.SpotifyId == spotifyTrack.SpotifyId);
                if (matched != null && matched.MatchedSong != null)
                {
                    // Check if this is a LOCAL track that we should fetch from Jellyfin
                    if (matched.MatchedSong.IsLocal && !string.IsNullOrEmpty(matched.MatchedSong.Id))
                    {
                        // Try to find the full Jellyfin item by ID
                        var jellyfinItem = jellyfinItems.FirstOrDefault(item =>
                            item.TryGetProperty("Id", out var idProp) &&
                            idProp.GetString() == matched.MatchedSong.Id);

                        if (jellyfinItem.ValueKind != JsonValueKind.Undefined)
                        {
                            // Found the full Jellyfin item - use it!
                            var itemDict = JsonElementToDictionary(jellyfinItem);
                            ProviderIdsEnricher.EnsureSpotifyProviderIds(itemDict, spotifyTrack.SpotifyId,
                                spotifyTrack.AlbumId);
                            ApplySpotifyAddedAtDateCreated(itemDict, spotifyTrack.AddedAt);
                            finalItems.Add(itemDict);
                            localUsedCount++;
                            _logger.LogDebug("✅ Position #{Pos}: '{Title}' → LOCAL from cache (ID: {Id})",
                                spotifyTrack.Position, spotifyTrack.Title, matched.MatchedSong.Id);
                            continue;
                        }
                        else
                        {
                            if (JellyfinItemSnapshotHelper.TryGetClonedRawItemSnapshot(
                                    matched.MatchedSong,
                                    out var cachedLocalItem))
                            {
                                ProviderIdsEnricher.EnsureSpotifyProviderIds(cachedLocalItem, spotifyTrack.SpotifyId,
                                    spotifyTrack.AlbumId);
                                ApplySpotifyAddedAtDateCreated(cachedLocalItem, spotifyTrack.AddedAt);
                                finalItems.Add(cachedLocalItem);
                                localUsedCount++;
                                _logger.LogDebug(
                                    "✅ Position #{Pos}: '{Title}' → LOCAL from cached raw snapshot (ID: {Id})",
                                    spotifyTrack.Position, spotifyTrack.Title, matched.MatchedSong.Id);
                                continue;
                            }

                            _logger.LogWarning(
                                "⚠️ Position #{Pos}: '{Title}' marked as LOCAL but not found in Jellyfin items (ID: {Id}); refusing to synthesize a replacement local object",
                                spotifyTrack.Position, spotifyTrack.Title, matched.MatchedSong.Id);
                            unresolvedLocalCount++;
                            continue;
                        }
                    }

                    // External track or local track not found - convert Song to Jellyfin item format
                    var externalItem = _responseBuilder.ConvertSongToJellyfinItem(matched.MatchedSong);

                    // Enhance with additional Spotify metadata
                    ProviderIdsEnricher.EnsureSpotifyProviderIds(externalItem, spotifyTrack.SpotifyId,
                        spotifyTrack.AlbumId);

                    ApplySpotifyAddedAtDateCreated(externalItem, spotifyTrack.AddedAt);

                    finalItems.Add(externalItem);
                    externalUsedCount++;
                    _logger.LogDebug(
                        "📥 Position #{Pos}: '{Title}' → EXTERNAL: {Provider}/{Id} (Spotify ID: {SpotifyId})",
                        spotifyTrack.Position, spotifyTrack.Title,
                        matched.MatchedSong.ExternalProvider, matched.MatchedSong.ExternalId, spotifyTrack.SpotifyId);
                }
                else
                {
                    _logger.LogDebug("❌ Position #{Pos}: '{Title}' → NO MATCH",
                        spotifyTrack.Position, spotifyTrack.Title);
                }
            }
        }

        _logger.LogDebug("🎵 Final playlist '{Playlist}': {Total} tracks ({Local} LOCAL + {External} EXTERNAL)",
            spotifyPlaylistName, finalItems.Count, localUsedCount, externalUsedCount);

        if (unresolvedLocalCount > 0)
        {
            _logger.LogWarning(
                "Aborting ordered injection for {Playlist}: {Count} local tracks could not be preserved from Jellyfin and would have been rewritten",
                spotifyPlaylistName, unresolvedLocalCount);
            await _cache.DeleteAsync(cacheKey);
            return null;
        }

        if (InjectedPlaylistItemHelper.ContainsSyntheticLocalItems(finalItems))
        {
            _logger.LogWarning(
                "Aborting ordered injection for {Playlist}: built playlist still contains synthesized local items",
                spotifyPlaylistName);
            await _cache.DeleteAsync(cacheKey);
            return null;
        }

        // Save to file cache for persistence across restarts
        await SavePlaylistItemsToFile(spotifyPlaylistName, finalItems);

        // Also cache in Redis for fast serving (reuse the same cache key from top of method)
        await _cache.SetAsync(cacheKey, finalItems, CacheExtensions.SpotifyPlaylistItemsTTL);

        // Cache the Jellyfin playlist signature to detect future changes
        await _cache.SetAsync(jellyfinSignatureCacheKey, currentJellyfinSignature,
            CacheExtensions.SpotifyPlaylistItemsTTL);

        // Return raw Jellyfin response format
        return new JsonResult(new
        {
            Items = finalItems,
            TotalRecordCount = finalItems.Count,
            StartIndex = 0
        });
    }

    private static void ApplySpotifyAddedAtDateCreated(
        Dictionary<string, object?> item,
        DateTime? addedAt)
    {
        if (!addedAt.HasValue)
        {
            return;
        }

        item["DateCreated"] = addedAt.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
    }

    private bool RequestIncludesField(string fieldName)
    {
        if (!Request.Query.TryGetValue("Fields", out var rawValues) || rawValues.Count == 0)
        {
            return false;
        }

        foreach (var rawValue in rawValues)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            var fields = rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Any(field => string.Equals(field, fieldName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets a signature of the Jellyfin playlist to detect changes.
    /// </summary>
    private async Task<string> GetJellyfinPlaylistSignatureAsync(string playlistId)
    {
        try
        {
            var userId = _settings.UserId;
            var playlistItemsUrl = $"Playlists/{playlistId}/Items?Fields=Id";
            if (!string.IsNullOrEmpty(userId))
            {
                playlistItemsUrl += $"&UserId={userId}";
            }

            var (response, _) = await _proxyService.GetJsonAsync(playlistItemsUrl, null, Request.Headers);

            if (response != null && response.RootElement.TryGetProperty("Items", out var items))
            {
                var trackIds = new List<string>();
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("Id", out var idEl))
                    {
                        trackIds.Add(idEl.GetString() ?? "");
                    }
                }

                // Create signature: count + sorted IDs (sorted for consistency)
                trackIds.Sort();
                var signature = $"{trackIds.Count}:{string.Join(",", trackIds)}";

                // Hash it to keep it compact
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(signature));
                return Convert.ToHexString(hashBytes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get Jellyfin playlist signature for {PlaylistId}", playlistId);
        }

        // Return empty string if failed (will trigger re-match)
        return string.Empty;
    }

    /// <summary>
    /// Saves playlist items (raw Jellyfin JSON) to file cache for persistence across restarts.
    /// </summary>
    private async Task SavePlaylistItemsToFile(string playlistName, List<Dictionary<string, object?>> items)
    {
        try
        {
            var cacheDir = "/app/cache/spotify";
            Directory.CreateDirectory(cacheDir);

            var safeName = string.Join("_", playlistName.Split(Path.GetInvalidFileNameChars()));
            var filePath = Path.Combine(cacheDir, $"{safeName}_items.json");

            var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(filePath, json);

            _logger.LogDebug("💾 Saved {Count} playlist items to file cache for {Playlist}",
                items.Count, playlistName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save playlist items to file for {Playlist}", playlistName);
        }
    }

    /// <summary>
    /// Loads playlist items (raw Jellyfin JSON) from file cache.
    /// </summary>
    private async Task<List<Dictionary<string, object?>>?> LoadPlaylistItemsFromFile(string playlistName)
    {
        try
        {
            var safeName = string.Join("_", playlistName.Split(Path.GetInvalidFileNameChars()));
            var filePath = Path.Combine("/app/cache/spotify", $"{safeName}_items.json");

            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogDebug("No playlist items file cache found for {Playlist} at {Path}", playlistName, filePath);
                return null;
            }

            var fileAge = DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(filePath);

            // Check if cache is too old (more than 24 hours)
            if (fileAge.TotalHours > 24)
            {
                _logger.LogDebug("Playlist items file cache for {Playlist} is too old ({Age:F1}h), will rebuild",
                    playlistName, fileAge.TotalHours);
                return null;
            }

            _logger.LogDebug("Playlist items file cache for {Playlist} age: {Age:F1}h", playlistName,
                fileAge.TotalHours);

            var json = await System.IO.File.ReadAllTextAsync(filePath);

            // Parse as JsonDocument first to preserve nested structures
            using var doc = JsonDocument.Parse(json);
            var items = new List<Dictionary<string, object?>>();

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    items.Add(JsonElementToDictionary(item));
                }
            }

            _logger.LogDebug("💿 Loaded {Count} playlist items from file cache for {Playlist} (age: {Age:F1}h)",
                items.Count, playlistName, fileAge.TotalHours);

            return items;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load playlist items from file for {Playlist}", playlistName);
            return null;
        }
    }

    #endregion
}
