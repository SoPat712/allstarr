using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;

namespace allstarr.Services.Jellyfin;

/// <summary>
/// Manages Jellyfin sessions for connected clients.
/// Creates sessions on first playback and keeps them alive with periodic pings.
/// Also maintains server-side WebSocket connections to Jellyfin on behalf of clients.
/// </summary>
public class JellyfinSessionManager : IDisposable
{
    private readonly JellyfinProxyService _proxyService;
    private readonly JellyfinSettings _settings;
    private readonly ILogger<JellyfinSessionManager> _logger;
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionInitLocks = new();
    private readonly ConcurrentDictionary<string, byte> _proxiedWebSocketConnections = new();
    private readonly Timer _keepAliveTimer;

    public JellyfinSessionManager(
        JellyfinProxyService proxyService,
        IOptions<JellyfinSettings> settings,
        ILogger<JellyfinSessionManager> logger)
    {
        _proxyService = proxyService;
        _settings = settings.Value;
        _logger = logger;

        // Keep sessions alive every 10 seconds (Jellyfin considers sessions stale after ~15 seconds of inactivity)
        _keepAliveTimer = new Timer(KeepSessionsAlive, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        _logger.LogInformation("🔧 SESSION: JellyfinSessionManager initialized with 10-second keep-alive and WebSocket support");
    }

    /// <summary>
    /// Ensures a session exists for the given device. Creates one if needed.
    /// Returns false if token is expired (401), indicating client needs to re-authenticate.
    /// </summary>
    public async Task<bool> EnsureSessionAsync(string deviceId, string client, string device, string version, IHeaderDictionary headers)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            _logger.LogError("Cannot create session - no device ID");
            return false;
        }

        var initLock = _sessionInitLocks.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));
        await initLock.WaitAsync();
        try
        {
            var hasProxiedWebSocket = HasProxiedWebSocket(deviceId);

            // Check if we already have this session tracked
            if (_sessions.TryGetValue(deviceId, out var existingSession))
            {
                existingSession.LastActivity = DateTime.UtcNow;
                existingSession.HasProxiedWebSocket = hasProxiedWebSocket;
                _logger.LogInformation("Session already exists for device {DeviceId}", deviceId);

                if (!hasProxiedWebSocket)
                {
                    // Refresh capabilities to keep session alive only for sessions that Allstarr
                    // is synthesizing itself. Native proxied websocket sessions should be left
                    // entirely under Jellyfin's control.
                    var refreshOk = await PostCapabilitiesAsync(headers);
                    if (!refreshOk)
                    {
                        // Token expired - remove the stale session
                        _logger.LogWarning("Token expired for device {DeviceId} - removing session", deviceId);
                        await RemoveSessionAsync(deviceId);
                        return false;
                    }
                }

                return true;
            }

            _logger.LogDebug("Creating new session for device: {DeviceId} ({Client} on {Device})", deviceId, client, device);

            if (!hasProxiedWebSocket)
            {
                // Post session capabilities to Jellyfin only when Allstarr is creating a
                // synthetic session. If the real client already has a proxied websocket,
                // re-posting capabilities can overwrite its remote-control state.
                var createOk = await PostCapabilitiesAsync(headers);
                if (!createOk)
                {
                    // Token expired or invalid - client needs to re-authenticate
                    _logger.LogError("Failed to create session for {DeviceId} - token may be expired", deviceId);
                    return false;
                }

                _logger.LogInformation("Session created for {DeviceId}", deviceId);
            }
            else
            {
                _logger.LogDebug("Skipping synthetic Jellyfin session bootstrap for proxied websocket device {DeviceId}",
                    deviceId);
            }

            // Track this session
            var clientIp = headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
                          ?? headers["X-Real-IP"].FirstOrDefault()
                          ?? "Unknown";

            _sessions[deviceId] = new SessionInfo
            {
                DeviceId = deviceId,
                Client = client,
                Device = device,
                Version = version,
                LastActivity = DateTime.UtcNow,
                Headers = CloneHeaders(headers),
                ClientIp = clientIp,
                HasProxiedWebSocket = hasProxiedWebSocket
            };

            // Start a synthetic WebSocket connection only when the client itself does not
            // already have a proxied Jellyfin socket through Allstarr.
            if (!hasProxiedWebSocket)
            {
                _ = Task.Run(() => MaintainWebSocketForSessionAsync(deviceId, headers));
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating session for {DeviceId}", deviceId);
            return false;
        }
        finally
        {
            initLock.Release();
        }
    }

    public async Task RegisterProxiedWebSocketAsync(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        _proxiedWebSocketConnections[deviceId] = 0;

        if (_sessions.TryGetValue(deviceId, out var session))
        {
            session.HasProxiedWebSocket = true;
            session.LastActivity = DateTime.UtcNow;
            await CloseSyntheticWebSocketAsync(deviceId, session);
        }
    }

    public void UnregisterProxiedWebSocket(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        _proxiedWebSocketConnections.TryRemove(deviceId, out _);

        if (_sessions.TryGetValue(deviceId, out var session))
        {
            session.HasProxiedWebSocket = false;
            session.LastActivity = DateTime.UtcNow;
        }
    }

    private bool HasProxiedWebSocket(string deviceId)
    {
        return !string.IsNullOrWhiteSpace(deviceId) && _proxiedWebSocketConnections.ContainsKey(deviceId);
    }

    /// <summary>
    /// Posts session capabilities to Jellyfin.
    /// Returns true if successful, false if token expired (401).
    /// </summary>
    private async Task<bool> PostCapabilitiesAsync(IHeaderDictionary headers)
    {
        var capabilities = new
        {
            PlayableMediaTypes = new[] { "Audio" },
            SupportedCommands = new[]
            {
                "Play",
                "Playstate",
                "PlayNext"
            },
            SupportsMediaControl = true,
            SupportsPersistentIdentifier = true,
            SupportsSync = false
        };

        var json = JsonSerializer.Serialize(capabilities);
        var (result, statusCode) = await _proxyService.PostJsonAsync("Sessions/Capabilities/Full", json, headers);

        if (statusCode == 204 || statusCode == 200)
        {
            _logger.LogTrace("Posted capabilities successfully ({StatusCode})", statusCode);
            return true;
        }
        else if (statusCode == 401)
        {
            // Token expired - this is expected, client needs to re-authenticate
            _logger.LogWarning("Capabilities returned 401 (token expired) - client should re-authenticate");
            return false;
        }
        else
        {
            _logger.LogDebug("Capabilities post returned {StatusCode}", statusCode);
            return false;
        }
    }

    /// <summary>
    /// Updates session activity timestamp.
    /// </summary>
    public void UpdateActivity(string deviceId)
    {
        if (_sessions.TryGetValue(deviceId, out var session))
        {
            session.LastActivity = DateTime.UtcNow;
            _logger.LogDebug("🔄 SESSION: Updated activity for {DeviceId}", deviceId);
        }
        else
        {
            _logger.LogError("⚠️ SESSION: Cannot update activity - device {DeviceId} not found", deviceId);
        }
    }

    /// <summary>
    /// Updates the currently playing item for a session (for scrobbling on cleanup).
    /// </summary>
    public void UpdatePlayingItem(string deviceId, string? itemId, long? positionTicks)
    {
        if (_sessions.TryGetValue(deviceId, out var session))
        {
            session.LastPlayingItemId = itemId;
            session.LastPlayingPositionTicks = positionTicks;
            session.LastActivity = DateTime.UtcNow;
            _logger.LogDebug("🎵 SESSION: Updated playing item for {DeviceId}: {ItemId} at {Position}",
                deviceId, itemId, positionTicks);
        }
    }

    /// <summary>
    /// Marks that an explicit playback stop was received for this device+item.
    /// Used to suppress duplicate inferred stop forwarding from progress transitions.
    /// </summary>
    public void MarkExplicitStop(string deviceId, string itemId)
    {
        if (_sessions.TryGetValue(deviceId, out var session))
        {
            lock (session.SyncRoot)
            {
                session.LastExplicitStopItemId = itemId;
                session.LastExplicitStopAtUtc = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// Returns true when an explicit stop for this device+item was recorded within the given time window.
    /// </summary>
    public bool WasRecentlyExplicitlyStopped(string deviceId, string itemId, TimeSpan within)
    {
        if (_sessions.TryGetValue(deviceId, out var session))
        {
            lock (session.SyncRoot)
            {
                if (!string.Equals(session.LastExplicitStopItemId, itemId, StringComparison.Ordinal))
                {
                    return false;
                }

                if (!session.LastExplicitStopAtUtc.HasValue)
                {
                    return false;
                }

                return (DateTime.UtcNow - session.LastExplicitStopAtUtc.Value) <= within;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if a local played-signal was already sent for this device+item.
    /// </summary>
    public bool HasSentLocalPlayedSignal(string deviceId, string itemId)
    {
        if (_sessions.TryGetValue(deviceId, out var session))
        {
            lock (session.SyncRoot)
            {
                return string.Equals(session.LastLocalPlayedSignalItemId, itemId, StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>
    /// Marks that a local played-signal was sent for this device+item.
    /// </summary>
    public void MarkLocalPlayedSignalSent(string deviceId, string itemId)
    {
        if (_sessions.TryGetValue(deviceId, out var session))
        {
            lock (session.SyncRoot)
            {
                session.LastLocalPlayedSignalItemId = itemId;
            }
        }
    }

    /// <summary>
    /// Returns true when a tracked session exists for this device.
    /// </summary>
    public bool HasSession(string deviceId)
    {
        return !string.IsNullOrWhiteSpace(deviceId) && _sessions.ContainsKey(deviceId);
    }

    /// <summary>
    /// Gets the last playing item id for a tracked session, if present.
    /// </summary>
    public string? GetLastPlayingItemId(string deviceId)
    {
        if (_sessions.TryGetValue(deviceId, out var session))
        {
            return session.LastPlayingItemId;
        }

        return null;
    }

    /// <summary>
    /// Gets last tracked playing item and position for a device, if present.
    /// </summary>
    public (string? ItemId, long? PositionTicks) GetLastPlayingState(string deviceId)
    {
        if (_sessions.TryGetValue(deviceId, out var session))
        {
            return (session.LastPlayingItemId, session.LastPlayingPositionTicks);
        }

        return (null, null);
    }

    /// <summary>
    /// Returns current active playback states for tracked sessions.
    /// </summary>
    public IReadOnlyList<ActivePlaybackState> GetActivePlaybackStates(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;

        return _sessions.Values
            .Where(session =>
                !string.IsNullOrWhiteSpace(session.LastPlayingItemId) &&
                session.LastActivity >= cutoff)
            .Select(session => new ActivePlaybackState(
                session.DeviceId,
                session.LastPlayingItemId!,
                session.LastPlayingPositionTicks ?? 0,
                session.LastActivity))
            .ToList();
    }

    /// <summary>
    /// Marks a session as potentially ended (e.g., after playback stops).
    /// Jellyfin should decide when the upstream playback session expires.
    /// </summary>
    public void MarkSessionPotentiallyEnded(string deviceId, TimeSpan timeout)
    {
        if (_sessions.TryGetValue(deviceId, out _))
        {
            _logger.LogDebug(
                "⏰ SESSION: Playback stopped for {DeviceId}; leaving upstream session lifetime to Jellyfin (timeout hint {Seconds}s ignored)",
                deviceId,
                timeout.TotalSeconds);
        }
    }

    /// <summary>
    /// Gets information about current active sessions for debugging.
    /// </summary>
    public object GetSessionsInfo()
    {
        var now = DateTime.UtcNow;
        var sessions = _sessions.Values.Select(s => new
        {
            DeviceId = s.DeviceId,
            Client = s.Client,
            Device = s.Device,
            Version = s.Version,
            ClientIp = s.ClientIp,
            LastActivity = s.LastActivity,
            InactiveMinutes = Math.Round((now - s.LastActivity).TotalMinutes, 1),
            HasWebSocket = s.HasProxiedWebSocket || s.WebSocket != null,
            HasProxiedWebSocket = s.HasProxiedWebSocket,
            HasSyntheticWebSocket = s.WebSocket != null,
            WebSocketState = s.HasProxiedWebSocket ? "Proxied" : s.WebSocket?.State.ToString() ?? "None"
        }).ToList();

        return new
        {
            TotalSessions = sessions.Count,
            ActiveSessions = sessions.Count(s => s.InactiveMinutes < 2),
            StaleSessions = sessions.Count(s => s.InactiveMinutes >= 2),
            Sessions = sessions.OrderBy(s => s.InactiveMinutes)
        };
    }

    /// <summary>
    /// Removes a session when the client disconnects.
    /// </summary>
    public async Task RemoveSessionAsync(string deviceId)
    {
        _proxiedWebSocketConnections.TryRemove(deviceId, out _);

        if (_sessions.TryRemove(deviceId, out var session))
        {
            _logger.LogDebug("🗑️ SESSION: Removing session for device {DeviceId}", deviceId);

            // Close WebSocket if it exists
            if (session.WebSocket != null && session.WebSocket.State == WebSocketState.Open)
            {
                try
                {
                    await session.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
                    _logger.LogDebug("🔌 WEBSOCKET: Closed WebSocket for device {DeviceId}", deviceId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "WEBSOCKET: Error closing WebSocket for {DeviceId}", deviceId);
                }
                finally
                {
                    session.WebSocket?.Dispose();
                }
            }

            try
            {
                // Report playback stopped to Jellyfin if we have a playing item (for scrobbling)
                if (!string.IsNullOrEmpty(session.LastPlayingItemId))
                {
                    var stopPayload = new
                    {
                        ItemId = session.LastPlayingItemId,
                        PositionTicks = session.LastPlayingPositionTicks ?? 0
                    };
                    var stopJson = JsonSerializer.Serialize(stopPayload);
                    await _proxyService.PostJsonAsync("Sessions/Playing/Stopped", stopJson, session.Headers);
                    _logger.LogInformation("🛑 SESSION: Reported playback stopped for {DeviceId} (ItemId: {ItemId}, Position: {Position})",
                        deviceId, session.LastPlayingItemId, session.LastPlayingPositionTicks);
                }

                // Let Jellyfin retire the session naturally; internal cleanup must not revoke the user's token.
            }
            catch (Exception ex)
            {
                _logger.LogError("⚠️ SESSION: Error removing session for {DeviceId}: {Message}", deviceId, ex.Message);
            }
        }
    }

    /// <summary>
    /// Maintains a WebSocket connection to Jellyfin on behalf of a client session.
    /// This allows the session to appear in Jellyfin's dashboard.
    /// </summary>
    private async Task MaintainWebSocketForSessionAsync(string deviceId, IHeaderDictionary headers)
    {
        if (!_sessions.TryGetValue(deviceId, out var session))
        {
            _logger.LogError("⚠️ WEBSOCKET: Cannot create WebSocket - session {DeviceId} not found", deviceId);
            return;
        }

        if (session.HasProxiedWebSocket || HasProxiedWebSocket(deviceId))
        {
            _logger.LogDebug("Skipping synthetic Jellyfin websocket for proxied device {DeviceId}", deviceId);
            return;
        }

        ClientWebSocket? webSocket = null;

        try
        {
            // Build Jellyfin WebSocket URL
            var jellyfinUrl = _settings.Url?.TrimEnd('/') ?? "";
            var wsScheme = jellyfinUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" : "ws://";
            var jellyfinHost = jellyfinUrl.Replace("https://", "").Replace("http://", "");
            var jellyfinWsUrl = $"{wsScheme}{jellyfinHost}/socket";

            // IMPORTANT: Do NOT add api_key to URL - we want to authenticate as the CLIENT, not the server
            // The client's token is passed via X-Emby-Authorization header
            // Using api_key would create a session for the server/admin, not the actual user's client

            webSocket = new ClientWebSocket();
            session.WebSocket = webSocket;

            // Use stored session headers instead of parameter (parameter might be disposed)
            var sessionHeaders = session.Headers;

            // Log available headers for debugging
            _logger.LogDebug("🔍 WEBSOCKET: Available headers for {DeviceId}: {Headers}",
                deviceId, string.Join(", ", sessionHeaders.Keys));

            // Forward authentication headers from the CLIENT - this is critical for session to appear under the right user
            bool authFound = false;
            if (sessionHeaders.TryGetValue("X-Emby-Authorization", out var embyAuth))
            {
                webSocket.Options.SetRequestHeader("X-Emby-Authorization", embyAuth.ToString());
                _logger.LogDebug("🔑 WEBSOCKET: Using X-Emby-Authorization for {DeviceId}", deviceId);
                authFound = true;
            }
            else if (sessionHeaders.TryGetValue("X-Emby-Token", out var token))
            {
                webSocket.Options.SetRequestHeader("X-Emby-Token", token.ToString());
                _logger.LogDebug("🔑 WEBSOCKET: Using X-Emby-Token for {DeviceId}", deviceId);
                authFound = true;
            }
            else if (sessionHeaders.TryGetValue("Authorization", out var auth))
            {
                var authValue = auth.ToString();
                if (authValue.Contains("MediaBrowser", StringComparison.OrdinalIgnoreCase))
                {
                    webSocket.Options.SetRequestHeader("X-Emby-Authorization", authValue);
                    _logger.LogDebug("🔑 WEBSOCKET: Converted Authorization to X-Emby-Authorization for {DeviceId}",
                        deviceId);
                    authFound = true;
                }
                else
                {
                    webSocket.Options.SetRequestHeader("Authorization", authValue);
                    _logger.LogDebug("🔑 WEBSOCKET: Using Authorization for {DeviceId}", deviceId);
                    authFound = true;
                }
            }

            if (!authFound)
            {
                // No client auth found - fall back to server API key as last resort
                if (!string.IsNullOrEmpty(_settings.ApiKey))
                {
                    jellyfinWsUrl += $"?api_key={_settings.ApiKey}";
                    _logger.LogWarning("WEBSOCKET: No client auth found in headers, falling back to server API key for {DeviceId}", deviceId);
                }
                else
                {
                    _logger.LogWarning("❌ WEBSOCKET: No authentication available for {DeviceId} - WebSocket will fail", deviceId);
                }
            }

            _logger.LogDebug("🔗 WEBSOCKET: Connecting to Jellyfin for device {DeviceId}: {Url}", deviceId,
                jellyfinWsUrl.Split('?')[0]);

            // Set user agent
            webSocket.Options.SetRequestHeader("User-Agent", $"Allstarr-Proxy/{session.Client}");

            // Connect to Jellyfin
            await webSocket.ConnectAsync(new Uri(jellyfinWsUrl), CancellationToken.None);
            _logger.LogInformation("✓ WEBSOCKET: Connected to Jellyfin for device {DeviceId}", deviceId);

            // CRITICAL: Send ForceKeepAlive message to initialize session in Jellyfin
            // This tells Jellyfin to create/show the session in the dashboard
            // Without this message, the WebSocket is connected but no session appears
            var forceKeepAliveMessage = "{\"MessageType\":\"ForceKeepAlive\",\"Data\":100}";
            var messageBytes = Encoding.UTF8.GetBytes(forceKeepAliveMessage);
            await webSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, CancellationToken.None);
            _logger.LogInformation("📤 WEBSOCKET: Sent ForceKeepAlive to initialize session for {DeviceId}", deviceId);

            // Also send SessionsStart to subscribe to session updates
            var sessionsStartMessage = "{\"MessageType\":\"SessionsStart\",\"Data\":\"0,1500\"}";
            messageBytes = Encoding.UTF8.GetBytes(sessionsStartMessage);
            await webSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, CancellationToken.None);
            _logger.LogDebug("📤 WEBSOCKET: Sent SessionsStart for {DeviceId}", deviceId);

            // Keep the WebSocket alive by reading messages and sending periodic keep-alive
            var buffer = new byte[1024 * 4];
            var lastKeepAlive = DateTime.UtcNow;
            using var cts = new CancellationTokenSource();

            while (webSocket.State == WebSocketState.Open && _sessions.ContainsKey(deviceId))
            {
                try
                {
                    if (HasProxiedWebSocket(deviceId))
                    {
                        _logger.LogDebug("Stopping synthetic Jellyfin websocket because proxied client websocket is active for {DeviceId}",
                            deviceId);
                        break;
                    }

                    // Use a timeout so we can send keep-alive messages periodically
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                    try
                    {
                        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), timeoutCts.Token);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _logger.LogDebug("🔌 WEBSOCKET: Jellyfin closed WebSocket for device {DeviceId}", deviceId);
                            break;
                        }

                        // Log received messages for debugging (only non-routine messages)
                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

                            // Respond to KeepAlive requests from Jellyfin
                            if (message.Contains("\"MessageType\":\"KeepAlive\""))
                            {
                                _logger.LogDebug("💓 WEBSOCKET: Received KeepAlive from Jellyfin for {DeviceId}", deviceId);
                            }
                            else if (message.Contains("\"MessageType\":\"Sessions\""))
                            {
                                // Session updates are routine, log at debug level
                                _logger.LogDebug("📥 WEBSOCKET: Session update for {DeviceId}", deviceId);
                            }
                            else
                            {
                                // Log other message types at trace level
                                _logger.LogTrace("📥 WEBSOCKET: {DeviceId}: {Message}",
                                    deviceId, message.Length > 100 ? message[..100] + "..." : message);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                    {
                        // Timeout - this is expected, send keep-alive if needed
                    }

                    // Send periodic keep-alive every 30 seconds
                    if (DateTime.UtcNow - lastKeepAlive > TimeSpan.FromSeconds(30))
                    {
                        var keepAliveMsg = "{\"MessageType\":\"KeepAlive\"}";
                        var keepAliveBytes = Encoding.UTF8.GetBytes(keepAliveMsg);
                        await webSocket.SendAsync(new ArraySegment<byte>(keepAliveBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                        _logger.LogDebug("💓 WEBSOCKET: Sent KeepAlive for {DeviceId}", deviceId);
                        lastKeepAlive = DateTime.UtcNow;
                    }
                }
                catch (WebSocketException wsEx)
                {
                    _logger.LogDebug(wsEx, "WEBSOCKET: Connection closed for device {DeviceId}", deviceId);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ WEBSOCKET: Failed to maintain WebSocket for device {DeviceId}", deviceId);
        }
        finally
        {
            if (webSocket != null)
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
                    }
                    catch { }
                }
                webSocket.Dispose();
                _logger.LogDebug("🧹 WEBSOCKET: Cleaned up WebSocket for device {DeviceId}", deviceId);
            }

            // Clear WebSocket reference from session
            if (_sessions.TryGetValue(deviceId, out var sess))
            {
                sess.WebSocket = null;
            }
        }
    }

    /// <summary>
    /// Periodically pings Jellyfin to keep sessions alive.
    /// Note: This is a backup mechanism. The WebSocket connection is the primary keep-alive.
    /// Removes sessions with expired tokens (401 responses).
    /// </summary>
    private async void KeepSessionsAlive(object? state)
    {
        var now = DateTime.UtcNow;
        var activeSessions = _sessions.Values.Where(s => now - s.LastActivity < TimeSpan.FromMinutes(5)).ToList();

        if (activeSessions.Count == 0)
        {
            return;
        }

        _logger.LogTrace("Keeping {Count} sessions alive", activeSessions.Count);

        var expiredSessions = new List<string>();

        foreach (var session in activeSessions)
        {
            try
            {
                session.HasProxiedWebSocket = HasProxiedWebSocket(session.DeviceId);
                if (session.HasProxiedWebSocket)
                {
                    continue;
                }

                // Post capabilities again to keep session alive
                // If this returns false (401), the token has expired
                var success = await PostCapabilitiesAsync(session.Headers);

                if (!success)
                {
                    _logger.LogWarning("Token expired for device {DeviceId} during keep-alive - marking for removal", session.DeviceId);
                    expiredSessions.Add(session.DeviceId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error keeping session alive for {DeviceId}", session.DeviceId);
            }
        }

        // Remove sessions with expired tokens
        foreach (var deviceId in expiredSessions)
        {
            _logger.LogWarning("Removing session with expired token: {DeviceId}", deviceId);
            await RemoveSessionAsync(deviceId);
        }

        // Clean up stale sessions after 3 minutes of inactivity
        // This balances cleaning up finished sessions with allowing brief pauses/network issues
        var staleSessions = _sessions.Where(kvp => now - kvp.Value.LastActivity > TimeSpan.FromMinutes(3)).ToList();
        foreach (var stale in staleSessions)
        {
            _logger.LogDebug("Removing stale session for {DeviceId} (inactive for {Minutes:F1} minutes)",
                stale.Key, (now - stale.Value.LastActivity).TotalMinutes);
            await RemoveSessionAsync(stale.Key);
        }
    }

    private static IHeaderDictionary CloneHeaders(IHeaderDictionary headers)
    {
        var cloned = new HeaderDictionary();
        foreach (var header in headers)
        {
            cloned[header.Key] = header.Value;
        }
        return cloned;
    }

    private class SessionInfo
    {
        public object SyncRoot { get; } = new();
        public required string DeviceId { get; init; }
        public required string Client { get; init; }
        public required string Device { get; init; }
        public required string Version { get; init; }
        public DateTime LastActivity { get; set; }
        public required IHeaderDictionary Headers { get; init; }
        public ClientWebSocket? WebSocket { get; set; }
        public string? LastPlayingItemId { get; set; }
        public long? LastPlayingPositionTicks { get; set; }
        public string? ClientIp { get; set; }
        public string? LastLocalPlayedSignalItemId { get; set; }
        public string? LastExplicitStopItemId { get; set; }
        public DateTime? LastExplicitStopAtUtc { get; set; }
        public bool HasProxiedWebSocket { get; set; }
    }

    public sealed record ActivePlaybackState(
        string DeviceId,
        string ItemId,
        long PositionTicks,
        DateTime LastActivity);

    public void Dispose()
    {
        _keepAliveTimer?.Dispose();

        foreach (var initLock in _sessionInitLocks.Values)
        {
            initLock.Dispose();
        }

        // Close all WebSocket connections
        foreach (var session in _sessions.Values)
        {
            if (session.WebSocket != null && session.WebSocket.State == WebSocketState.Open)
            {
                try
                {
                    session.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Service stopping", CancellationToken.None).Wait(TimeSpan.FromSeconds(5));
                }
                catch { }
                finally
                {
                    session.WebSocket?.Dispose();
                }
            }
        }
    }

    private async Task CloseSyntheticWebSocketAsync(string deviceId, SessionInfo session)
    {
        var syntheticSocket = session.WebSocket;
        if (syntheticSocket == null)
        {
            return;
        }

        session.WebSocket = null;

        try
        {
            if (syntheticSocket.State == WebSocketState.Open || syntheticSocket.State == WebSocketState.CloseReceived)
            {
                await syntheticSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Native client websocket active", CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to close synthetic Jellyfin websocket for proxied device {DeviceId}", deviceId);
        }
        finally
        {
            syntheticSocket.Dispose();
        }
    }
}
