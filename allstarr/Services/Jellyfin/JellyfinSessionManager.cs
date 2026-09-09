using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using allstarr.Core.Identity;
using allstarr.Models.Settings;
using allstarr.Services.Common;

namespace allstarr.Services.Jellyfin;

public class JellyfinSessionManager : IDisposable
{
    private readonly JellyfinProxyService _proxyService;
    private readonly JellyfinSettings _settings;
    private readonly ILogger<JellyfinSessionManager> _logger;
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionInitLocks = new();
    private readonly ConcurrentDictionary<string, byte> _proxiedWebSocketConnections = new();
    private readonly Timer _keepAliveTimer;
    private int _keepAliveRunning;

    public JellyfinSessionManager(
        JellyfinProxyService proxyService,
        IOptions<JellyfinSettings> settings,
        ILogger<JellyfinSessionManager> logger)
    {
        _proxyService = proxyService;
        _settings = settings.Value;
        _logger = logger;

        // Jellyfin considers inactive sessions stale after roughly 15 seconds.
        _keepAliveTimer = new Timer(KeepSessionsAlive, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        _logger.LogInformation("🔧 SESSION: JellyfinSessionManager initialized with 10-second keep-alive and WebSocket support");
    }

    public async Task<bool> EnsureSessionAsync(string deviceId, string client, string device, string version, IHeaderDictionary headers)
        => await EnsureSessionAsync(deviceId, client, device, version, headers, null);

    public async Task<bool> EnsureSessionAsync(
        string deviceId,
        string client,
        string device,
        string version,
        IHeaderDictionary headers,
        AllstarrPrincipal? principal)
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

            if (_sessions.TryGetValue(deviceId, out var existingSession))
            {
                existingSession.LastActivity = DateTime.UtcNow;
                existingSession.HasProxiedWebSocket = hasProxiedWebSocket;
                existingSession.UserId ??= principal?.UserId;
                existingSession.TenantId ??= principal?.TenantId;
                existingSession.BackendUserId ??= principal?.BackendPrincipalId ?? AuthHeaderHelper.ExtractUserId(headers);
                existingSession.UserName ??= principal?.DisplayName;
                _logger.LogInformation("Session already exists for device {DeviceId}", deviceId);

                if (!hasProxiedWebSocket)
                {
                    // Native proxied websocket sessions remain entirely under Jellyfin's control.
                    var refreshResult = await PostCapabilitiesAsync(headers);
                    if (refreshResult == CapabilitiesPostResult.Unauthorized)
                    {
                        _logger.LogWarning("Token expired for device {DeviceId} - removing session", deviceId);
                        await RemoveSessionAsync(deviceId);
                        return false;
                    }

                    if (refreshResult == CapabilitiesPostResult.Failed)
                    {
                        _logger.LogWarning("Could not refresh capabilities for device {DeviceId}; preserving the existing session", deviceId);
                    }
                }

                return true;
            }

            _logger.LogDebug("Creating new session for device: {DeviceId} ({Client} on {Device})", deviceId, client, device);

            if (!hasProxiedWebSocket)
            {
                // Re-posting capabilities can overwrite a native client's remote-control state.
                var createResult = await PostCapabilitiesAsync(headers);
                if (createResult != CapabilitiesPostResult.Success)
                {
                    _logger.LogError("Failed to create session for {DeviceId}: {Result}", deviceId, createResult);
                    return false;
                }

                _logger.LogInformation("Session created for {DeviceId}", deviceId);
            }
            else
            {
                _logger.LogDebug("Skipping synthetic Jellyfin session bootstrap for proxied websocket device {DeviceId}",
                    deviceId);
            }

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
                HasProxiedWebSocket = hasProxiedWebSocket,
                TenantId = principal?.TenantId,
                UserId = principal?.UserId,
                BackendUserId = principal?.BackendPrincipalId ?? AuthHeaderHelper.ExtractUserId(headers),
                UserName = principal?.DisplayName
            };

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

    private async Task<CapabilitiesPostResult> PostCapabilitiesAsync(IHeaderDictionary headers)
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
            return CapabilitiesPostResult.Success;
        }
        else if (statusCode == 401)
        {
            _logger.LogWarning("Capabilities returned 401 (token expired) - client should re-authenticate");
            return CapabilitiesPostResult.Unauthorized;
        }
        else
        {
            _logger.LogDebug("Capabilities post returned {StatusCode}", statusCode);
            return CapabilitiesPostResult.Failed;
        }
    }

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

    // Explicit stops suppress duplicate stops inferred from progress transitions.
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

    public bool HasSession(string deviceId)
    {
        return !string.IsNullOrWhiteSpace(deviceId) && _sessions.ContainsKey(deviceId);
    }

    public string? GetLastPlayingItemId(string deviceId)
    {
        if (_sessions.TryGetValue(deviceId, out var session))
        {
            return session.LastPlayingItemId;
        }

        return null;
    }

    public (string? ItemId, long? PositionTicks) GetLastPlayingState(string deviceId)
    {
        if (_sessions.TryGetValue(deviceId, out var session))
        {
            return (session.LastPlayingItemId, session.LastPlayingPositionTicks);
        }

        return (null, null);
    }

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
                session.LastActivity,
                session.UserId,
                session.BackendUserId,
                session.UserName,
                session.Client,
                session.Device,
                session.TenantId))
            .ToList();
    }

    // Jellyfin, not local playback cleanup, owns the upstream session lifetime.
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

    public async Task RemoveSessionAsync(string deviceId)
    {
        _proxiedWebSocketConnections.TryRemove(deviceId, out _);

        if (_sessions.TryRemove(deviceId, out var session))
        {
            _logger.LogDebug("🗑️ SESSION: Removing session for device {DeviceId}", deviceId);

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

                // Internal cleanup must never revoke the user's token.
            }
            catch (Exception ex)
            {
                _logger.LogError("⚠️ SESSION: Error removing session for {DeviceId}: {Message}", deviceId, ex.Message);
            }
        }
    }

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
            var jellyfinUrl = _settings.Url?.TrimEnd('/') ?? "";
            var wsScheme = jellyfinUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" : "ws://";
            var jellyfinHost = jellyfinUrl.Replace("https://", "").Replace("http://", "");
            var jellyfinWsUrl = $"{wsScheme}{jellyfinHost}/socket";

            // Server credentials here would attribute the session to the server/admin, not the client.

            webSocket = new ClientWebSocket();
            session.WebSocket = webSocket;

            // The request-scoped header collection may already be disposed.
            var sessionHeaders = session.Headers;

            _logger.LogDebug("🔍 WEBSOCKET: Available headers for {DeviceId}: {Headers}",
                deviceId, string.Join(", ", sessionHeaders.Keys));

            bool authFound = false;
            if (sessionHeaders.TryGetValue("Authorization", out var auth))
            {
                webSocket.Options.SetRequestHeader("Authorization", auth.ToString());
                _logger.LogDebug("🔑 WEBSOCKET: Using Authorization for {DeviceId}", deviceId);
                authFound = true;
            }
            else if (sessionHeaders.TryGetValue("X-Emby-Authorization", out var embyAuth))
            {
                webSocket.Options.SetRequestHeader("Authorization", embyAuth.ToString());
                _logger.LogDebug("🔑 WEBSOCKET: Upgraded legacy authorization for {DeviceId}", deviceId);
                authFound = true;
            }
            else if (sessionHeaders.TryGetValue("X-Emby-Token", out var token))
            {
                webSocket.Options.SetRequestHeader(
                    "Authorization",
                    AuthHeaderHelper.CreateAuthHeader(
                        token.ToString(), session.Client, session.Device, deviceId, session.Version));
                _logger.LogDebug("🔑 WEBSOCKET: Upgraded legacy token for {DeviceId}", deviceId);
                authFound = true;
            }

            if (!authFound)
            {
                if (!string.IsNullOrEmpty(_settings.ApiKey))
                {
                    jellyfinWsUrl += $"?ApiKey={Uri.EscapeDataString(_settings.ApiKey)}";
                    _logger.LogWarning("WEBSOCKET: No client auth found in headers, falling back to server API key for {DeviceId}", deviceId);
                }
                else
                {
                    _logger.LogWarning("❌ WEBSOCKET: No authentication available for {DeviceId} - WebSocket will fail", deviceId);
                }
            }

            _logger.LogDebug("🔗 WEBSOCKET: Connecting to Jellyfin for device {DeviceId}: {Url}", deviceId,
                jellyfinWsUrl.Split('?')[0]);

            webSocket.Options.SetRequestHeader("User-Agent", $"Allstarr-Proxy/{session.Client}");

            await webSocket.ConnectAsync(new Uri(jellyfinWsUrl), CancellationToken.None);
            _logger.LogInformation("✓ WEBSOCKET: Connected to Jellyfin for device {DeviceId}", deviceId);

            // Jellyfin does not expose the connected session until ForceKeepAlive arrives.
            var forceKeepAliveMessage = "{\"MessageType\":\"ForceKeepAlive\",\"Data\":100}";
            var messageBytes = Encoding.UTF8.GetBytes(forceKeepAliveMessage);
            await webSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, CancellationToken.None);
            _logger.LogInformation("📤 WEBSOCKET: Sent ForceKeepAlive to initialize session for {DeviceId}", deviceId);

            var sessionsStartMessage = "{\"MessageType\":\"SessionsStart\",\"Data\":\"0,1500\"}";
            messageBytes = Encoding.UTF8.GetBytes(sessionsStartMessage);
            await webSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, CancellationToken.None);
            _logger.LogDebug("📤 WEBSOCKET: Sent SessionsStart for {DeviceId}", deviceId);

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

                    // Bound the receive so an idle socket cannot starve outbound keep-alives.
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

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

                            if (message.Contains("\"MessageType\":\"KeepAlive\""))
                            {
                                _logger.LogDebug("💓 WEBSOCKET: Received KeepAlive from Jellyfin for {DeviceId}", deviceId);
                            }
                            else if (message.Contains("\"MessageType\":\"Sessions\""))
                            {
                                _logger.LogDebug("📥 WEBSOCKET: Session update for {DeviceId}", deviceId);
                            }
                            else
                            {
                                _logger.LogTrace("📥 WEBSOCKET: {DeviceId}: {Message}",
                                    deviceId, message.Length > 100 ? message[..100] + "..." : message);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                    {
                    }

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

            if (_sessions.TryGetValue(deviceId, out var sess))
            {
                sess.WebSocket = null;
            }
        }
    }

    // Capabilities are a fallback keep-alive; native or synthetic WebSockets are primary.
    private void KeepSessionsAlive(object? state) => _ = RunKeepAlivePassAsync();

    internal async Task RunKeepAlivePassAsync()
    {
        if (Interlocked.Exchange(ref _keepAliveRunning, 1) != 0)
        {
            return;
        }

        try
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

                    var result = await PostCapabilitiesAsync(session.Headers);
                    if (result == CapabilitiesPostResult.Unauthorized)
                    {
                        _logger.LogWarning("Token expired for device {DeviceId} during keep-alive - marking for removal", session.DeviceId);
                        expiredSessions.Add(session.DeviceId);
                    }
                    else if (result == CapabilitiesPostResult.Failed)
                    {
                        _logger.LogWarning("Capability keep-alive failed for device {DeviceId}; preserving the session", session.DeviceId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error keeping session alive for {DeviceId}", session.DeviceId);
                }
            }

            foreach (var deviceId in expiredSessions)
            {
                _logger.LogWarning("Removing session with expired token: {DeviceId}", deviceId);
                await RemoveSessionAsync(deviceId);
            }

            // Three minutes tolerates brief pauses and network interruptions before cleanup.
            var staleSessions = _sessions.Where(kvp => now - kvp.Value.LastActivity > TimeSpan.FromMinutes(3)).ToList();
            foreach (var stale in staleSessions)
            {
                _logger.LogDebug("Removing stale session for {DeviceId} (inactive for {Minutes:F1} minutes)",
                    stale.Key, (now - stale.Value.LastActivity).TotalMinutes);
                await RemoveSessionAsync(stale.Key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Jellyfin session keep-alive");
        }
        finally
        {
            Volatile.Write(ref _keepAliveRunning, 0);
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
        public Guid? TenantId { get; set; }
        public Guid? UserId { get; set; }
        public string? BackendUserId { get; set; }
        public string? UserName { get; set; }
    }

    public sealed record ActivePlaybackState(
        string DeviceId,
        string ItemId,
        long PositionTicks,
        DateTime LastActivity,
        Guid? UserId = null,
        string? BackendUserId = null,
        string? UserName = null,
        string? Client = null,
        string? Device = null,
        Guid? TenantId = null);

    private enum CapabilitiesPostResult
    {
        Success,
        Unauthorized,
        Failed
    }

    public void Dispose()
    {
        _keepAliveTimer?.Dispose();

        foreach (var initLock in _sessionInitLocks.Values)
        {
            initLock.Dispose();
        }

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
