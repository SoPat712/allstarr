using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Services.Admin;

public sealed class AdminAuthSession
{
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public required string UserName { get; init; }
    public required bool IsAdministrator { get; init; }
    public required string JellyfinAccessToken { get; init; }
    public string? JellyfinServerId { get; init; }
    public bool IsPersistent { get; init; }
    public required DateTime ExpiresAtUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// Cookie-backed admin sessions for the local Web UI.
/// Session IDs stay in the browser cookie, while the authenticated Jellyfin
/// session details are protected and persisted on disk so brief app restarts
/// do not force a relogin.
/// </summary>
public class AdminAuthSessionService
{
    public const string SessionCookieName = "allstarr_admin_session";
    public const string HttpContextSessionItemKey = "__allstarr_admin_auth_session";

    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromHours(12);
    public static readonly TimeSpan PersistentSessionLifetime = TimeSpan.FromDays(30);

    private const string SessionStoreFilePath = "/app/cache/admin-auth/sessions.protected";

    private readonly ConcurrentDictionary<string, AdminAuthSession> _sessions = new();
    private readonly IDataProtector _protector;
    private readonly ILogger<AdminAuthSessionService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _persistLock = new();

    public AdminAuthSessionService(
        IDataProtectionProvider dataProtectionProvider,
        ILogger<AdminAuthSessionService> logger)
    {
        _protector = dataProtectionProvider.CreateProtector("allstarr.admin.auth.sessions.v1");
        _logger = logger;

        var directory = Path.GetDirectoryName(SessionStoreFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        LoadSessionsFromDisk();
    }

    public AdminAuthSessionService(ILogger<AdminAuthSessionService> logger)
        : this(CreateFallbackDataProtectionProvider(), logger)
    {
    }

    public AdminAuthSessionService()
        : this(CreateFallbackDataProtectionProvider(), NullLogger<AdminAuthSessionService>.Instance)
    {
    }

    public AdminAuthSession CreateSession(
        string userId,
        string userName,
        bool isAdministrator,
        string jellyfinAccessToken,
        string? jellyfinServerId,
        bool isPersistent = false)
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
            IsPersistent = isPersistent,
            ExpiresAtUtc = now.Add(isPersistent ? PersistentSessionLifetime : DefaultSessionLifetime),
            LastSeenUtc = now
        };

        _sessions[session.SessionId] = session;
        PersistSessions();
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
            PersistSessions();
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

        if (_sessions.TryRemove(sessionId, out _))
        {
            PersistSessions();
        }
    }

    private void RemoveExpiredSessions()
    {
        var now = DateTime.UtcNow;
        var removedAny = false;
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.ExpiresAtUtc <= now &&
                _sessions.TryRemove(kvp.Key, out _))
            {
                removedAny = true;
            }
        }

        if (removedAny)
        {
            PersistSessions();
        }
    }

    private void LoadSessionsFromDisk()
    {
        try
        {
            if (!File.Exists(SessionStoreFilePath))
            {
                return;
            }

            var protectedPayload = File.ReadAllText(SessionStoreFilePath);
            if (string.IsNullOrWhiteSpace(protectedPayload))
            {
                return;
            }

            var json = _protector.Unprotect(protectedPayload);
            var sessions = JsonSerializer.Deserialize<List<PersistedAdminAuthSession>>(json, _jsonOptions)
                ?? [];

            var now = DateTime.UtcNow;
            foreach (var persisted in sessions)
            {
                if (string.IsNullOrWhiteSpace(persisted.SessionId) ||
                    string.IsNullOrWhiteSpace(persisted.UserId) ||
                    string.IsNullOrWhiteSpace(persisted.UserName) ||
                    string.IsNullOrWhiteSpace(persisted.JellyfinAccessToken) ||
                    persisted.ExpiresAtUtc <= now)
                {
                    continue;
                }

                _sessions[persisted.SessionId] = new AdminAuthSession
                {
                    SessionId = persisted.SessionId,
                    UserId = persisted.UserId,
                    UserName = persisted.UserName,
                    IsAdministrator = persisted.IsAdministrator,
                    JellyfinAccessToken = persisted.JellyfinAccessToken,
                    JellyfinServerId = persisted.JellyfinServerId,
                    IsPersistent = persisted.IsPersistent,
                    ExpiresAtUtc = persisted.ExpiresAtUtc,
                    LastSeenUtc = persisted.LastSeenUtc
                };
            }

            if (_sessions.Count > 0)
            {
                _logger.LogInformation("Loaded {Count} persisted admin auth sessions", _sessions.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted admin auth sessions; starting with an empty session store");
            _sessions.Clear();
        }
    }

    private void PersistSessions()
    {
        lock (_persistLock)
        {
            try
            {
                var activeSessions = _sessions.Values
                    .Where(session => session.ExpiresAtUtc > DateTime.UtcNow)
                    .Select(session => new PersistedAdminAuthSession
                    {
                        SessionId = session.SessionId,
                        UserId = session.UserId,
                        UserName = session.UserName,
                        IsAdministrator = session.IsAdministrator,
                        JellyfinAccessToken = session.JellyfinAccessToken,
                        JellyfinServerId = session.JellyfinServerId,
                        IsPersistent = session.IsPersistent,
                        ExpiresAtUtc = session.ExpiresAtUtc,
                        LastSeenUtc = session.LastSeenUtc
                    })
                    .ToList();

                var json = JsonSerializer.Serialize(activeSessions, _jsonOptions);
                var protectedPayload = _protector.Protect(json);
                File.WriteAllText(SessionStoreFilePath, protectedPayload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist admin auth sessions");
            }
        }
    }

    private static string GenerateSessionId()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static IDataProtectionProvider CreateFallbackDataProtectionProvider()
    {
        var keysDirectory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "allstarr-admin-auth-keys"));
        keysDirectory.Create();
        return DataProtectionProvider.Create(keysDirectory, configuration =>
        {
            configuration.SetApplicationName("allstarr-admin");
        });
    }

    private sealed class PersistedAdminAuthSession
    {
        public required string SessionId { get; init; }
        public required string UserId { get; init; }
        public required string UserName { get; init; }
        public required bool IsAdministrator { get; init; }
        public required string JellyfinAccessToken { get; init; }
        public string? JellyfinServerId { get; init; }
        public required bool IsPersistent { get; init; }
        public required DateTime ExpiresAtUtc { get; init; }
        public required DateTime LastSeenUtc { get; init; }
    }
}
