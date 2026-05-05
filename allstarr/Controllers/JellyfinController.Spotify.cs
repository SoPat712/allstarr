using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Spotify;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private static readonly string[] KeptAudioExtensions = [".flac", ".mp3", ".m4a", ".opus"];

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
    /// <summary>
    /// Copies an external track to the kept folder when favorited.
    /// </summary>
    private async Task CopyExternalTrackToKeptAsync(string itemId, string provider, string externalId)
    {
        try
        {
            // Check if already favorited (persistent tracking)
            if (await IsTrackFavoritedAsync(itemId))
            {
                _logger.LogInformation("Track already favorited (persistent): {ItemId}", itemId);
                return;
            }

            // Get the song metadata first to build paths
            var song = await _metadataService.GetSongAsync(provider, externalId);
            if (song == null)
            {
                _logger.LogWarning("Could not find song metadata for {ItemId}", itemId);
                return;
            }

            // Build kept folder path: Artist/Album/
            var keptBasePath = Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "kept");
            var keptArtistPath = Path.Combine(keptBasePath, AdminHelperService.SanitizeFileName(song.Artist));
            var keptAlbumPath = Path.Combine(keptArtistPath, AdminHelperService.SanitizeFileName(song.Album));

            // Check if track already exists in kept folder
            if (Directory.Exists(keptAlbumPath))
            {
                var sanitizedTitle = AdminHelperService.SanitizeFileName(song.Title);
                var existingAudioFiles = Directory.GetFiles(keptAlbumPath, $"*{sanitizedTitle}*")
                    .Where(IsKeptAudioFile)
                    .ToArray();
                if (existingAudioFiles.Length > 0)
                {
                    _logger.LogInformation("Track already exists in kept folder: {Path}", existingAudioFiles[0]);
                    await EnsureLyricsSidecarForKeptTrackAsync(existingAudioFiles[0], song, provider, externalId);
                    // Mark as favorited even if we didn't download it
                    await MarkTrackAsFavoritedAsync(itemId, song);
                    return;
                }
            }

            // Look for the track in cache folder first
            var cacheBasePath = Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "cache");
            var cacheArtistPath = Path.Combine(cacheBasePath, AdminHelperService.SanitizeFileName(song.Artist));
            var cacheAlbumPath = Path.Combine(cacheArtistPath, AdminHelperService.SanitizeFileName(song.Album));

            string? sourceFilePath = null;

            if (Directory.Exists(cacheAlbumPath))
            {
                var sanitizedTitle = AdminHelperService.SanitizeFileName(song.Title);
                var cacheFiles = Directory.GetFiles(cacheAlbumPath, $"*{sanitizedTitle}*");
                if (cacheFiles.Length > 0)
                {
                    sourceFilePath = cacheFiles[0];
                    _logger.LogDebug("Found track in cache folder: {Path}", sourceFilePath);
                }
            }

            // If not in cache, download it first
            if (sourceFilePath == null)
            {
                _logger.LogInformation("Track not in cache, downloading: {ItemId}", itemId);
                try
                {
                    // Use CancellationToken.None to ensure download completes even if user navigates away
                    sourceFilePath =
                        await _downloadService.DownloadSongAsync(provider, externalId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download track {ItemId}", itemId);
                    return;
                }
            }

            // Create the kept folder structure
            Directory.CreateDirectory(keptAlbumPath);

            // Copy file to kept folder
            var fileName = Path.GetFileName(sourceFilePath);
            var keptFilePath = Path.Combine(keptAlbumPath, fileName);

            // Create hard link instead of copying to save space
            // Both locations will point to the same file data on disk
            try
            {
                // Use ln command on Unix systems for hard links
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ln",
                        Arguments = $"\"{sourceFilePath}\" \"{keptFilePath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    });

                    if (process != null)
                    {
                        await process.WaitForExitAsync();

                        // Check if link was created successfully
                        if (process.ExitCode != 0)
                        {
                            throw new IOException($"ln command failed with exit code {process.ExitCode}");
                        }

                        _logger.LogInformation("🔗 Created hard link: {Source} → {Destination}", sourceFilePath, keptFilePath);
                    }
                }
                else
                {
                    // Fall back to copy on Windows
                    System.IO.File.Copy(sourceFilePath, keptFilePath, overwrite: false);
                    _logger.LogInformation("📋 Copied track: {Source} → {Destination}", sourceFilePath, keptFilePath);
                }
            }
            catch (IOException ex) when (ex.Message.Contains("already exists") || System.IO.File.Exists(keptFilePath))
            {
                // Race condition - file was created by another request
                _logger.LogInformation("Track already exists in kept folder (race condition): {Path}", keptFilePath);
                await EnsureLyricsSidecarForKeptTrackAsync(keptFilePath, song, provider, externalId);
                await MarkTrackAsFavoritedAsync(itemId, song);
                return;
            }
            catch (Exception ex)
            {
                // Fall back to copy if hard link fails (e.g., different filesystems)
                _logger.LogWarning(ex, "Failed to create hard link, falling back to copy");

                try
                {
                    System.IO.File.Copy(sourceFilePath, keptFilePath, overwrite: false);
                    _logger.LogInformation("📋 Copied track (fallback): {Source} → {Destination}", sourceFilePath, keptFilePath);
                }
                catch (IOException copyEx) when (copyEx.Message.Contains("already exists") || System.IO.File.Exists(keptFilePath))
                {
                    // Race condition on copy fallback
                    _logger.LogInformation("Track already exists in kept folder (race condition on copy): {Path}", keptFilePath);
                    await EnsureLyricsSidecarForKeptTrackAsync(keptFilePath, song, provider, externalId);
                    await MarkTrackAsFavoritedAsync(itemId, song);
                    return;
                }
            }

            // Also create hard link for cover art if it exists
            var sourceCoverPath = Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "cover.jpg");
            if (System.IO.File.Exists(sourceCoverPath))
            {
                var keptCoverPath = Path.Combine(keptAlbumPath, "cover.jpg");
                if (!System.IO.File.Exists(keptCoverPath))
                {
                    try
                    {
                        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                        {
                            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "ln",
                                Arguments = $"\"{sourceCoverPath}\" \"{keptCoverPath}\"",
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false
                            });

                            if (process != null)
                            {
                                await process.WaitForExitAsync();
                                _logger.LogDebug("🔗 Created hard link for cover: {Source} → {Destination}", sourceCoverPath, keptCoverPath);
                            }
                        }
                        else
                        {
                            System.IO.File.Copy(sourceCoverPath, keptCoverPath, overwrite: false);
                            _logger.LogDebug("📋 Copied cover: {Source} → {Destination}", sourceCoverPath, keptCoverPath);
                        }
                    }
                    catch (IOException ex) when (ex.Message.Contains("already exists") || System.IO.File.Exists(keptCoverPath))
                    {
                        // Race condition - cover already exists
                        _logger.LogDebug("Cover art already exists (race condition)");
                    }
                    catch (Exception ex)
                    {
                        // Fall back to copy if hard link fails
                        _logger.LogDebug(ex, "Failed to create hard link for cover, falling back to copy");

                        try
                        {
                            System.IO.File.Copy(sourceCoverPath, keptCoverPath, overwrite: false);
                            _logger.LogDebug("📋 Copied cover (fallback): {Source} → {Destination}", sourceCoverPath, keptCoverPath);
                        }
                        catch (IOException copyEx) when (copyEx.Message.Contains("already exists") || System.IO.File.Exists(keptCoverPath))
                        {
                            // Race condition on copy fallback
                            _logger.LogDebug("Cover art already exists (race condition on copy)");
                        }
                    }
                }
            }

            await EnsureLyricsSidecarForKeptTrackAsync(keptFilePath, song, provider, externalId);

            // Mark as favorited in persistent storage
            await MarkTrackAsFavoritedAsync(itemId, song);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error copying external track {ItemId} to kept folder", itemId);
        }
    }

    /// <summary>
    /// Copies an external album (all tracks) to the kept folder when favorited.
    /// </summary>
    private async Task CopyExternalAlbumToKeptAsync(string itemId, string provider, string externalId)
    {
        try
        {
            // Get the album metadata with all tracks
            var album = await _metadataService.GetAlbumAsync(provider, externalId);
            if (album == null)
            {
                _logger.LogWarning("Could not find album metadata for {ItemId}", itemId);
                return;
            }

            _logger.LogInformation("Downloading {Count} tracks from album: {Artist} - {Album}",
                album.Songs.Count, album.Artist, album.Title);

            // Download all tracks in the album
            var downloadTasks = album.Songs.Select(async song =>
            {
                try
                {
                    var songItemId = song.Id; // Already in format: ext-provider-song-id
                    var (_, songProvider, songExternalId) = _localLibraryService.ParseSongId(songItemId);

                    if (songProvider != null && songExternalId != null)
                    {
                        await CopyExternalTrackToKeptAsync(songItemId, songProvider, songExternalId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download track: {Title}", song.Title);
                }
            });

            await Task.WhenAll(downloadTasks);

            _logger.LogInformation("Finished downloading album: {Artist} - {Album}", album.Artist, album.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error copying external album {ItemId} to kept folder", itemId);
        }
    }

    /// <summary>
    /// Removes an external track from the kept folder when unfavorited.
    /// </summary>
    private async Task RemoveExternalTrackFromKeptAsync(string itemId, string provider, string externalId)
    {
        try
        {
            // Mark for deletion instead of immediate deletion
            await MarkTrackForDeletionAsync(itemId);
            _logger.LogInformation("✓ Marked track for deletion: {ItemId}", itemId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking external track {ItemId} for deletion", itemId);
        }
    }

    #region Persistent Favorites Tracking
    
    /// <summary>
    /// Information about a favorited track for persistent storage.
    /// </summary>
    private class FavoriteTrackInfo
    {
        public string ItemId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public DateTime FavoritedAt { get; set; }
    }

    /// <summary>
    /// Checks if a track is already favorited (persistent across restarts).
    /// </summary>
    private async Task<bool> IsTrackFavoritedAsync(string itemId)
    {
        try
        {
            return await _cache.ExistsAsync($"favorites:{itemId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check favorite status for {ItemId}", itemId);
            return false;
        }
    }

    /// <summary>
    /// Marks a track as favorited in persistent storage.
    /// </summary>
    private async Task MarkTrackAsFavoritedAsync(string itemId, Song song)
    {
        try
        {
            var info = new FavoriteTrackInfo
            {
                ItemId = itemId,
                Title = song.Title ?? "Unknown Title",
                Artist = song.Artist ?? "Unknown Artist",
                Album = song.Album ?? "Unknown Album",
                FavoritedAt = DateTime.UtcNow
            };
            
            await _cache.SetAsync($"favorites:{itemId}", info);
            _logger.LogDebug("Marked track as favorited: {ItemId}", itemId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark track as favorited: {ItemId}", itemId);
        }
    }

    /// <summary>
    /// Removes a track from persistent favorites storage.
    /// </summary>
    private async Task UnmarkTrackAsFavoritedAsync(string itemId)
    {
        try
        {
            if (await _cache.ExistsAsync($"favorites:{itemId}"))
            {
                await _cache.DeleteAsync($"favorites:{itemId}");
                _logger.LogDebug("Removed track from favorites: {ItemId}", itemId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove track from favorites: {ItemId}", itemId);
        }
    }

    /// <summary>
    /// Marks a track for deletion (delayed deletion for safety).
    /// </summary>
    private async Task MarkTrackForDeletionAsync(string itemId)
    {
        try
        {
            var deletionTime = DateTime.UtcNow.AddHours(24);
            await _cache.SetStringAsync($"pending_deletion:{itemId}", deletionTime.ToString("O"));

            // Also remove from favorites immediately
            await UnmarkTrackAsFavoritedAsync(itemId);

            _logger.LogDebug("Marked track for deletion in 24 hours: {ItemId}", itemId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark track for deletion: {ItemId}", itemId);
        }
    }

    /// <summary>
    /// Processes pending deletions (called by cleanup service).
    /// </summary>
    public async Task ProcessPendingDeletionsAsync()
    {
        try
        {
            var deletionKeys = _cache.GetKeysByPattern("pending_deletion:*").ToList();
            if (deletionKeys.Count == 0) return;

            var now = DateTime.UtcNow;
            int deletedCount = 0;

            foreach (var key in deletionKeys)
            {
                var timeStr = await _cache.GetStringAsync(key);
                if (string.IsNullOrEmpty(timeStr)) continue;

                if (DateTime.TryParse(timeStr, out var scheduleTime) && scheduleTime <= now)
                {
                    var itemId = key.Substring("pending_deletion:".Length);
                    await ActuallyDeleteTrackAsync(itemId);
                    await _cache.DeleteAsync(key);
                    deletedCount++;
                }
            }

            if (deletedCount > 0)
            {
                _logger.LogDebug("Processed {Count} pending deletions", deletedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing pending deletions");
        }
    }

    /// <summary>
    /// Actually deletes a track from the kept folder.
    /// </summary>
    private async Task ActuallyDeleteTrackAsync(string itemId)
    {
        try
        {
            var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);
            if (!isExternal) return;

            var song = await _metadataService.GetSongAsync(provider!, externalId!);
            if (song == null) return;

            var keptBasePath = Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "kept");
            var keptArtistPath = Path.Combine(keptBasePath, AdminHelperService.SanitizeFileName(song.Artist));
            var keptAlbumPath = Path.Combine(keptArtistPath, AdminHelperService.SanitizeFileName(song.Album));

            if (!Directory.Exists(keptAlbumPath)) return;

            var sanitizedTitle = AdminHelperService.SanitizeFileName(song.Title);
            var trackFiles = Directory.GetFiles(keptAlbumPath, $"*{sanitizedTitle}*");

            foreach (var trackFile in trackFiles)
            {
                System.IO.File.Delete(trackFile);
                _logger.LogDebug("✓ Deleted track from kept folder: {Path}", trackFile);
            }

            // Clean up empty directories
            if (Directory.GetFiles(keptAlbumPath).Length == 0 && Directory.GetDirectories(keptAlbumPath).Length == 0)
            {
                Directory.Delete(keptAlbumPath);

                if (Directory.Exists(keptArtistPath) &&
                    Directory.GetFiles(keptArtistPath).Length == 0 &&
                    Directory.GetDirectories(keptArtistPath).Length == 0)
                {
                    Directory.Delete(keptArtistPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete track {ItemId}", itemId);
        }
    }

    private async Task EnsureLyricsSidecarForKeptTrackAsync(string keptFilePath, Song song, string provider, string externalId)
    {
        if (_keptLyricsSidecarService == null)
        {
            return;
        }

        try
        {
            await _keptLyricsSidecarService.EnsureSidecarAsync(
                keptFilePath,
                song,
                provider,
                externalId,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to create kept lyrics sidecar for {Path}", keptFilePath);
        }
    }

    private static bool IsKeptAudioFile(string path)
    {
        return KeptAudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    }

    #endregion

    /// <summary>
    /// Loads missing tracks from file cache as fallback when Redis is empty.
    /// <summary>
    /// Gets a signature (hash) of the Jellyfin playlist to detect changes.
    /// This is a cheap operation compared to re-matching all tracks.
    /// Signature includes: track count + concatenated track IDs.
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
