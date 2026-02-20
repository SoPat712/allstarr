using System.Text.Json;
using allstarr.Models.Scrobbling;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    #region Playback Session Reporting

    #region Session Management

    /// <summary>
    /// Reports session capabilities. Required for Jellyfin to track active sessions.
    /// Handles both POST (with body) and GET (query params only) methods.
    /// </summary>
    [HttpPost("Sessions/Capabilities")]
    [HttpPost("Sessions/Capabilities/Full")]
    [HttpGet("Sessions/Capabilities")]
    [HttpGet("Sessions/Capabilities/Full")]
    public async Task<IActionResult> ReportCapabilities()
    {
        try
        {
            var method = Request.Method;
            var queryString = Request.QueryString.HasValue ? Request.QueryString.Value : "";

            _logger.LogDebug("📡 Session capabilities reported - Method: {Method}, Query: {Query}", method,
                queryString);
            _logger.LogInformation("Headers: {Headers}",
                string.Join(", ", Request.Headers.Where(h =>
                        h.Key.Contains("Auth", StringComparison.OrdinalIgnoreCase) ||
                        h.Key.Contains("Device", StringComparison.OrdinalIgnoreCase) ||
                        h.Key.Contains("Client", StringComparison.OrdinalIgnoreCase))
                    .Select(h => $"{h.Key}={h.Value}")));

            // Forward to Jellyfin with query string and headers
            var endpoint = $"Sessions/Capabilities{queryString}";

            // Read body if present (POST requests)
            string body = "{}";
            if (method == "POST" && Request.ContentLength > 0)
            {
                Request.EnableBuffering();
                using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8,
                           detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
                {
                    body = await reader.ReadToEndAsync();
                }

                Request.Body.Position = 0;
                _logger.LogInformation("Capabilities body: {Body}", body);
            }

            var (result, statusCode) = await _proxyService.PostJsonAsync(endpoint, body, Request.Headers);

            if (statusCode == 204 || statusCode == 200)
            {
                _logger.LogDebug("✓ Session capabilities forwarded to Jellyfin ({StatusCode})", statusCode);
            }
            else if (statusCode == 401)
            {
                _logger.LogWarning("⚠ Jellyfin returned 401 for capabilities (token expired)");
            }
            else
            {
                _logger.LogWarning("⚠ Jellyfin returned {StatusCode} for capabilities", statusCode);
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to report session capabilities");
            return StatusCode(500);
        }
    }

    /// <summary>
    /// Reports playback start. Handles both local and external tracks.
    /// For local tracks, forwards to Jellyfin. For external tracks, logs locally.
    /// Also ensures session is initialized if this is the first report from a device.
    /// </summary>
    [HttpPost("Sessions/Playing")]
    public async Task<IActionResult> ReportPlaybackStart()
    {
        try
        {
            Request.EnableBuffering();
            string body;
            using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8,
                       detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
            {
                body = await reader.ReadToEndAsync();
            }

            Request.Body.Position = 0;

            _logger.LogDebug("📻 Playback START reported");

            // Parse the body to check if it's an external track
            var doc = JsonDocument.Parse(body);
            string? itemId = null;
            string? itemName = null;
            long? positionTicks = null;

            if (doc.RootElement.TryGetProperty("ItemId", out var itemIdProp))
            {
                itemId = itemIdProp.GetString();
            }

            if (doc.RootElement.TryGetProperty("ItemName", out var itemNameProp))
            {
                itemName = itemNameProp.GetString();
            }

            if (doc.RootElement.TryGetProperty("PositionTicks", out var posProp))
            {
                positionTicks = posProp.GetInt64();
            }

            // Track the playing item for scrobbling on session cleanup (local tracks only)
            var (deviceId, client, device, version) = ExtractDeviceInfo(Request.Headers);

            // Only update session for local tracks - external tracks don't need session tracking
            if (!string.IsNullOrEmpty(deviceId) && !string.IsNullOrEmpty(itemId))
            {
                var (isExt, _, _) = _localLibraryService.ParseSongId(itemId);
                if (!isExt)
                {
                    _sessionManager.UpdatePlayingItem(deviceId, itemId, positionTicks);
                }
            }

            if (!string.IsNullOrEmpty(itemId))
            {
                var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

                if (isExternal)
                {
                    // Fetch metadata early so we can log the correct track name
                    var song = await _metadataService.GetSongAsync(provider!, externalId!);
                    var trackName = song != null ? $"{song.Artist} - {song.Title}" : "Unknown";

                    _logger.LogInformation("🎵 External track playback started: {TrackName} ({Provider}/{ExternalId})",
                        trackName, provider, externalId);

                    // Proactively fetch lyrics in background for external tracks
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await PrefetchLyricsForTrackAsync(itemId, isExternal: true, provider, externalId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to prefetch lyrics for external track {ItemId}", itemId);
                        }
                    });

                    // Create a ghost/fake item to report to Jellyfin so "Now Playing" shows up
                    // Generate a deterministic UUID from the external ID
                    var ghostUuid = GenerateUuidFromString(itemId);

                    // Build minimal playback start with just the ghost UUID
                    // Don't include the Item object - Jellyfin will just track the session without item details
                    var playbackStart = new
                    {
                        ItemId = ghostUuid,
                        PositionTicks = positionTicks ?? 0,
                        CanSeek = true,
                        IsPaused = false,
                        IsMuted = false,
                        PlayMethod = "DirectPlay"
                    };

                    var playbackJson = JsonSerializer.Serialize(playbackStart);
                    _logger.LogDebug("📤 Sending ghost playback start for external track: {Json}", playbackJson);

                    // Forward to Jellyfin with ghost UUID
                    var (ghostResult, ghostStatusCode) =
                        await _proxyService.PostJsonAsync("Sessions/Playing", playbackJson, Request.Headers);

                    if (ghostStatusCode == 204 || ghostStatusCode == 200)
                    {
                        _logger.LogDebug(
                            "✓ Ghost playback start forwarded to Jellyfin for external track ({StatusCode})",
                            ghostStatusCode);
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ Ghost playback start returned status {StatusCode} for external track",
                            ghostStatusCode);
                    }

                    // Scrobble external track playback start
                    _logger.LogInformation(
                        "🎵 Checking scrobbling: orchestrator={HasOrchestrator}, helper={HasHelper}, deviceId={DeviceId}",
                        _scrobblingOrchestrator != null, _scrobblingHelper != null, deviceId ?? "null");

                    if (_scrobblingOrchestrator != null && _scrobblingHelper != null &&
                        !string.IsNullOrEmpty(deviceId) && song != null)
                    {
                        _logger.LogInformation("🎵 Starting scrobble task for external track");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var track = _scrobblingHelper.CreateScrobbleTrackFromExternal(
                                    title: song.Title,
                                    artist: song.Artist,
                                    album: song.Album,
                                    albumArtist: song.AlbumArtist,
                                    durationSeconds: song.Duration
                                );

                                if (track != null)
                                {
                                    await _scrobblingOrchestrator.OnPlaybackStartAsync(deviceId, track);
                                }
                                else
                                {
                                    _logger.LogWarning("Failed to create scrobble track from metadata");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to scrobble external track playback start");
                            }
                        });
                    }

                    return NoContent();
                }

                // Proactively fetch lyrics in background for local tracks
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await PrefetchLyricsForTrackAsync(itemId, isExternal: false, null, null);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to prefetch lyrics for local track {ItemId}", itemId);
                    }
                });
            }

            // For local tracks, forward playback start to Jellyfin FIRST
            _logger.LogDebug("Forwarding playback start to Jellyfin...");

            // Fetch full item details to include in playback report
            try
            {
                var (itemResult, itemStatus) =
                    await _proxyService.GetJsonAsync($"Items/{itemId}", null, Request.Headers);
                if (itemResult != null && itemStatus == 200)
                {
                    var item = itemResult.RootElement;

                    // Extract track name from item details for logging
                    string? trackName = null;
                    if (item.TryGetProperty("Name", out var nameElement))
                    {
                        trackName = nameElement.GetString();
                    }

                    _logger.LogInformation("🎵 Local track playback started: {Name} (ID: {ItemId})",
                        trackName ?? "Unknown", itemId);

                    // Build playback start info - Jellyfin will fetch item details itself
                    var playbackStart = new
                    {
                        ItemId = itemId,
                        PositionTicks = positionTicks ?? 0,
                        // Let Jellyfin fetch the item details - don't include NowPlayingItem
                    };

                    var playbackJson = JsonSerializer.Serialize(playbackStart);
                    _logger.LogInformation("📤 Sending playback start: {Json}", playbackJson);

                    var (result, statusCode) =
                        await _proxyService.PostJsonAsync("Sessions/Playing", playbackJson, Request.Headers);

                    if (statusCode == 204 || statusCode == 200)
                    {
                        _logger.LogDebug("✓ Playback start forwarded to Jellyfin ({StatusCode})", statusCode);

                        // Scrobble local track playback start (only if enabled)
                        if (_scrobblingSettings.LocalTracksEnabled && _scrobblingOrchestrator != null &&
                            _scrobblingHelper != null && !string.IsNullOrEmpty(deviceId) &&
                            !string.IsNullOrEmpty(itemId))
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var track = await _scrobblingHelper.GetScrobbleTrackFromItemIdAsync(itemId,
                                        Request.Headers);
                                    if (track != null)
                                    {
                                        await _scrobblingOrchestrator.OnPlaybackStartAsync(deviceId, track);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to scrobble local track playback start");
                                }
                            });
                        }

                        // NOW ensure session exists with capabilities (after playback is reported)
                        if (!string.IsNullOrEmpty(deviceId))
                        {
                            var sessionCreated = await _sessionManager.EnsureSessionAsync(deviceId, client ?? "Unknown",
                                device ?? "Unknown", version ?? "1.0", Request.Headers);
                            if (sessionCreated)
                            {
                                _logger.LogDebug(
                                    "✓ SESSION: Session ensured for device {DeviceId} after playback start", deviceId);
                            }
                            else
                            {
                                _logger.LogError("⚠️ SESSION: Failed to ensure session for device {DeviceId}",
                                    deviceId);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("⚠️ SESSION: No device ID found in headers for playback start");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("⚠️  Playback start returned status {StatusCode}", statusCode);
                    }
                }
                else
                {
                    _logger.LogWarning("⚠️  Could not fetch item details ({StatusCode}), sending basic playback start",
                        itemStatus);
                    // Fall back to basic playback start
                    var (result, statusCode) =
                        await _proxyService.PostJsonAsync("Sessions/Playing", body, Request.Headers);
                    if (statusCode == 204 || statusCode == 200)
                    {
                        _logger.LogDebug("✓ Basic playback start forwarded to Jellyfin ({StatusCode})", statusCode);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send playback start, trying basic");
                // Fall back to basic playback start
                var (result, statusCode) = await _proxyService.PostJsonAsync("Sessions/Playing", body, Request.Headers);
                if (statusCode == 204 || statusCode == 200)
                {
                    _logger.LogInformation("✓ Basic playback start forwarded to Jellyfin ({StatusCode})", statusCode);
                }
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to report playback start");
            return NoContent(); // Return success anyway to not break playback
        }
    }

    /// <summary>
    /// Reports playback progress. Handles both local and external tracks.
    /// </summary>
    [HttpPost("Sessions/Playing/Progress")]
    public async Task<IActionResult> ReportPlaybackProgress()
    {
        try
        {
            Request.EnableBuffering();
            string body;
            using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8,
                       detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
            {
                body = await reader.ReadToEndAsync();
            }

            Request.Body.Position = 0;

            // Update session activity (local tracks only)
            var (deviceId, _, _, _) = ExtractDeviceInfo(Request.Headers);

            // Parse the body to check if it's an external track
            var doc = JsonDocument.Parse(body);
            string? itemId = null;
            long? positionTicks = null;

            if (doc.RootElement.TryGetProperty("ItemId", out var itemIdProp))
            {
                itemId = itemIdProp.GetString();
            }

            if (doc.RootElement.TryGetProperty("PositionTicks", out var posProp))
            {
                positionTicks = posProp.GetInt64();
            }

            // Only update session for local tracks
            if (!string.IsNullOrEmpty(deviceId) && !string.IsNullOrEmpty(itemId))
            {
                var (isExt, _, _) = _localLibraryService.ParseSongId(itemId);
                if (!isExt)
                {
                    _sessionManager.UpdateActivity(deviceId);
                    _sessionManager.UpdatePlayingItem(deviceId, itemId, positionTicks);
                }

                // Scrobble progress check (both local and external)
                if (_scrobblingOrchestrator != null && _scrobblingHelper != null && positionTicks.HasValue)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

                            // Skip local tracks if local scrobbling is disabled
                            if (!isExternal && !_scrobblingSettings.LocalTracksEnabled)
                            {
                                return;
                            }

                            ScrobbleTrack? track = null;

                            if (isExternal)
                            {
                                // For external tracks, fetch metadata from provider
                                var song = await _metadataService.GetSongAsync(provider!, externalId!);
                                if (song != null)
                                {
                                    track = _scrobblingHelper.CreateScrobbleTrackFromExternal(
                                        title: song.Title,
                                        artist: song.Artist,
                                        album: song.Album,
                                        albumArtist: song.AlbumArtist,
                                        durationSeconds: song.Duration
                                    );
                                }
                            }
                            else
                            {
                                // For local tracks, fetch from Jellyfin
                                track = await _scrobblingHelper.GetScrobbleTrackFromItemIdAsync(itemId,
                                    Request.Headers);
                            }

                            if (track != null)
                            {
                                var positionSeconds = (int)(positionTicks.Value / TimeSpan.TicksPerSecond);
                                await _scrobblingOrchestrator.OnPlaybackProgressAsync(deviceId, track.Artist,
                                    track.Title, positionSeconds);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to scrobble playback progress");
                        }
                    });
                }
            }

            if (!string.IsNullOrEmpty(itemId))
            {
                var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

                if (isExternal)
                {
                    // For external tracks, report progress with ghost UUID to Jellyfin
                    var ghostUuid = GenerateUuidFromString(itemId);

                    // Build progress report with ghost UUID
                    var progressReport = new
                    {
                        ItemId = ghostUuid,
                        PositionTicks = positionTicks ?? 0,
                        IsPaused = false,
                        IsMuted = false,
                        CanSeek = true,
                        PlayMethod = "DirectPlay"
                    };

                    var progressJson = JsonSerializer.Serialize(progressReport);

                    // Forward to Jellyfin with ghost UUID
                    var (progressResult, progressStatusCode) =
                        await _proxyService.PostJsonAsync("Sessions/Playing/Progress", progressJson, Request.Headers);

                    // Log progress occasionally for debugging (every ~30 seconds)
                    if (positionTicks.HasValue)
                    {
                        var position = TimeSpan.FromTicks(positionTicks.Value);
                        if (position.Seconds % 30 == 0 && position.Milliseconds < 500)
                        {
                            _logger.LogDebug(
                                "▶️ External track progress: {Position:mm\\:ss} ({Provider}/{ExternalId}) - Status: {StatusCode}",
                                position, provider, externalId, progressStatusCode);
                        }
                    }

                    return NoContent();
                }

                // Log progress for local tracks (only every ~10 seconds to avoid spam)
                if (positionTicks.HasValue)
                {
                    var position = TimeSpan.FromTicks(positionTicks.Value);
                    // Only log at 10-second intervals
                    if (position.Seconds % 10 == 0 && position.Milliseconds < 500)
                    {
                        _logger.LogDebug("▶️ Progress: {Position:mm\\:ss} for item {ItemId}", position, itemId);
                    }
                }
            }

            // For local tracks, forward to Jellyfin
            _logger.LogDebug("📤 Sending playback progress body: {Body}", body);

            var (result, statusCode) =
                await _proxyService.PostJsonAsync("Sessions/Playing/Progress", body, Request.Headers);

            if (statusCode != 204 && statusCode != 200)
            {
                _logger.LogWarning("⚠️ Progress report returned {StatusCode} for item {ItemId}", statusCode, itemId);
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to report playback progress");
            return NoContent();
        }
    }

    /// <summary>
    /// Reports playback stopped. Handles both local and external tracks.
    /// </summary>
    [HttpPost("Sessions/Playing/Stopped")]
    public async Task<IActionResult> ReportPlaybackStopped()
    {
        try
        {
            Request.EnableBuffering();
            string body;
            using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8,
                       detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
            {
                body = await reader.ReadToEndAsync();
            }

            Request.Body.Position = 0;

            _logger.LogInformation("⏹️  Playback STOPPED reported");
            _logger.LogDebug("📤 Sending playback stop body: {Body}", body);

            // Parse the body to check if it's an external track
            var doc = JsonDocument.Parse(body);
            string? itemId = null;
            string? itemName = null;
            long? positionTicks = null;
            string? deviceId = null;

            if (doc.RootElement.TryGetProperty("ItemId", out var itemIdProp))
            {
                itemId = itemIdProp.GetString();
            }

            if (doc.RootElement.TryGetProperty("ItemName", out var itemNameProp))
            {
                itemName = itemNameProp.GetString();
            }

            if (doc.RootElement.TryGetProperty("PositionTicks", out var posProp))
            {
                positionTicks = posProp.GetInt64();
            }

            // Try to get device ID from headers for session management
            if (Request.Headers.TryGetValue("X-Emby-Device-Id", out var deviceIdHeader))
            {
                deviceId = deviceIdHeader.FirstOrDefault();
            }

            if (!string.IsNullOrEmpty(itemId))
            {
                var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

                if (isExternal)
                {
                    var position = positionTicks.HasValue
                        ? TimeSpan.FromTicks(positionTicks.Value).ToString(@"mm\:ss")
                        : "unknown";

                    // Try to get track metadata from provider if not in stop event
                    if (string.IsNullOrEmpty(itemName))
                    {
                        try
                        {
                            var song = await _metadataService.GetSongAsync(provider!, externalId!);
                            if (song != null)
                            {
                                itemName = $"{song.Artist} - {song.Title}";
                                // Update position with actual track duration if available
                                if (positionTicks.HasValue && song.Duration > 0)
                                {
                                    var actualPosition = TimeSpan.FromTicks(positionTicks.Value);
                                    var duration = TimeSpan.FromSeconds((double)song.Duration);
                                    position = $"{actualPosition:mm\\:ss}/{duration:mm\\:ss}";
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Could not fetch track name for external track on stop");
                        }
                    }

                    _logger.LogInformation(
                        "🎵 External track playback stopped: {Name} at {Position} ({Provider}/{ExternalId})",
                        itemName ?? "Unknown", position, provider, externalId);

                    // Report stop to Jellyfin with ghost UUID
                    var ghostUuid = GenerateUuidFromString(itemId);

                    var externalStopInfo = new
                    {
                        ItemId = ghostUuid,
                        PositionTicks = positionTicks ?? 0
                    };

                    var stopJson = JsonSerializer.Serialize(externalStopInfo);
                    _logger.LogDebug("📤 Sending ghost playback stop for external track: {Json}", stopJson);

                    var (stopResult, stopStatusCode) =
                        await _proxyService.PostJsonAsync("Sessions/Playing/Stopped", stopJson, Request.Headers);

                    if (stopStatusCode == 204 || stopStatusCode == 200)
                    {
                        _logger.LogDebug("✓ Ghost playback stop forwarded to Jellyfin ({StatusCode})", stopStatusCode);
                    }

                    // Scrobble external track playback stop
                    if (_scrobblingOrchestrator != null && _scrobblingHelper != null &&
                        !string.IsNullOrEmpty(deviceId) && positionTicks.HasValue)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // Fetch full metadata from the provider for scrobbling
                                var song = await _metadataService.GetSongAsync(provider!, externalId!);

                                if (song != null)
                                {
                                    var track = _scrobblingHelper.CreateScrobbleTrackFromExternal(
                                        title: song.Title,
                                        artist: song.Artist,
                                        album: song.Album,
                                        albumArtist: song.AlbumArtist,
                                        durationSeconds: song.Duration
                                    );

                                    if (track != null)
                                    {
                                        var positionSeconds = (int)(positionTicks.Value / TimeSpan.TicksPerSecond);
                                        await _scrobblingOrchestrator.OnPlaybackStopAsync(deviceId, track.Artist,
                                            track.Title, positionSeconds);
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning(
                                        "Could not fetch metadata for external track scrobbling on stop: {Provider}/{ExternalId}",
                                        provider, externalId);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to scrobble external track playback stop");
                            }
                        });
                    }

                    return NoContent();
                }

                // For local tracks, fetch item details to get track name
                string? trackName = itemName;
                if (string.IsNullOrEmpty(trackName))
                {
                    try
                    {
                        var (itemResult, itemStatus) =
                            await _proxyService.GetJsonAsync($"Items/{itemId}", null, Request.Headers);
                        if (itemResult != null && itemStatus == 200)
                        {
                            var item = itemResult.RootElement;
                            if (item.TryGetProperty("Name", out var nameElement))
                            {
                                trackName = nameElement.GetString();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not fetch track name for local track on stop");
                    }
                }

                _logger.LogInformation("🎵 Local track playback stopped: {Name} (ID: {ItemId})",
                    trackName ?? "Unknown", itemId);

                // Scrobble local track playback stop (only if enabled)
                if (_scrobblingSettings.LocalTracksEnabled && _scrobblingOrchestrator != null &&
                    _scrobblingHelper != null && !string.IsNullOrEmpty(deviceId) && !string.IsNullOrEmpty(itemId) &&
                    positionTicks.HasValue)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var track = await _scrobblingHelper.GetScrobbleTrackFromItemIdAsync(itemId,
                                Request.Headers);
                            if (track != null)
                            {
                                var positionSeconds = (int)(positionTicks.Value / TimeSpan.TicksPerSecond);
                                await _scrobblingOrchestrator.OnPlaybackStopAsync(deviceId, track.Artist, track.Title,
                                    positionSeconds);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to scrobble local track playback stop");
                        }
                    });
                }
            }

            // For local tracks, forward to Jellyfin
            _logger.LogDebug("Forwarding playback stop to Jellyfin...");

            // Log the body being sent for debugging
            _logger.LogDebug("📤 Original playback stop body: {Body}", body);

            // Parse and fix the body - ensure IsPaused is false for a proper stop
            var stopDoc = JsonDocument.Parse(body);
            var stopInfo = new Dictionary<string, object?>();

            foreach (var prop in stopDoc.RootElement.EnumerateObject())
            {
                if (prop.Name == "IsPaused")
                {
                    // Force IsPaused to false for a proper stop
                    stopInfo[prop.Name] = false;
                }
                else if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    stopInfo[prop.Name] = prop.Value.GetString();
                }
                else if (prop.Value.ValueKind == JsonValueKind.Number)
                {
                    stopInfo[prop.Name] = prop.Value.GetInt64();
                }
                else if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                {
                    stopInfo[prop.Name] = prop.Value.GetBoolean();
                }
                else
                {
                    stopInfo[prop.Name] = prop.Value.GetRawText();
                }
            }

            // Ensure required fields are present
            if (!stopInfo.ContainsKey("ItemId") && !string.IsNullOrEmpty(itemId))
            {
                stopInfo["ItemId"] = itemId;
            }

            if (!stopInfo.ContainsKey("PositionTicks") && positionTicks.HasValue)
            {
                stopInfo["PositionTicks"] = positionTicks.Value;
            }

            body = JsonSerializer.Serialize(stopInfo);
            _logger.LogInformation("📤 Sending playback stop body (IsPaused=false): {Body}", body);

            var (result, statusCode) =
                await _proxyService.PostJsonAsync("Sessions/Playing/Stopped", body, Request.Headers);

            if (statusCode == 204 || statusCode == 200)
            {
                _logger.LogDebug("✓ Playback stop forwarded to Jellyfin ({StatusCode})", statusCode);
            }
            else if (statusCode == 401)
            {
                _logger.LogWarning("Playback stop returned 401 (token expired)");
            }
            else
            {
                _logger.LogWarning("Playback stop forward failed with status {StatusCode}", statusCode);
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to report playback stopped");
            return NoContent();
        }
    }

    /// <summary>
    /// Pings a playback session to keep it alive.
    /// </summary>
    [HttpPost("Sessions/Playing/Ping")]
    public async Task<IActionResult> PingPlaybackSession([FromQuery] string playSessionId)
    {
        try
        {
            _logger.LogDebug("Playback session ping: {SessionId}", playSessionId);

            // Forward to Jellyfin
            var endpoint = $"Sessions/Playing/Ping?playSessionId={Uri.EscapeDataString(playSessionId)}";
            var (result, statusCode) = await _proxyService.PostJsonAsync(endpoint, "{}", Request.Headers);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ping playback session");
            return NoContent();
        }
    }

    /// <summary>
    /// Catch-all for any other session-related requests.
    /// <summary>
    /// Catch-all proxy for any other session-related endpoints we haven't explicitly implemented.
    /// This ensures all session management calls get proxied to Jellyfin.
    /// Examples: GET /Sessions, POST /Sessions/Logout, etc.
    /// </summary>
    [HttpGet("Sessions")]
    [HttpPost("Sessions")]
    [HttpGet("Sessions/{**path}")]
    [HttpPost("Sessions/{**path}")]
    [HttpPut("Sessions/{**path}")]
    [HttpDelete("Sessions/{**path}")]
    public async Task<IActionResult> ProxySessionRequest(string? path = null)
    {
        try
        {
            var method = Request.Method;
            var queryString = Request.QueryString.HasValue ? Request.QueryString.Value : "";
            var endpoint = string.IsNullOrEmpty(path) ? $"Sessions{queryString}" : $"Sessions/{path}{queryString}";

            _logger.LogDebug("🔄 Proxying session request: {Method} {Endpoint}", method, endpoint);
            _logger.LogDebug("Session proxy headers: {Headers}",
                string.Join(", ", Request.Headers.Where(h => h.Key.Contains("Auth", StringComparison.OrdinalIgnoreCase))
                    .Select(h => $"{h.Key}={h.Value}")));

            // Read body if present
            string body = "{}";
            if ((method == "POST" || method == "PUT") && Request.ContentLength > 0)
            {
                Request.EnableBuffering();
                using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8,
                           detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
                {
                    body = await reader.ReadToEndAsync();
                }

                Request.Body.Position = 0;
                _logger.LogDebug("Session proxy body: {Body}", body);
            }

            // Forward to Jellyfin
            var (result, statusCode) = method switch
            {
                "GET" => await _proxyService.GetJsonAsync(endpoint, null, Request.Headers),
                "POST" => await _proxyService.PostJsonAsync(endpoint, body, Request.Headers),
                "PUT" => await _proxyService.PostJsonAsync(endpoint, body, Request.Headers), // Use POST for PUT
                "DELETE" => await _proxyService.PostJsonAsync(endpoint, body, Request.Headers), // Use POST for DELETE
                _ => (null, 405)
            };

            if (result != null)
            {
                _logger.LogDebug("✓ Session request proxied successfully ({StatusCode})", statusCode);
                return new JsonResult(result.RootElement.Clone());
            }

            _logger.LogDebug("✓ Session request proxied ({StatusCode}, no body)", statusCode);
            return StatusCode(statusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy session request: {Path}", path);
            return StatusCode(500);
        }
    }

    #endregion // Session Management

    #endregion // Playback Session Reporting
}