using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

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

public interface IAdminAuthSessionStore
{
    Task<AdminAuthSessionRecord?> FindAsync(string id, CancellationToken cancellationToken);
    Task AddAsync(AdminAuthSessionRecord record, CancellationToken cancellationToken);
    Task TouchAsync(string id, DateTimeOffset lastSeenAt, CancellationToken cancellationToken);
    Task RemoveAsync(string id, CancellationToken cancellationToken);
    Task RemoveExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class EfAdminAuthSessionStore(IDbContextFactory<AllstarrDbContext> factory)
    : IAdminAuthSessionStore
{
    public async Task<AdminAuthSessionRecord?> FindAsync(string id, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await context.AdminAuthSessions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    }

    public async Task AddAsync(AdminAuthSessionRecord record, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        context.AdminAuthSessions.Add(record);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task TouchAsync(string id, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await context.AdminAuthSessions.Where(item => item.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LastSeenAt, lastSeenAt), cancellationToken);
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await context.AdminAuthSessions.Where(item => item.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task RemoveExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await context.AdminAuthSessions.Where(item => item.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>
/// Stores only opaque session IDs in cookies and encrypted session payloads in PostgreSQL.
/// </summary>
public sealed class AdminAuthSessionService(
    IAdminAuthSessionStore store,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<AdminAuthSessionService> logger)
{
    public const string SessionCookieName = "allstarr_admin_session_v3";
    public const string LegacySessionCookieName = "allstarr_admin_session";
    public const string HttpContextSessionItemKey = "__allstarr_admin_auth_session";

    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromHours(12);
    public static readonly TimeSpan PersistentSessionLifetime = TimeSpan.FromDays(30);

    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector("allstarr.admin.auth.sessions.v2");
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AdminAuthSession> CreateSessionAsync(
        string userId,
        string userName,
        bool isAdministrator,
        string jellyfinAccessToken,
        string? jellyfinServerId,
        bool isPersistent = false,
        string backendType = "Jellyfin",
        Guid? tenantId = null,
        Guid? allstarrUserId = null,
        CancellationToken cancellationToken = default)
    {
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

        await store.RemoveExpiredAsync(now, cancellationToken);
        await store.AddAsync(new AdminAuthSessionRecord
        {
            Id = session.SessionId,
            ProtectedPayload = _protector.Protect(JsonSerializer.Serialize(session, _jsonOptions)),
            ExpiresAt = session.ExpiresAtUtc,
            LastSeenAt = now
        }, cancellationToken);
        return session;
    }

    public async Task<AdminAuthSession?> GetValidSessionAsync(
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        var record = await store.FindAsync(sessionId, cancellationToken);
        if (record is null) return null;
        if (record.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await store.RemoveAsync(sessionId, cancellationToken);
            return null;
        }

        try
        {
            var session = JsonSerializer.Deserialize<AdminAuthSession>(
                _protector.Unprotect(record.ProtectedPayload),
                _jsonOptions);
            if (session is null ||
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(session.SessionId),
                    Convert.FromHexString(sessionId)) ||
                session.ExpiresAtUtc <= DateTime.UtcNow)
            {
                await store.RemoveAsync(sessionId, cancellationToken);
                return null;
            }

            var now = DateTime.UtcNow;
            session.LastSeenUtc = now;
            if (record.LastSeenAt <= DateTimeOffset.UtcNow.AddMinutes(-5))
            {
                await store.TouchAsync(sessionId, now, cancellationToken);
            }
            return session;
        }
        catch (Exception exception) when (
            exception is CryptographicException or JsonException or FormatException)
        {
            await store.RemoveAsync(sessionId, cancellationToken);
            logger.LogWarning(
                "Rejected corrupt administrator session payload ({ExceptionType})",
                exception.GetType().Name);
            return null;
        }
    }

    public async Task<AdminAuthSession?> GetValidSessionAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        foreach (var sessionId in ReadSessionIds(request))
        {
            if (await GetValidSessionAsync(sessionId, cancellationToken) is { } session) return session;
        }
        return null;
    }

    public IReadOnlyList<string> ReadSessionIds(HttpRequest request)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var header in request.Headers.Cookie)
        {
            if (string.IsNullOrWhiteSpace(header)) continue;
            foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length != 2 ||
                    (!pair[0].Trim().Equals(SessionCookieName, StringComparison.Ordinal) &&
                     !pair[0].Trim().Equals(LegacySessionCookieName, StringComparison.Ordinal)))
                {
                    continue;
                }

                var value = pair[1].Trim();
                if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
            }
        }
        return values.ToArray();
    }

    public Task RemoveSessionAsync(string? sessionId, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(sessionId)
            ? Task.CompletedTask
            : store.RemoveAsync(sessionId, cancellationToken);

    private static string GenerateSessionId()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
