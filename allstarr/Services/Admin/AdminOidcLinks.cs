using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Admin;

public sealed class AdminOidcLinks(
    IDbContextFactory<AllstarrDbContext> factory,
    EncryptedSecretStore secrets,
    IdentityOptions identityOptions,
    AdminOidcBackendAuthentication backend,
    BackendIdentityResolver identities,
    AdminAuthSessionService sessions)
{
    public const string SecretPurpose = AdminOidcLinkRecord.SecretPurpose;

    public static string? IdentityKey(ClaimsPrincipal principal, string clientId)
    {
        var issuer = principal.FindFirst("iss")?.Value;
        var subject = principal.FindFirst("sub")?.Value;
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(subject))
            return null;
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { issuer, subject, clientId })));
    }

    public async Task LinkAsync(string key, AllstarrPrincipal principal, AdminOidcCredential credential,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.Users.SingleAsync(item => item.Enabled && item.Id == principal.UserId &&
            item.IsAdmin == principal.IsAdministrator &&
            item.BackendType == principal.BackendType && item.BackendInstanceId == principal.BackendInstanceId &&
            item.BackendPrincipalId == principal.BackendPrincipalId, cancellationToken);
        var existing = await db.AdminOidcLinks.SingleOrDefaultAsync(item => item.Id == key, cancellationToken);
        if (existing != null && existing.UserId != user.Id ||
            await db.AdminOidcLinks.AnyAsync(item => item.UserId == user.Id && item.Id != key, cancellationToken))
            throw new UnauthorizedAccessException("An account is already linked. Disconnect it before linking another account.");
        if (credential.Backend != user.BackendType || credential.UserId != user.BackendPrincipalId ||
            credential.Endpoint != backend.Endpoint) throw new UnauthorizedAccessException("Backend identity changed.");

        var payload = JsonSerializer.SerializeToUtf8Bytes(credential);
        try
        {
            var secret = await secrets.StoreWithinTransactionAsync(db, user.Id, SecretPurpose,
                payload, existing?.SecretReferenceId, cancellationToken);
            if (existing == null) db.AdminOidcLinks.Add(new()
            {
                Id = key,
                UserId = user.Id,
                SecretReferenceId = secret.Id,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    public async Task<AdminAuthSession?> SignInAsync(string key, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var binding = await (from link in db.AdminOidcLinks
                             join linkedUser in db.Users on link.UserId equals linkedUser.Id
                             join secret in db.SecretReferences on link.SecretReferenceId equals secret.Id
                             where link.Id == key && secret.RevokedAt == null &&
                                   secret.UserId == linkedUser.Id && secret.Purpose == SecretPurpose &&
                                   linkedUser.Enabled && linkedUser.BackendType == backend.Backend &&
                                   linkedUser.BackendInstanceId == identityOptions.BackendInstanceId
                             select new { link, user = linkedUser }).SingleOrDefaultAsync(cancellationToken);
        if (binding == null) return null;
        using var lease = await secrets.OpenAsync(binding.link.SecretReferenceId,
            new SecretAccessContext(binding.user.Id, SecretPurpose), cancellationToken);
        var credential = JsonSerializer.Deserialize<AdminOidcCredential>(lease.Value.Span);
        if (credential == null || credential.UserId != binding.user.BackendPrincipalId) return null;
        var user = await backend.ValidateAsync(credential, cancellationToken);
        if (user == null) return null;
        var principal = await identities.ResolveAsync(new(backend.Backend, user.UserId, user.Name, user.IsAdministrator), cancellationToken);
        if (principal == null || principal.UserId != binding.user.Id ||
            principal.BackendType != binding.user.BackendType || principal.BackendInstanceId != binding.user.BackendInstanceId ||
            principal.BackendPrincipalId != binding.user.BackendPrincipalId)
            return null;
        return await sessions.CreateSessionAsync(user.UserId, user.Name, user.IsAdministrator, user.AccessToken, null,
            backendType: backend.Backend == "jellyfin" ? "Jellyfin" : "Subsonic", allstarrUserId: principal.UserId,
            cancellationToken: cancellationToken, oidcSecretReferenceId: binding.link.SecretReferenceId,
            oidcBackendEndpoint: backend.Endpoint, oidcBackendInstanceId: identityOptions.BackendInstanceId,
            subsonicReadAuthentication: backend.Backend == "subsonic"
                ? allstarr.Services.Subsonic.SubsonicSessionAuthentication.Create(user.UserId, credential.Credential)
                : null);
    }

    public async Task<bool> IsLinkedAsync(AdminAuthSession session, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await OwnedLinks(db, session).AnyAsync(cancellationToken);
    }

    public async Task UnlinkAsync(AdminAuthSession session, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var link in await OwnedLinks(db, session).ToListAsync(cancellationToken))
        {
            var secret = await db.SecretReferences.SingleAsync(item => item.Id == link.SecretReferenceId &&
                item.UserId == link.UserId && item.Purpose == SecretPurpose, cancellationToken);
            secret.RevokedAt = DateTimeOffset.UtcNow;
            db.AdminOidcLinks.Remove(link);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private IQueryable<AdminOidcLinkRecord> OwnedLinks(AllstarrDbContext db, AdminAuthSession session) =>
        from link in db.AdminOidcLinks
        join user in db.Users on link.UserId equals user.Id
        where user.Id == session.AllstarrUserId && user.Enabled &&
              user.IsAdmin == session.IsAdministrator &&
              user.BackendPrincipalId == session.UserId && user.BackendType == backend.Backend &&
              user.BackendInstanceId == identityOptions.BackendInstanceId
        select link;
}
