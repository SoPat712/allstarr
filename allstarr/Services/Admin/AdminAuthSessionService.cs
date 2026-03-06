using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace allstarr.Services.Admin;

public sealed class AdminAuthSession
{
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public required string UserName { get; init; }
    public required bool IsAdministrator { get; init; }
    public required string JellyfinAccessToken { get; init; }
    public string? JellyfinServerId { get; init; }
    public required DateTime ExpiresAtUtc { get; init; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// In-memory authenticated admin sessions for the local Web UI.
/// </summary>
public class AdminAuthSessionService
{
    public const string SessionCookieName = "allstarr_admin_session";
    public const string HttpContextSessionItemKey = "__allstarr_admin_auth_session";

    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    private readonly ConcurrentDictionary<string, AdminAuthSession> _sessions = new();

    public AdminAuthSession CreateSession(
        string userId,
        string userName,
        bool isAdministrator,
        string jellyfinAccessToken,
        string? jellyfinServerId)
    {
        RemoveExpiredSessions();

        var now = DateTime.UtcNow;
        var session = new AdminAuthSession
        {
            SessionId = GenerateSessionId(),
            UserId = userId,
            UserName = userName,
            IsAdministrator = isAdministrator,
            JellyfinAccessToken = jellyfinAccessToken,
            JellyfinServerId = jellyfinServerId,
            ExpiresAtUtc = now.Add(SessionLifetime),
            LastSeenUtc = now
        };

        _sessions[session.SessionId] = session;
        return session;
    }

    public bool TryGetValidSession(string? sessionId, out AdminAuthSession session)
    {
        session = null!;

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (!_sessions.TryGetValue(sessionId, out var existing))
        {
            return false;
        }

        if (existing.ExpiresAtUtc <= DateTime.UtcNow)
        {
            _sessions.TryRemove(sessionId, out _);
            return false;
        }

        existing.LastSeenUtc = DateTime.UtcNow;
        session = existing;
        return true;
    }

    public void RemoveSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _sessions.TryRemove(sessionId, out _);
    }

    private void RemoveExpiredSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.ExpiresAtUtc <= now)
            {
                _sessions.TryRemove(kvp.Key, out _);
            }
        }
    }

    private static string GenerateSessionId()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
