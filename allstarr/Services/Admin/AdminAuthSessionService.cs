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
    public string BackendType { get; init; } = "Jellyfin";
    public Guid? TenantId { get; init; }
    public Guid? AllstarrUserId { get; init; }
    public required string JellyfinAccessToken { get; init; }
    public string? JellyfinServerId { get; init; }
    public bool IsPersistent { get; init; }
    public required DateTime ExpiresAtUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// Cookie-backed admin sessions for the local Web UI.
/// Session IDs stay in the browser cookie, while the resolved backend identity
/// is protected and persisted so brief app restarts do not force a relogin.
/// </summary>
public class AdminAuthSessionService
{
    public const string SessionCookieName = "allstarr_admin_session";
    public const string HttpContextSessionItemKey = "__allstarr_admin_auth_session";

    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromHours(12);
    public static readonly TimeSpan PersistentSessionLifetime = TimeSpan.FromDays(30);

    private readonly ConcurrentDictionary<string, AdminAuthSession> _sessions = new();
    private readonly IDataProtector _protector;
    private readonly ILogger<AdminAuthSessionService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _persistLock = new();
    private readonly string _sessionStoreFilePath;

    public AdminAuthSessionService(
        IDataProtectionProvider dataProtectionProvider,
        ILogger<AdminAuthSessionService> logger,
        IConfiguration configuration)
        : this(
            dataProtectionProvider,
            logger,
            configuration["Admin:SessionStorePath"] ?? "/app/cache/admin-auth/sessions.protected")
    {
    }

    public AdminAuthSessionService(
        IDataProtectionProvider dataProtectionProvider,
        ILogger<AdminAuthSessionService> logger)
        : this(dataProtectionProvider, logger, "/app/cache/admin-auth/sessions.protected")
    {
    }

    private AdminAuthSessionService(
        IDataProtectionProvider dataProtectionProvider,
        ILogger<AdminAuthSessionService> logger,
        string sessionStoreFilePath)
    {
        _protector = dataProtectionProvider.CreateProtector("allstarr.admin.auth.sessions.v1");
        _logger = logger;
        _sessionStoreFilePath = sessionStoreFilePath;

        var directory = Path.GetDirectoryName(_sessionStoreFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        LoadSessionsFromDisk();
    }

    public AdminAuthSessionService(ILogger<AdminAuthSessionService> logger)
        : this(
            CreateFallbackDataProtectionProvider(),
            logger,
            Path.Combine(Path.GetTempPath(), "allstarr-admin-auth", "sessions.protected"))
    {
    }

    public AdminAuthSessionService()
        : this(
            CreateFallbackDataProtectionProvider(),
            NullLogger<AdminAuthSessionService>.Instance,
            Path.Combine(Path.GetTempPath(), "allstarr-admin-auth", "sessions.protected"))
    {
    }

    public AdminAuthSession CreateSession(
        string userId,
        string userName,
        bool isAdministrator,
        string jellyfinAccessToken,
        string? jellyfinServerId,
        bool isPersistent = false,
        string backendType = "Jellyfin",
        Guid? tenantId = null,
        Guid? allstarrUserId = null)
    {
        RemoveExpiredSessions();

        var now = DateTime.UtcNow;
        var session = new AdminAuthSession
        {
            SessionId = GenerateSessionId(),
            UserId = userId,
            UserName = userName,
            IsAdministrator = isAdministrator,
            BackendType = backendType,
            TenantId = tenantId,
            AllstarrUserId = allstarrUserId,
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
            if (!File.Exists(_sessionStoreFilePath))
            {
                return;
            }

            var protectedPayload = File.ReadAllText(_sessionStoreFilePath);
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
                var backendType = string.IsNullOrWhiteSpace(persisted.BackendType)
                    ? "Jellyfin"
                    : persisted.BackendType;
                if (string.IsNullOrWhiteSpace(persisted.SessionId) ||
                    string.IsNullOrWhiteSpace(persisted.UserId) ||
                    string.IsNullOrWhiteSpace(persisted.UserName) ||
                    (!backendType.Equals("Subsonic", StringComparison.OrdinalIgnoreCase) &&
                     string.IsNullOrWhiteSpace(persisted.JellyfinAccessToken)) ||
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
                    BackendType = backendType,
                    TenantId = persisted.TenantId,
                    AllstarrUserId = persisted.AllstarrUserId,
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
            _logger.LogWarning(
                "Failed to load persisted admin auth sessions ({ExceptionType}); starting with an empty session store",
                ex.GetType().Name);
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
                        BackendType = session.BackendType,
                        TenantId = session.TenantId,
                        AllstarrUserId = session.AllstarrUserId,
                        JellyfinAccessToken = session.JellyfinAccessToken,
                        JellyfinServerId = session.JellyfinServerId,
                        IsPersistent = session.IsPersistent,
                        ExpiresAtUtc = session.ExpiresAtUtc,
                        LastSeenUtc = session.LastSeenUtc
                    })
                    .ToList();

                var json = JsonSerializer.Serialize(activeSessions, _jsonOptions);
                var protectedPayload = _protector.Protect(json);
                File.WriteAllText(_sessionStoreFilePath, protectedPayload);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Failed to persist admin auth sessions ({ExceptionType})",
                    ex.GetType().Name);
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
        public string BackendType { get; init; } = "Jellyfin";
        public Guid? TenantId { get; init; }
        public Guid? AllstarrUserId { get; init; }
        public required string JellyfinAccessToken { get; init; }
        public string? JellyfinServerId { get; init; }
        public required bool IsPersistent { get; init; }
        public required DateTime ExpiresAtUtc { get; init; }
        public required DateTime LastSeenUtc { get; init; }
    }
}
