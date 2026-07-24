using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Spotify;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using allstarr.Services.Spotify;
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
            // Both the direct Spotify API importer and the Jellyfin Spotify plugin build the
            // same ordered injected-items cache. Serving that cache must not depend on which
            // importer populated it.
            var orderedResult = await GetSpotifyPlaylistTracksOrderedAsync(spotifyPlaylistName, playlistId);
            if (orderedResult != null) return orderedResult;

            // No injected cache is ready yet. Preserve the local playlist while matching runs.
            _logger.LogDebug("No injected tracks are ready, proxying playlist {PlaylistName} without modification",
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
        var directSpotifyMode = _spotifyApiSettings.Enabled && _spotifyPlaylistFetcher != null;
        // Check if Jellyfin playlist has changed (cheap API call)
        var jellyfinSignatureCacheKey =
            CacheKeyBuilder.BuildSpotifyPlaylistJellyfinSignatureKey(spotifyPlaylistName);
        var currentJellyfinSignature = await GetJellyfinPlaylistSignatureAsync(playlistId);
        var cachedJellyfinSignature = await _cache.GetAsync<string>(jellyfinSignatureCacheKey);

        var jellyfinPlaylistChanged = cachedJellyfinSignature != currentJellyfinSignature;
        var requestNeedsGenreMetadata = RequestIncludesField("Genres");

        // Check the shared cache first when the Jellyfin playlist has not changed.
        var cacheKey = CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(spotifyPlaylistName);
        var cachedItems = await _cache.GetAsync<List<Dictionary<string, object?>>>(cacheKey);
        NormalizeSyntheticPlaylistItems(cachedItems);

        if (cachedItems != null && cachedItems.Count > 0 &&
            InjectedPlaylistItemHelper.ContainsSyntheticLocalItems(cachedItems))
        {
            _logger.LogWarning(
                "Ignoring shared playlist cache for {Playlist}: found synthesized local items that should have remained raw Jellyfin objects",
                spotifyPlaylistName);
            await _cache.DeleteAsync(cacheKey);
            cachedItems = null;
        }

        if (cachedItems != null && cachedItems.Count > 0 &&
            InjectedPlaylistItemHelper.ContainsLegacyExternalSourceLabels(cachedItems))
        {
            _logger.LogInformation(
                "Ignoring shared playlist cache for {Playlist}: external items still use legacy source labels",
                spotifyPlaylistName);
            await _cache.DeleteAsync(cacheKey);
            cachedItems = null;
        }

        if (cachedItems != null && cachedItems.Count > 0 &&
            InjectedPlaylistItemHelper.ContainsUnavailableExternalItems(cachedItems))
        {
            _logger.LogWarning(
                "Ignoring shared playlist cache for {Playlist}: it contains unavailable external tracks",
                spotifyPlaylistName);
            await _cache.DeleteAsync(cacheKey);
            cachedItems = null;
        }

        if (cachedItems != null && cachedItems.Count > 0 &&
            requestNeedsGenreMetadata &&
            InjectedPlaylistItemHelper.ContainsLocalItemsMissingGenreMetadata(cachedItems))
        {
            _logger.LogWarning(
                "Ignoring shared playlist cache for {Playlist}: local items are missing genre metadata required by this request",
                spotifyPlaylistName);
            await _cache.DeleteAsync(cacheKey);
            cachedItems = null;
        }

        if (cachedItems != null && cachedItems.Count > 0 &&
            (!jellyfinPlaylistChanged || !directSpotifyMode))
        {
            _logger.LogDebug("✅ Loaded {Count} playlist items from shared cache for {Playlist} (Jellyfin unchanged)",
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

        // Without direct Spotify metadata, the legacy matcher is the cache producer. If its
        // pre-built items are not ready yet, retain the local Jellyfin playlist for this request.
        if (!directSpotifyMode)
        {
            return null;
        }

        // Check for ordered tracks retained by the playlist matching coordinator.
        var orderedCacheKey = CacheKeyBuilder.BuildSpotifyMatchedTracksKey(spotifyPlaylistName);
        var orderedTracks = await _cache.GetAsync<List<MatchedTrack>>(orderedCacheKey);

        if (orderedTracks != null)
        {
            var playableOrderedTracks = orderedTracks
                .Where(track => ExternalTrackPlaybackPolicy.CanUseForPlayback(track.MatchedSong))
                .ToList();
            if (playableOrderedTracks.Count != orderedTracks.Count)
            {
                _logger.LogWarning(
                    "Discarded {Count} unavailable ordered matches from {Playlist}",
                    orderedTracks.Count - playableOrderedTracks.Count,
                    spotifyPlaylistName);
                orderedTracks = playableOrderedTracks;
                if (orderedTracks.Count > 0)
                {
                    await _cache.SetAsync(
                        orderedCacheKey,
                        orderedTracks,
                        CacheExtensions.SpotifyMatchedTracksTTL);
                }
                else
                {
                    await _cache.DeleteAsync(orderedCacheKey);
                }
            }
        }

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

                    if (!ExternalTrackPlaybackPolicy.CanUseForPlayback(matched.MatchedSong))
                    {
                        _logger.LogWarning(
                            "Skipping unavailable external match for {Title}: {Provider}/{Id}",
                            spotifyTrack.Title,
                            matched.MatchedSong.ExternalProvider,
                            matched.MatchedSong.ExternalId);
                        continue;
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

        // Also cache for fast serving using the same key from the top of the method.
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

    private void NormalizeSyntheticPlaylistItems(List<Dictionary<string, object?>>? items)
    {
        if (items == null) return;

        foreach (var item in items)
        {
            if (!item.TryGetValue("Id", out var rawId)) continue;
            var id = rawId switch
            {
                string value => value,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null
            };
            if (id?.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) != true) continue;

            // A Jellyfin client associates images with the server identity it connected to.
            // Repair older caches at the serving boundary so upgrades do not require a rematch.
            item["ServerId"] = _settings.DeviceId;
            item["HasLyrics"] = true;
            item["ImageTags"] = new Dictionary<string, string>
            {
                ["Primary"] = $"{id}-art-v2"
            };
        }
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

    #endregion
}
