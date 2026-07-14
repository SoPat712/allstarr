using System.Net.WebSockets;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Services.Jellyfin;

namespace allstarr.Middleware;

/// <summary>
/// Middleware that proxies WebSocket connections to Jellyfin server.
/// This enables real-time features like session tracking, remote control, and live updates.
/// </summary>
public class WebSocketProxyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly JellyfinSettings _settings;
    private readonly ILogger<WebSocketProxyMiddleware> _logger;
    private readonly JellyfinSessionManager? _sessionManager;

    public WebSocketProxyMiddleware(
        RequestDelegate next,
        IOptions<JellyfinSettings> settings,
        ILogger<WebSocketProxyMiddleware> logger,
        IEnumerable<JellyfinSessionManager> sessionManagers)
    {
        _next = next;
        _settings = settings.Value;
        _logger = logger;
        _sessionManager = sessionManagers.FirstOrDefault();

        _logger.LogInformation("🔧 WEBSOCKET: WebSocketProxyMiddleware initialized - Jellyfin URL: {Url}", _settings.Url);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_sessionManager == null)
        {
            await _next(context);
            return;
        }

        // Log ALL requests for debugging
        var path = context.Request.Path.Value ?? "";
        var isWebSocket = context.WebSockets.IsWebSocketRequest;

        // Log any request that might be WebSocket-related
        if (path.Contains("socket", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("ws", StringComparison.OrdinalIgnoreCase) ||
            isWebSocket ||
            context.Request.Headers.ContainsKey("Upgrade"))
        {
            _logger.LogDebug("🔍 WEBSOCKET: Potential WebSocket request: Path={Path}, IsWs={IsWs}, Method={Method}, Upgrade={Upgrade}, Connection={Connection}",
                path,
                isWebSocket,
                context.Request.Method,
                context.Request.Headers["Upgrade"].ToString(),
                context.Request.Headers["Connection"].ToString());
        }

        // Check if this is a WebSocket request to /socket
        if (context.Request.Path.StartsWithSegments("/socket", StringComparison.OrdinalIgnoreCase) &&
            context.WebSockets.IsWebSocketRequest)
        {
            _logger.LogDebug("🔌 WEBSOCKET: WebSocket connection request received from {RemoteIp}",
                context.Connection.RemoteIpAddress);

            await HandleWebSocketProxyAsync(context);
            return;
        }

        // Not a WebSocket request, pass to next middleware
        await _next(context);
    }

    private async Task HandleWebSocketProxyAsync(HttpContext context)
    {
        var sessionManager = _sessionManager ??
                             throw new InvalidOperationException("Jellyfin WebSocket support is unavailable.");
        ClientWebSocket? serverWebSocket = null;
        WebSocket? clientWebSocket = null;
        string? deviceId = null;

        try
        {
            // Extract device ID from query string or headers for session tracking
            deviceId = context.Request.Query["deviceId"].ToString();
            if (string.IsNullOrEmpty(deviceId))
            {
                // Try to extract from X-Emby-Authorization header
                if (context.Request.Headers.TryGetValue("X-Emby-Authorization", out var authHeader))
                {
                    var authValue = authHeader.ToString();
                    var deviceIdMatch = System.Text.RegularExpressions.Regex.Match(authValue, @"DeviceId=""([^""]+)""");
                    if (deviceIdMatch.Success)
                    {
                        deviceId = deviceIdMatch.Groups[1].Value;
                    }
                }
            }

            if (!string.IsNullOrEmpty(deviceId))
            {
                _logger.LogDebug("🔍 WEBSOCKET: Client WebSocket for device {DeviceId}", deviceId);
            }

            // Build Jellyfin WebSocket URL
            var jellyfinUrl = _settings.Url?.TrimEnd('/') ?? "";
            var wsScheme = jellyfinUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" : "ws://";
            var jellyfinHost = jellyfinUrl.Replace("https://", "").Replace("http://", "");
            var jellyfinWsUrl = $"{wsScheme}{jellyfinHost}/socket";

            // Add query parameters if present (e.g., ?api_key=xxx or ?deviceId=xxx)
            if (context.Request.QueryString.HasValue)
            {
                jellyfinWsUrl += context.Request.QueryString.Value;
            }

            // Build masked query string for safe logging
            var maskedQuery = BuildMaskedQuery(context.Request.QueryString.Value);
            _logger.LogDebug("🔗 WEBSOCKET: Connecting to Jellyfin WebSocket: {BaseUrl}{MaskedQuery}", jellyfinWsUrl.Split('?')[0], maskedQuery);

            // Connect to Jellyfin WebSocket
            serverWebSocket = new ClientWebSocket();

            // Forward authentication headers - check X-Emby-Authorization FIRST
            // Most Jellyfin clients use X-Emby-Authorization, not Authorization
            if (context.Request.Headers.TryGetValue("X-Emby-Authorization", out var embyAuthHeader))
            {
                serverWebSocket.Options.SetRequestHeader("X-Emby-Authorization", embyAuthHeader.ToString());
                _logger.LogDebug("🔑 WEBSOCKET: Forwarded X-Emby-Authorization header");
            }
            else if (context.Request.Headers.TryGetValue("X-Emby-Token", out var tokenHeader))
            {
                serverWebSocket.Options.SetRequestHeader("X-Emby-Token", tokenHeader.ToString());
                _logger.LogDebug("🔑 WEBSOCKET: Forwarded X-Emby-Token header");
            }
            else if (context.Request.Headers.TryGetValue("Authorization", out var authHeader2))
            {
                var authValue = authHeader2.ToString();
                // If it's a MediaBrowser auth header, use X-Emby-Authorization
                if (authValue.Contains("MediaBrowser", StringComparison.OrdinalIgnoreCase))
                {
                    serverWebSocket.Options.SetRequestHeader("X-Emby-Authorization", authValue);
                    _logger.LogDebug("🔑 WEBSOCKET: Converted Authorization to X-Emby-Authorization header");
                }
                else
                {
                    serverWebSocket.Options.SetRequestHeader("Authorization", authValue);
                    _logger.LogDebug("🔑 WEBSOCKET: Forwarded Authorization header");
                }
            }

            // Set user agent
            serverWebSocket.Options.SetRequestHeader("User-Agent", "Allstarr/1.0.3");

            await serverWebSocket.ConnectAsync(new Uri(jellyfinWsUrl), context.RequestAborted);
            _logger.LogInformation("✓ WEBSOCKET: Connected to Jellyfin WebSocket");

            // Only accept the client socket after upstream auth/handshake succeeds.
            // This ensures auth failures surface as HTTP status (401/403) instead of misleading 101 upgrades.
            clientWebSocket = await context.WebSockets.AcceptWebSocketAsync();
            _logger.LogDebug("✓ WEBSOCKET: Client WebSocket accepted");

            if (!string.IsNullOrEmpty(deviceId))
            {
                await sessionManager.RegisterProxiedWebSocketAsync(deviceId);
            }

            // Start bidirectional proxying
            var clientToServer = ProxyMessagesAsync(clientWebSocket, serverWebSocket, "Client→Server", context.RequestAborted);
            var serverToClient = ProxyMessagesAsync(serverWebSocket, clientWebSocket, "Server→Client", context.RequestAborted);

            // Wait for either direction to complete
            await Task.WhenAny(clientToServer, serverToClient);

            _logger.LogDebug("🔌 WEBSOCKET: WebSocket proxy connection closed");
        }
        catch (WebSocketException wsEx)
        {
            var isAuthFailure =
                wsEx.Message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
                wsEx.Message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
                wsEx.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
                wsEx.Message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase);

            if (isAuthFailure)
            {
                _logger.LogWarning("WEBSOCKET: Connection rejected by Jellyfin auth (token expired or session ended)");
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                        title = "Forbidden",
                        status = StatusCodes.Status403Forbidden
                    });
                }
            }
            else
            {
                _logger.LogWarning(wsEx, "⚠️ WEBSOCKET: WebSocket error: {Message}", wsEx.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ WEBSOCKET: Error in WebSocket proxy");
        }
        finally
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                sessionManager.UnregisterProxiedWebSocket(deviceId);
            }

            // Clean up connections
            if (clientWebSocket?.State == WebSocketState.Open)
            {
                try
                {
                    await clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Proxy closing", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error closing client WebSocket");
                }
            }

            if (serverWebSocket?.State == WebSocketState.Open)
            {
                try
                {
                    await serverWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Proxy closing", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error closing server WebSocket");
                }
            }

            clientWebSocket?.Dispose();
            serverWebSocket?.Dispose();

            // CRITICAL: Notify session manager only when a client socket was accepted.
            if (clientWebSocket != null && !string.IsNullOrEmpty(deviceId))
            {
                _logger.LogInformation("🧹 WEBSOCKET: Client disconnected, removing session for device {DeviceId}", deviceId);
                await sessionManager.RemoveSessionAsync(deviceId);
            }

            _logger.LogDebug("🧹 WEBSOCKET: WebSocket connections cleaned up");
        }
    }

    // Helper for building a masked query string for logging. Redacts sensitive keys.
    public static string BuildMaskedQuery(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString)) return string.Empty;

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryString);
        var parts = new List<string>();
        foreach (var kv in query)
        {
            var key = kv.Key;
            var value = kv.Value.ToString();
            if (string.Equals(key, "api_key", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "token", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "auth", StringComparison.OrdinalIgnoreCase))
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

    private async Task ProxyMessagesAsync(
        WebSocket source,
        WebSocket destination,
        string direction,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 4]; // 4KB buffer
        var messageBuffer = new List<byte>();

        try
        {
            while (source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
            {
                var result = await source.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogDebug("🔌 WEBSOCKET {Direction}: Close message received", direction);
                    await destination.CloseAsync(
                        result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription,
                        cancellationToken);
                    break;
                }

                // Accumulate message fragments
                messageBuffer.AddRange(buffer.Take(result.Count));

                // If this is the end of the message, forward it
                if (result.EndOfMessage)
                {
                    var messageBytes = messageBuffer.ToArray();

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("WEBSOCKET {Direction}: {MessageType} message ({Size} bytes)",
                            direction, result.MessageType, messageBytes.Length);
                    }

                    // Forward the complete message
                    await destination.SendAsync(
                        new ArraySegment<byte>(messageBytes),
                        result.MessageType,
                        true,
                        cancellationToken);

                    messageBuffer.Clear();
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("⚠️ WEBSOCKET {Direction}: Operation cancelled", direction);
        }
        catch (WebSocketException wsEx) when (wsEx.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogDebug("⚠️ WEBSOCKET {Direction}: Connection closed prematurely", direction);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WEBSOCKET {Direction}: Error proxying messages (connection closed)", direction);
        }
    }
}
