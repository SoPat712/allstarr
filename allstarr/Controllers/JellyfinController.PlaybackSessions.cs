using System.Collections.Concurrent;
using System.Text.Json;
using System.Globalization;
using allstarr.Models.Scrobbling;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private static readonly TimeSpan InferredStopDedupeWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PlaybackSignalDedupeWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PlaybackSignalRetentionWindow = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, DateTime> RecentPlaybackSignals = new();

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
            var maskedQueryString = MaskSensitiveQueryString(queryString);

            _logger.LogDebug("📡 Session capabilities reported - Method: {Method}, QueryLength: {QueryLength}",
                method, maskedQueryString.Length);
            _logger.LogDebug("Capabilities header keys: {HeaderKeys}",
                string.Join(", ", Request.Headers.Keys.Where(k =>
                    k.Contains("Auth", StringComparison.OrdinalIgnoreCase) ||
                    k.Contains("Device", StringComparison.OrdinalIgnoreCase) ||
                    k.Contains("Client", StringComparison.OrdinalIgnoreCase))));

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
                _logger.LogDebug("Capabilities body length: {BodyLength} bytes", body.Length);
            }

            var (_, statusCode) = await _proxyService.PostJsonAsync(endpoint, body, Request.Headers);

            if (statusCode == 204 || statusCode == 200)
            {
                _logger.LogDebug("✓ Session capabilities forwarded to Jellyfin ({StatusCode})", statusCode);
                return NoContent();
            }

            if (statusCode == 401)
            {
                _logger.LogWarning("⚠ Jellyfin returned 401 for capabilities (token expired)");
                return Unauthorized();
            }

            if (statusCode == 403)
            {
                _logger.LogWarning("⚠ Jellyfin returned 403 for capabilities");
                return Forbid();
            }

            _logger.LogWarning("⚠ Jellyfin returned {StatusCode} for capabilities", statusCode);
            return StatusCode(statusCode);
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
            string? playSessionId = null;

            itemId = ParsePlaybackItemId(doc.RootElement);

            if (doc.RootElement.TryGetProperty("ItemName", out var itemNameProp))
            {
                itemName = itemNameProp.GetString();
            }

            positionTicks = ParsePlaybackPositionTicks(doc.RootElement);
            playSessionId = ParsePlaybackSessionId(doc.RootElement);

            // Track the playing item for scrobbling on session cleanup (local tracks only)
            var (deviceId, client, device, version) = ExtractDeviceInfo(Request.Headers);
            deviceId = ResolveDeviceId(deviceId, doc.RootElement);

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
                    var sessionReady = false;
                    if (!string.IsNullOrEmpty(deviceId))
                    {
                        sessionReady = _sessionManager.HasSession(deviceId);
                        if (!sessionReady)
                        {
                            var ensured = await _sessionManager.EnsureSessionAsync(
                                deviceId,
                                client ?? "Unknown",
                                device ?? "Unknown",
                                version ?? "1.0",
                                Request.Headers);

                            if (!ensured)
                            {
                                _logger.LogWarning(
                                    "⚠️ SESSION: Could not ensure session from external playback start for device {DeviceId}",
                                    deviceId);
                            }

                            sessionReady = ensured || _sessionManager.HasSession(deviceId);
                        }

                        if (sessionReady)
                        {
                            var (previousItemId, previousPositionTicks) = _sessionManager.GetLastPlayingState(deviceId);
                            var inferredStop = !string.IsNullOrWhiteSpace(previousItemId) &&
                                               !string.Equals(previousItemId, itemId, StringComparison.Ordinal);
                            if (inferredStop && !string.IsNullOrWhiteSpace(previousItemId))
                            {
                                await HandleInferredStopOnProgressTransitionAsync(deviceId, previousItemId, previousPositionTicks);
                            }
                        }
                    }

                    if (ShouldSuppressPlaybackSignal("start", deviceId, itemId, playSessionId))
                    {
                        _logger.LogDebug(
                            "Skipping duplicate external playback start signal for {ItemId} on {DeviceId} (PlaySessionId: {PlaySessionId})",
                            itemId,
                            deviceId ?? "unknown",
                            playSessionId ?? "none");

                        if (sessionReady)
                        {
                            _sessionManager.UpdateActivity(deviceId!);
                            _sessionManager.UpdatePlayingItem(deviceId!, itemId, positionTicks);
                        }

                        return NoContent();
                    }

                    // Fetch metadata early so we can log the correct track name
                    var song = await _metadataService.GetSongAsync(provider!, externalId!);
                    var trackName = song != null ? $"{song.Artist} - {song.Title}" : "Unknown";

                    _logger.LogInformation("▶️ External track playback started: {TrackName} ({Provider}/{ExternalId})",
                        trackName, provider, externalId);

                    // Proactively fetch lyrics in background for external tracks
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await PrefetchLyricsForTrackAsync(itemId, isExternal: true, provider, externalId, CancellationToken.None);
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
                    _logger.LogDebug(
                        "Checking scrobbling: orchestrator={HasOrchestrator}, helper={HasHelper}, deviceId={DeviceId}",
                        _scrobblingOrchestrator != null, _scrobblingHelper != null, deviceId ?? "null");

                    if (_scrobblingOrchestrator != null && _scrobblingHelper != null &&
                        !string.IsNullOrEmpty(deviceId) && song != null)
                    {
                        _logger.LogDebug("Starting scrobble task for external track");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var track = _scrobblingHelper.CreateScrobbleTrackFromExternal(
                                    title: song.Title,
                                    artist: song.Artist,
                                    album: song.Album,
                                    albumArtist: song.AlbumArtist,
                                    durationSeconds: song.Duration,
                                    startPositionSeconds: ToPlaybackPositionSeconds(positionTicks)
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

                    if (sessionReady)
                    {
                        _sessionManager.UpdateActivity(deviceId!);
                        _sessionManager.UpdatePlayingItem(deviceId!, itemId, positionTicks);
                    }

                    return NoContent();
                }

                // Proactively fetch lyrics in background for local tracks
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await PrefetchLyricsForTrackAsync(itemId, isExternal: false, null, null, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to prefetch lyrics for local track {ItemId}", itemId);
                    }
                });
            }

            if (!string.IsNullOrEmpty(itemId) &&
                ShouldSuppressPlaybackSignal("start", deviceId, itemId, playSessionId))
            {
                _logger.LogDebug(
                    "Skipping duplicate local playback start signal for {ItemId} on {DeviceId} (PlaySessionId: {PlaySessionId})",
                    itemId,
                    deviceId ?? "unknown",
                    playSessionId ?? "none");

                if (!string.IsNullOrEmpty(deviceId))
                {
                    _sessionManager.UpdateActivity(deviceId);
                    _sessionManager.UpdatePlayingItem(deviceId, itemId, positionTicks);
                }

                return NoContent();
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
                    _logger.LogDebug("📤 Sending playback start: {Json}", playbackJson);

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
                        _logger.LogInformation("🎵 Local track playback started: {Name} (ID: {ItemId})",
                            itemName ?? "Unknown", itemId ?? "unknown");
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
                    _logger.LogDebug("✓ Basic playback start forwarded to Jellyfin ({StatusCode})", statusCode);
                    _logger.LogInformation("🎵 Local track playback started: {Name} (ID: {ItemId})",
                        itemName ?? "Unknown", itemId ?? "unknown");
                }
            }

            // Ensure session exists for local playback regardless of start payload path taken.
            if (!string.IsNullOrEmpty(deviceId) && !string.IsNullOrEmpty(itemId))
            {
                var (isExt, _, _) = _localLibraryService.ParseSongId(itemId);
                if (!isExt)
                {
                    var sessionCreated = await _sessionManager.EnsureSessionAsync(deviceId, client ?? "Unknown",
                        device ?? "Unknown", version ?? "1.0", Request.Headers);
                    if (sessionCreated)
                    {
                        _sessionManager.UpdateActivity(deviceId);
                        _sessionManager.UpdatePlayingItem(deviceId, itemId, positionTicks);
                        _logger.LogDebug("✓ SESSION: Session ensured for device {DeviceId} after playback start", deviceId);
                    }
                    else
                    {
                        _logger.LogError("⚠️ SESSION: Failed to ensure session for device {DeviceId}", deviceId);
                    }
                }
            }
            else if (string.IsNullOrEmpty(deviceId))
            {
                _logger.LogWarning("⚠️ SESSION: No device ID found in headers for playback start");
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
            var (deviceId, client, device, version) = ExtractDeviceInfo(Request.Headers);

            // Parse the body to check if it's an external track
            var doc = JsonDocument.Parse(body);
            string? itemId = null;
            long? positionTicks = null;
            string? playSessionId = null;

            itemId = ParsePlaybackItemId(doc.RootElement);
            positionTicks = ParsePlaybackPositionTicks(doc.RootElement);
            playSessionId = ParsePlaybackSessionId(doc.RootElement);

            deviceId = ResolveDeviceId(deviceId, doc.RootElement);

            if (string.IsNullOrWhiteSpace(itemId))
            {
                _logger.LogWarning(
                    "⚠️ Playback progress missing item id after parsing. Payload keys: {Keys}",
                    string.Join(", ", doc.RootElement.EnumerateObject().Select(p => p.Name)));
            }

            // Scrobble progress check (both local and external)
            if (!string.IsNullOrEmpty(deviceId) && !string.IsNullOrEmpty(itemId))
            {
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
                                        durationSeconds: song.Duration,
                                        startPositionSeconds: ToPlaybackPositionSeconds(positionTicks)
                                    );
                                }
                                else
                                {
                                    _logger.LogDebug("Could not fetch metadata for external track progress: {Provider}/{ExternalId}", 
                                        provider, externalId);
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
                                await _scrobblingOrchestrator.OnPlaybackProgressAsync(deviceId, track, positionSeconds);
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
                    if (!string.IsNullOrEmpty(deviceId))
                    {
                        var sessionReady = _sessionManager.HasSession(deviceId);
                        if (!sessionReady)
                        {
                            var ensured = await _sessionManager.EnsureSessionAsync(
                                deviceId,
                                client ?? "Unknown",
                                device ?? "Unknown",
                                version ?? "1.0",
                                Request.Headers);

                            if (!ensured)
                            {
                                _logger.LogWarning(
                                    "⚠️ SESSION: Could not ensure session from external progress for device {DeviceId}",
                                    deviceId);
                            }

                            sessionReady = ensured || _sessionManager.HasSession(deviceId);
                        }

                        var (previousItemId, previousPositionTicks) = _sessionManager.GetLastPlayingState(deviceId);
                        var inferredStop = sessionReady &&
                                           !string.IsNullOrWhiteSpace(previousItemId) &&
                                           !string.Equals(previousItemId, itemId, StringComparison.Ordinal);
                        var inferredStart = sessionReady &&
                                            !string.IsNullOrWhiteSpace(itemId) &&
                                            !string.Equals(previousItemId, itemId, StringComparison.Ordinal);

                        if (sessionReady && inferredStop && !string.IsNullOrWhiteSpace(previousItemId))
                        {
                            await HandleInferredStopOnProgressTransitionAsync(deviceId, previousItemId, previousPositionTicks);
                        }

                        if (sessionReady)
                        {
                            _sessionManager.UpdateActivity(deviceId);
                            _sessionManager.UpdatePlayingItem(deviceId, itemId, positionTicks);
                        }

                        if (inferredStart &&
                            !ShouldSuppressPlaybackSignal("start", deviceId, itemId, playSessionId))
                        {
                            var song = await _metadataService.GetSongAsync(provider!, externalId!);
                            var externalTrackName = song != null ? $"{song.Artist} - {song.Title}" : "Unknown";
                            _logger.LogInformation(
                                "▶️ External track playback started (inferred from progress): {TrackName} ({Provider}/{ExternalId})",
                                externalTrackName,
                                provider,
                                externalId);

                            var inferredStartGhostUuid = GenerateUuidFromString(itemId);
                            var inferredExternalStartPayload = JsonSerializer.Serialize(new
                            {
                                ItemId = inferredStartGhostUuid,
                                PositionTicks = positionTicks ?? 0,
                                CanSeek = true,
                                IsPaused = false,
                                IsMuted = false,
                                PlayMethod = "DirectPlay"
                            });

                            var (_, inferredStartStatusCode) = await _proxyService.PostJsonAsync(
                                "Sessions/Playing",
                                inferredExternalStartPayload,
                                Request.Headers);

                            if (inferredStartStatusCode == 200 || inferredStartStatusCode == 204)
                            {
                                _logger.LogDebug("✓ Inferred external playback start forwarded to Jellyfin ({StatusCode})",
                                    inferredStartStatusCode);
                            }

                            if (_scrobblingOrchestrator != null && _scrobblingHelper != null &&
                                !string.IsNullOrEmpty(deviceId) && song != null)
                            {
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        var track = _scrobblingHelper.CreateScrobbleTrackFromExternal(
                                            title: song.Title,
                                            artist: song.Artist,
                                            album: song.Album,
                                            albumArtist: song.AlbumArtist,
                                            durationSeconds: song.Duration,
                                            startPositionSeconds: ToPlaybackPositionSeconds(positionTicks));

                                        if (track != null)
                                        {
                                            await _scrobblingOrchestrator.OnPlaybackStartAsync(deviceId, track);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "Failed to scrobble inferred external track playback start");
                                    }
                                });
                            }
                        }
                        else if (inferredStart)
                        {
                            _logger.LogDebug(
                                "Skipping duplicate inferred external playback start signal for {ItemId} on {DeviceId} (PlaySessionId: {PlaySessionId})",
                                itemId,
                                deviceId,
                                playSessionId ?? "none");
                        }
                        else if (!sessionReady)
                        {
                            _logger.LogDebug(
                                "Skipping inferred external playback start/stop from progress for {DeviceId} because session is unavailable",
                                deviceId);
                        }
                    }

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

                // Some clients (e.g. mobile) may skip /Sessions/Playing and only send Progress.
                // Infer playback start from first progress event or track-change progress event.
                if (!string.IsNullOrEmpty(deviceId))
                {
                    var sessionReady = _sessionManager.HasSession(deviceId);
                    if (!sessionReady)
                    {
                        var ensured = await _sessionManager.EnsureSessionAsync(
                            deviceId,
                            client ?? "Unknown",
                            device ?? "Unknown",
                            version ?? "1.0",
                            Request.Headers);

                        if (!ensured)
                        {
                            _logger.LogWarning(
                                "⚠️ SESSION: Could not ensure session from progress for device {DeviceId}",
                                deviceId);
                        }

                        sessionReady = ensured || _sessionManager.HasSession(deviceId);
                    }

                    var (previousItemId, previousPositionTicks) = _sessionManager.GetLastPlayingState(deviceId);
                    var inferredStop = sessionReady &&
                                       !string.IsNullOrWhiteSpace(previousItemId) &&
                                       !string.Equals(previousItemId, itemId, StringComparison.Ordinal);
                    var inferredStart = sessionReady &&
                                        !string.IsNullOrWhiteSpace(itemId) &&
                                        !string.Equals(previousItemId, itemId, StringComparison.Ordinal);

                    if (sessionReady && inferredStop && !string.IsNullOrWhiteSpace(previousItemId))
                    {
                        await HandleInferredStopOnProgressTransitionAsync(deviceId, previousItemId, previousPositionTicks);
                    }

                    if (sessionReady)
                    {
                        _sessionManager.UpdateActivity(deviceId);
                        _sessionManager.UpdatePlayingItem(deviceId, itemId, positionTicks);
                    }

                    if (inferredStart &&
                        !ShouldSuppressPlaybackSignal("start", deviceId, itemId, playSessionId))
                    {
                        var trackName = await TryGetLocalTrackNameAsync(itemId);
                        _logger.LogInformation("🎵 Local track playback started (inferred from progress): {Name} (ID: {ItemId})",
                            trackName ?? "Unknown", itemId);

                        var inferredStartPayload = JsonSerializer.Serialize(new
                        {
                            ItemId = itemId,
                            PositionTicks = positionTicks ?? 0
                        });

                        var (_, inferredStartStatusCode) =
                            await _proxyService.PostJsonAsync("Sessions/Playing", inferredStartPayload, Request.Headers);

                        if (inferredStartStatusCode == 200 || inferredStartStatusCode == 204)
                        {
                            _logger.LogDebug("✓ Inferred playback start forwarded to Jellyfin ({StatusCode})", inferredStartStatusCode);
                        }
                        else
                        {
                            _logger.LogDebug("Inferred playback start returned {StatusCode}", inferredStartStatusCode);
                        }

                        // Scrobble local track playback start (only if enabled)
                        if (_scrobblingSettings.LocalTracksEnabled && _scrobblingOrchestrator != null &&
                            _scrobblingHelper != null)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var track = await _scrobblingHelper.GetScrobbleTrackFromItemIdAsync(itemId, Request.Headers);
                                    if (track != null)
                                    {
                                        await _scrobblingOrchestrator.OnPlaybackStartAsync(deviceId, track);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to scrobble inferred local track playback start");
                                }
                            });
                        }
                    }
                    else if (inferredStart)
                    {
                        _logger.LogDebug(
                            "Skipping duplicate inferred local playback start signal for {ItemId} on {DeviceId} (PlaySessionId: {PlaySessionId})",
                            itemId,
                            deviceId,
                            playSessionId ?? "none");
                    }
                    else if (!sessionReady)
                    {
                        _logger.LogDebug(
                            "Skipping inferred local playback start/stop from progress for {DeviceId} because session is unavailable",
                            deviceId);
                    }

                    // When local scrobbling is disabled, still trigger Jellyfin's user-data path
                    // shortly after the normal scrobble threshold so downstream plugins that listen
                    // to user-data events can process local listens even without a stop event.
                    await MaybeTriggerLocalPlayedSignalFromProgressAsync(doc.RootElement, deviceId, itemId, positionTicks);
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
            _logger.LogDebug("📤 Sending playback progress body ({BodyLength} bytes)", body.Length);

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

    private async Task<string?> TryGetLocalTrackNameAsync(string itemId)
    {
        try
        {
            var (itemResult, itemStatus) = await _proxyService.GetJsonAsync($"Items/{itemId}", null, Request.Headers);
            if (itemResult != null && itemStatus == 200 &&
                itemResult.RootElement.TryGetProperty("Name", out var nameElement))
            {
                return nameElement.GetString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch local track name for {ItemId}", itemId);
        }

        return null;
    }

    private async Task HandleInferredStopOnProgressTransitionAsync(
        string deviceId,
        string previousItemId,
        long? previousPositionTicks)
    {
        if (_sessionManager.WasRecentlyExplicitlyStopped(deviceId, previousItemId, InferredStopDedupeWindow))
        {
            _logger.LogDebug(
                "Skipping inferred stop for {ItemId} on {DeviceId} (explicit stop already recorded within {Window}s)",
                previousItemId,
                deviceId,
                InferredStopDedupeWindow.TotalSeconds);
            return;
        }

        if (ShouldSuppressPlaybackSignal("stop", deviceId, previousItemId, playSessionId: null))
        {
            _logger.LogDebug(
                "Skipping duplicate inferred playback stop signal for {ItemId} on {DeviceId}",
                previousItemId,
                deviceId);
            return;
        }

        var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(previousItemId);

        if (isExternal)
        {
            var song = await _metadataService.GetSongAsync(provider!, externalId!);
            var externalTrackName = song != null ? $"{song.Artist} - {song.Title}" : "Unknown";
            _logger.LogInformation(
                "🎵 External track playback stopped (inferred from progress): {TrackName} ({Provider}/{ExternalId})",
                externalTrackName,
                provider,
                externalId);

            if (_scrobblingOrchestrator != null && _scrobblingHelper != null &&
                !string.IsNullOrEmpty(deviceId) && previousPositionTicks.HasValue && song != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var track = _scrobblingHelper.CreateScrobbleTrackFromExternal(
                            title: song.Title,
                            artist: song.Artist,
                            album: song.Album,
                            albumArtist: song.AlbumArtist,
                            durationSeconds: song.Duration);

                        if (track != null)
                        {
                            var positionSeconds = (int)(previousPositionTicks.Value / TimeSpan.TicksPerSecond);
                            await _scrobblingOrchestrator.OnPlaybackStopAsync(deviceId, track.Artist, track.Title, positionSeconds);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to scrobble inferred external track playback stop");
                    }
                });
            }

            var ghostUuid = GenerateUuidFromString(previousItemId);
            var inferredExternalStopPayload = JsonSerializer.Serialize(new
            {
                ItemId = ghostUuid,
                PositionTicks = previousPositionTicks ?? 0,
                IsPaused = false
            });

            var (_, inferredExternalStopStatusCode) = await _proxyService.PostJsonAsync(
                "Sessions/Playing/Stopped",
                inferredExternalStopPayload,
                Request.Headers);

            if (inferredExternalStopStatusCode == 200 || inferredExternalStopStatusCode == 204)
            {
                _logger.LogDebug("✓ Inferred external playback stop forwarded to Jellyfin ({StatusCode})",
                    inferredExternalStopStatusCode);
            }

            return;
        }

        var previousTrackName = await TryGetLocalTrackNameAsync(previousItemId);
        _logger.LogInformation(
            "🎵 Local track playback stopped (inferred from progress): {Name} (ID: {ItemId})",
            previousTrackName ?? "Unknown",
            previousItemId);

        // Scrobble local track playback stop (only if enabled)
        if (_scrobblingSettings.LocalTracksEnabled && _scrobblingOrchestrator != null &&
            _scrobblingHelper != null && previousPositionTicks.HasValue)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var track = await _scrobblingHelper.GetScrobbleTrackFromItemIdAsync(previousItemId, Request.Headers);
                    if (track != null)
                    {
                        var positionSeconds = (int)(previousPositionTicks.Value / TimeSpan.TicksPerSecond);
                        await _scrobblingOrchestrator.OnPlaybackStopAsync(deviceId, track.Artist, track.Title, positionSeconds);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to scrobble inferred local track playback stop");
                }
            });
        }

        var inferredStopPayload = JsonSerializer.Serialize(new
        {
            ItemId = previousItemId,
            PositionTicks = previousPositionTicks ?? 0,
            IsPaused = false
        });

        var (_, inferredStopStatusCode) = await _proxyService.PostJsonAsync(
            "Sessions/Playing/Stopped",
            inferredStopPayload,
            Request.Headers);

        if (inferredStopStatusCode == 200 || inferredStopStatusCode == 204)
        {
            _logger.LogDebug("✓ Inferred playback stop forwarded to Jellyfin ({StatusCode})", inferredStopStatusCode);
        }
        else
        {
            _logger.LogDebug("Inferred playback stop returned {StatusCode}", inferredStopStatusCode);
        }
    }

    private async Task MaybeTriggerLocalPlayedSignalFromProgressAsync(
        JsonElement progressPayload,
        string? deviceId,
        string itemId,
        long? positionTicks)
    {
        if (!_scrobblingSettings.Enabled ||
            _scrobblingSettings.LocalTracksEnabled ||
            !_scrobblingSettings.SyntheticLocalPlayedSignalEnabled ||
            _scrobblingHelper == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(deviceId) || !positionTicks.HasValue)
        {
            return;
        }

        if (_sessionManager.HasSentLocalPlayedSignal(deviceId, itemId))
        {
            return;
        }

        var playedSeconds = (int)(positionTicks.Value / TimeSpan.TicksPerSecond);
        if (playedSeconds < 25)
        {
            return;
        }

        var track = await _scrobblingHelper.GetScrobbleTrackFromItemIdAsync(itemId, Request.Headers);
        if (track?.DurationSeconds is not int durationSeconds || durationSeconds < 30)
        {
            return;
        }

        var baseThresholdSeconds = Math.Min(durationSeconds / 2.0, 240.0);
        var triggerAtSeconds = (int)Math.Ceiling(baseThresholdSeconds + 10.0);
        if (playedSeconds < triggerAtSeconds)
        {
            return;
        }

        var userId = ResolvePlaybackUserId(progressPayload);
        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogDebug("Skipping local played signal for {ItemId} - no user id available", itemId);
            return;
        }

        var endpoint = $"UserPlayedItems/{Uri.EscapeDataString(itemId)}?userId={Uri.EscapeDataString(userId)}";
        var (_, statusCode) = await _proxyService.PostJsonAsync(endpoint, "{}", Request.Headers);

        if (statusCode == 404)
        {
            var legacyEndpoint = $"Users/{Uri.EscapeDataString(userId)}/PlayedItems/{Uri.EscapeDataString(itemId)}";
            (_, statusCode) = await _proxyService.PostJsonAsync(legacyEndpoint, "{}", Request.Headers);
        }

        if (statusCode == 200 || statusCode == 204)
        {
            _sessionManager.MarkLocalPlayedSignalSent(deviceId, itemId);
            _logger.LogInformation(
                "🎧 Local played signal sent via PlayedItems for {ItemId} at {Position}s (trigger={Trigger}s)",
                itemId,
                playedSeconds,
                triggerAtSeconds);
        }
        else
        {
            _logger.LogDebug(
                "Local played signal returned {StatusCode} for {ItemId} (position={Position}s, trigger={Trigger}s)",
                statusCode,
                itemId,
                playedSeconds,
                triggerAtSeconds);
        }
    }

    private string? ResolvePlaybackUserId(JsonElement progressPayload)
    {
        if (progressPayload.TryGetProperty("UserId", out var userIdElement) &&
            userIdElement.ValueKind == JsonValueKind.String)
        {
            var payloadUserId = userIdElement.GetString();
            if (!string.IsNullOrWhiteSpace(payloadUserId))
            {
                return payloadUserId;
            }
        }

        var queryUserId = Request.Query["userId"].ToString();
        if (!string.IsNullOrWhiteSpace(queryUserId))
        {
            return queryUserId;
        }

        return _settings.UserId;
    }

    private static int? ToPlaybackPositionSeconds(long? positionTicks)
    {
        if (!positionTicks.HasValue)
        {
            return null;
        }

        var seconds = positionTicks.Value / TimeSpan.TicksPerSecond;
        if (seconds <= 0)
        {
            return 0;
        }

        return seconds > int.MaxValue ? int.MaxValue : (int)seconds;
    }

    private string? ResolveDeviceId(string? parsedDeviceId, JsonElement? payload = null)
    {
        if (!string.IsNullOrWhiteSpace(parsedDeviceId))
        {
            return parsedDeviceId;
        }

        if (payload.HasValue &&
            payload.Value.TryGetProperty("DeviceId", out var payloadDeviceIdElement) &&
            payloadDeviceIdElement.ValueKind == JsonValueKind.String)
        {
            var payloadDeviceId = payloadDeviceIdElement.GetString();
            if (!string.IsNullOrWhiteSpace(payloadDeviceId))
            {
                return payloadDeviceId;
            }
        }

        if (Request.Headers.TryGetValue("X-Emby-Device-Id", out var headerDeviceId))
        {
            var deviceIdFromHeader = headerDeviceId.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(deviceIdFromHeader))
            {
                return deviceIdFromHeader;
            }
        }

        var queryDeviceId = Request.Query["DeviceId"].ToString();
        if (string.IsNullOrWhiteSpace(queryDeviceId))
        {
            queryDeviceId = Request.Query["deviceId"].ToString();
        }

        return string.IsNullOrWhiteSpace(queryDeviceId) ? parsedDeviceId : queryDeviceId;
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

                _logger.LogInformation("⏹️ Playback STOPPED reported");
            _logger.LogDebug("📤 Sending playback stop body ({BodyLength} bytes)", body.Length);

            // Parse the body to check if it's an external track
            var doc = JsonDocument.Parse(body);
            string? itemId = null;
            string? itemName = null;
            long? positionTicks = null;
            string? playSessionId = null;
            var (deviceId, _, _, _) = ExtractDeviceInfo(Request.Headers);

            itemId = ParsePlaybackItemId(doc.RootElement);

            if (doc.RootElement.TryGetProperty("ItemName", out var itemNameProp))
            {
                itemName = itemNameProp.GetString();
            }

            if (string.IsNullOrWhiteSpace(itemName))
            {
                itemName = ParsePlaybackItemName(doc.RootElement);
            }

            positionTicks = ParsePlaybackPositionTicks(doc.RootElement);
            playSessionId = ParsePlaybackSessionId(doc.RootElement);

            deviceId = ResolveDeviceId(deviceId, doc.RootElement);

            // Some clients send stop without ItemId. Recover from tracked session state when possible.
            if (string.IsNullOrWhiteSpace(itemId) && !string.IsNullOrWhiteSpace(deviceId))
            {
                var (trackedItemId, trackedPositionTicks) = _sessionManager.GetLastPlayingState(deviceId);
                if (!string.IsNullOrWhiteSpace(trackedItemId))
                {
                    itemId = trackedItemId;
                    if (!positionTicks.HasValue)
                    {
                        positionTicks = trackedPositionTicks;
                    }

                    _logger.LogInformation(
                        "⏹️ Playback stop missing ItemId - recovered from session state: {ItemId}",
                        itemId);
                }
            }

            if (!string.IsNullOrEmpty(itemId))
            {
                var (isExternal, provider, externalId) = _localLibraryService.ParseSongId(itemId);

                if (isExternal)
                {
                    if (ShouldSuppressPlaybackSignal("stop", deviceId, itemId, playSessionId))
                    {
                        _logger.LogDebug(
                            "Skipping duplicate external playback stop signal for {ItemId} on {DeviceId} (PlaySessionId: {PlaySessionId})",
                            itemId,
                            deviceId ?? "unknown",
                            playSessionId ?? "none");

                        if (!string.IsNullOrWhiteSpace(deviceId))
                        {
                            _sessionManager.MarkExplicitStop(deviceId, itemId);
                            _sessionManager.UpdatePlayingItem(deviceId, null, null);
                            _sessionManager.MarkSessionPotentiallyEnded(deviceId, TimeSpan.FromSeconds(30));
                        }

                        return NoContent();
                    }

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

                    if ((stopStatusCode == 200 || stopStatusCode == 204) && !string.IsNullOrWhiteSpace(deviceId))
                    {
                        _sessionManager.MarkExplicitStop(deviceId, itemId);
                        _sessionManager.UpdatePlayingItem(deviceId, null, null);
                        _sessionManager.MarkSessionPotentiallyEnded(deviceId, TimeSpan.FromSeconds(30));
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

                if (ShouldSuppressPlaybackSignal("stop", deviceId, itemId, playSessionId))
                {
                    _logger.LogDebug(
                        "Skipping duplicate local playback stop signal for {ItemId} on {DeviceId} (PlaySessionId: {PlaySessionId})",
                        itemId,
                        deviceId ?? "unknown",
                        playSessionId ?? "none");

                    if (!string.IsNullOrWhiteSpace(deviceId))
                    {
                        _sessionManager.MarkExplicitStop(deviceId, itemId);
                        _sessionManager.UpdatePlayingItem(deviceId, null, null);
                        _sessionManager.MarkSessionPotentiallyEnded(deviceId, TimeSpan.FromSeconds(30));
                    }

                    return NoContent();
                }

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
            _logger.LogDebug("📤 Original playback stop body length: {BodyLength} bytes", body.Length);

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
                else
                {
                    // Preserve client payload types as-is (number/string/object/array) to avoid
                    // format exceptions on non-int64 numbers and keep Jellyfin-compatible shapes.
                    stopInfo[prop.Name] = prop.Value.Clone();
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
            _logger.LogDebug("📤 Sending playback stop body (IsPaused=false, {BodyLength} bytes)", body.Length);

            var (result, statusCode) =
                await _proxyService.PostJsonAsync("Sessions/Playing/Stopped", body, Request.Headers);

            if (statusCode == 204 || statusCode == 200)
            {
                _logger.LogDebug("✓ Playback stop forwarded to Jellyfin ({StatusCode})", statusCode);
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    if (!string.IsNullOrWhiteSpace(itemId))
                    {
                        _sessionManager.MarkExplicitStop(deviceId, itemId);
                    }
                    _sessionManager.UpdatePlayingItem(deviceId, null, null);
                    _sessionManager.MarkSessionPotentiallyEnded(deviceId, TimeSpan.FromSeconds(30));
                }
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
    /// Proxy unhandled session-related endpoints to Jellyfin.
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
            var maskedQueryString = MaskSensitiveQueryString(queryString);
            var logEndpoint = string.IsNullOrEmpty(path)
                ? $"Sessions{maskedQueryString}"
                : $"Sessions/{path}{maskedQueryString}";

            _logger.LogDebug("🔄 Proxying session request: {Method} {Endpoint}", method, logEndpoint);
            _logger.LogDebug("Session proxy auth header keys: {HeaderKeys}",
                string.Join(", ", Request.Headers.Keys.Where(h =>
                    h.Contains("Auth", StringComparison.OrdinalIgnoreCase))));

            // Read body if present. Preserve true empty-body requests because Jellyfin
            // uses several POST session-control endpoints with query params only.
            string? body = null;
            var hasRequestBody = !HttpMethods.IsGet(method) &&
                                 (Request.ContentLength.GetValueOrDefault() > 0 ||
                                  Request.Headers.ContainsKey("Transfer-Encoding"));

            if (hasRequestBody)
            {
                Request.EnableBuffering();
                using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8,
                           detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
                {
                    body = await reader.ReadToEndAsync();
                }

                Request.Body.Position = 0;
                _logger.LogDebug("Session proxy body length: {BodyLength} bytes", body.Length);
            }

            // Forward to Jellyfin
            var (result, statusCode) = method switch
            {
                "GET" => await _proxyService.GetJsonAsync(endpoint, null, Request.Headers),
                "POST" => await _proxyService.SendAsync(HttpMethod.Post, endpoint, body, Request.Headers, Request.ContentType),
                "PUT" => await _proxyService.SendAsync(HttpMethod.Put, endpoint, body, Request.Headers, Request.ContentType),
                "DELETE" => await _proxyService.SendAsync(HttpMethod.Delete, endpoint, body, Request.Headers, Request.ContentType),
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

    private static long? ParseOptionalInt64(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var int64Value))
            {
                return int64Value;
            }

            if (value.TryGetDouble(out var doubleValue))
            {
                return (long)doubleValue;
            }

            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
            {
                return parsedInt;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble))
            {
                return (long)parsedDouble;
            }
        }

        return null;
    }

    private static string? ParseOptionalString(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var stringValue = value.GetString();
            return string.IsNullOrWhiteSpace(stringValue) ? null : stringValue;
        }

        return null;
    }

    private static string? TryReadStringProperty(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!obj.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return ParseOptionalString(value);
    }

    private static string? ParsePlaybackSessionId(JsonElement payload)
    {
        var direct = TryReadStringProperty(payload, "PlaySessionId");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (payload.TryGetProperty("PlaySession", out var playSession))
        {
            var nested = TryReadStringProperty(playSession, "Id");
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }

        return null;
    }

    private static bool ShouldSuppressPlaybackSignal(
        string signalType,
        string? deviceId,
        string itemId,
        string? playSessionId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        var normalizedDevice = string.IsNullOrWhiteSpace(deviceId) ? "unknown-device" : deviceId;
        var baseKey = $"{signalType}:{normalizedDevice}:{itemId}";
        var sessionKey = string.IsNullOrWhiteSpace(playSessionId)
            ? null
            : $"{baseKey}:{playSessionId}";

        var now = DateTime.UtcNow;
        if (RecentPlaybackSignals.TryGetValue(baseKey, out var lastSeenAtUtc) &&
            (now - lastSeenAtUtc) <= PlaybackSignalDedupeWindow)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(sessionKey) &&
            RecentPlaybackSignals.TryGetValue(sessionKey, out var lastSeenForSessionAtUtc) &&
            (now - lastSeenForSessionAtUtc) <= PlaybackSignalDedupeWindow)
        {
            return true;
        }

        RecentPlaybackSignals[baseKey] = now;
        if (!string.IsNullOrWhiteSpace(sessionKey))
        {
            RecentPlaybackSignals[sessionKey] = now;
        }

        if (RecentPlaybackSignals.Count > 4096)
        {
            var cutoff = now - PlaybackSignalRetentionWindow;
            foreach (var pair in RecentPlaybackSignals)
            {
                if (pair.Value < cutoff)
                {
                    RecentPlaybackSignals.TryRemove(pair.Key, out _);
                }
            }
        }

        return false;
    }

    private static string? ParsePlaybackItemId(JsonElement payload)
    {
        var direct = TryReadStringProperty(payload, "ItemId");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (payload.TryGetProperty("Item", out var item))
        {
            var nested = TryReadStringProperty(item, "Id");
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }

        return null;
    }

    private static string? ParsePlaybackItemName(JsonElement payload)
    {
        var direct = TryReadStringProperty(payload, "ItemName") ?? TryReadStringProperty(payload, "Name");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (payload.TryGetProperty("Item", out var item))
        {
            var nested = TryReadStringProperty(item, "Name");
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }

        return null;
    }

    private static long? ParsePlaybackPositionTicks(JsonElement payload)
    {
        if (payload.TryGetProperty("PositionTicks", out var positionTicks))
        {
            return ParseOptionalInt64(positionTicks);
        }

        return null;
    }

    #endregion // Session Management

    #endregion // Playback Session Reporting
}
