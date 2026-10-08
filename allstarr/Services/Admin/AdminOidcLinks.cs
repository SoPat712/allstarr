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
        var identity = await db.BackendIdentities.SingleAsync(item =>
            item.TenantId == principal.TenantId && item.UserId == principal.UserId &&
            item.BackendType == principal.BackendType && item.BackendInstanceId == principal.BackendInstanceId &&
            item.PrincipalId == principal.BackendPrincipalId, cancellationToken);
        var existing = await db.AdminOidcLinks.SingleOrDefaultAsync(item => item.Id == key, cancellationToken);
        if (existing != null && existing.BackendIdentityId != identity.Id ||
            await db.AdminOidcLinks.AnyAsync(item => item.BackendIdentityId == identity.Id && item.Id != key, cancellationToken))
            throw new UnauthorizedAccessException("An account is already linked. Disconnect it before linking another account.");
        if (credential.Backend != identity.BackendType || credential.UserId != identity.PrincipalId ||
            credential.Endpoint != backend.Endpoint) throw new UnauthorizedAccessException("Backend identity changed.");

        var payload = JsonSerializer.SerializeToUtf8Bytes(credential);
        try
        {
            var secret = await secrets.StoreWithinTransactionAsync(db, identity.TenantId, SecretPurpose,
                payload, existing?.SecretReferenceId, cancellationToken);
            db.SecretReferences.Local.Single(item => item.Id == secret.Id).BackendIdentityId = identity.Id;
            if (existing == null) db.AdminOidcLinks.Add(new()
            {
                Id = key,
                BackendIdentityId = identity.Id,
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
                             join identity in db.BackendIdentities on link.BackendIdentityId equals identity.Id
                             join secret in db.SecretReferences on link.SecretReferenceId equals secret.Id
                             where link.Id == key && secret.RevokedAt == null &&
                                   secret.BackendIdentityId == identity.Id && secret.TenantId == identity.TenantId &&
                                   secret.Purpose == SecretPurpose && identity.BackendType == backend.Backend &&
                                   identity.BackendInstanceId == identityOptions.BackendInstanceId
                             select new { link, identity }).SingleOrDefaultAsync(cancellationToken);
        if (binding == null) return null;
        using var lease = await secrets.OpenAsync(binding.link.SecretReferenceId,
            new(binding.identity.TenantId), cancellationToken);
        var credential = JsonSerializer.Deserialize<AdminOidcCredential>(lease.Value.Span);
        if (credential == null || credential.UserId != binding.identity.PrincipalId) return null;
        var user = await backend.ValidateAsync(credential, cancellationToken);
        if (user == null) return null;
        var principal = await identities.ResolveAsync(new(backend.Backend, user.UserId, user.Name, user.IsAdministrator), cancellationToken);
        if (principal == null || principal.TenantId != binding.identity.TenantId || principal.UserId != binding.identity.UserId ||
            principal.BackendType != binding.identity.BackendType || principal.BackendInstanceId != binding.identity.BackendInstanceId ||
            principal.BackendPrincipalId != binding.identity.PrincipalId)
            return null;
        return await sessions.CreateSessionAsync(user.UserId, user.Name, user.IsAdministrator, user.AccessToken, null,
            backendType: backend.Backend == "jellyfin" ? "Jellyfin" : "Subsonic", tenantId: principal.TenantId, allstarrUserId: principal.UserId,
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
            var secret = await db.SecretReferences.SingleAsync(item => item.Id == link.SecretReferenceId, cancellationToken);
            secret.RevokedAt = DateTimeOffset.UtcNow;
            db.AdminOidcLinks.Remove(link);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private IQueryable<AdminOidcLinkRecord> OwnedLinks(AllstarrDbContext db, AdminAuthSession session) =>
        from link in db.AdminOidcLinks
        join identity in db.BackendIdentities on link.BackendIdentityId equals identity.Id
        where identity.TenantId == session.TenantId && identity.UserId == session.AllstarrUserId &&
              identity.PrincipalId == session.UserId && identity.BackendType == backend.Backend &&
              identity.BackendInstanceId == identityOptions.BackendInstanceId
        select link;
}
