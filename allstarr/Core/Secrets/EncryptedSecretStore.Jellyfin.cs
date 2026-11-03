using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Secrets;

public sealed partial class EncryptedSecretStore
{
    private sealed record JellyfinPlaylistCredential(string AccessToken, bool NeedsSignIn = false);

    public async Task<BackendCredentialGrant?> GetJellyfinPlaylistGrantAsync(
        AllstarrPrincipal principal, CancellationToken cancellationToken = default) =>
        await GetJellyfinPlaylistGrantAsync(principal.BackendInstanceId, principal.BackendPrincipalId,
            principal.UserId, cancellationToken);

    public async Task<BackendCredentialGrant?> GetJellyfinPlaylistGrantAsync(
        string backendInstanceId, string principalId, Guid? userId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await RequireJellyfinUserAsync(db, userId, backendInstanceId, principalId, cancellationToken);
        var reference = await JellyfinPlaylistGrants(db, user).AsNoTracking()
            .OrderByDescending(item => item.UpdatedAt).FirstOrDefaultAsync(cancellationToken);
        if (reference == null) return null;
        using var lease = await OpenAsync(reference.Id, new(user.Id, BackendCredentialScope.JellyfinPurpose), cancellationToken);
        var credential = JsonSerializer.Deserialize<JellyfinPlaylistCredential>(lease.Value.Span)!;
        return new(reference.Id, reference.UpdatedAt, credential.NeedsSignIn, reference.ActiveVersion, user.Id);
    }

    public async Task<BackendCredentialGrant> StoreJellyfinPlaylistGrantAsync(
        AllstarrPrincipal principal, string accessToken, CancellationToken cancellationToken = default)
    {
        if (principal.BackendType != "jellyfin" || string.IsNullOrWhiteSpace(accessToken)) throw new UnauthorizedAccessException();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await RequireJellyfinUserAsync(db, principal.UserId, principal.BackendInstanceId,
            principal.BackendPrincipalId, cancellationToken);
        var reference = await JellyfinPlaylistGrants(db, user).OrderByDescending(item => item.UpdatedAt)
            .Select(item => (Guid?)item.Id).FirstOrDefaultAsync(cancellationToken);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new JellyfinPlaylistCredential(accessToken));
        try
        {
            var info = await StoreWithinTransactionAsync(db, user.Id, BackendCredentialScope.JellyfinPurpose,
                bytes, reference, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(info.Id, info.UpdatedAt, Version: info.ActiveVersion, UserId: user.Id);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task<SecretLease> OpenJellyfinPlaylistTokenAsync(
        string backendInstanceId, string principalId, BackendCredentialGrant grant,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await RequireJellyfinUserAsync(db, null, backendInstanceId, principalId, cancellationToken);
        if (!await JellyfinPlaylistGrants(db, user).AnyAsync(item => item.Id == grant.ReferenceId &&
                item.ActiveVersion == grant.Version, cancellationToken)) throw new UnauthorizedAccessException();
        using var lease = await OpenAsync(grant.ReferenceId, new(user.Id, BackendCredentialScope.JellyfinPurpose), cancellationToken);
        var credential = JsonSerializer.Deserialize<JellyfinPlaylistCredential>(lease.Value.Span)!;
        return new(System.Text.Encoding.UTF8.GetBytes(credential.AccessToken));
    }

    public async Task RejectJellyfinPlaylistGrantAsync(
        string backendInstanceId, string principalId, BackendCredentialGrant grant,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await RequireJellyfinUserAsync(db, null, backendInstanceId, principalId, cancellationToken);
        var reference = await JellyfinPlaylistGrants(db, user).SingleOrDefaultAsync(item =>
            item.Id == grant.ReferenceId && item.ActiveVersion == grant.Version, cancellationToken);
        if (reference == null) return;
        using var lease = await OpenAsync(reference.Id, new(user.Id, BackendCredentialScope.JellyfinPurpose), cancellationToken);
        var credential = JsonSerializer.Deserialize<JellyfinPlaylistCredential>(lease.Value.Span)!;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(credential with { NeedsSignIn = true });
        try
        {
            await StoreWithinTransactionAsync(db, user.Id, BackendCredentialScope.JellyfinPurpose, bytes,
                reference.Id, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task RevokeJellyfinPlaylistGrantAsync(
        AllstarrPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (principal.BackendType != "jellyfin") throw new UnauthorizedAccessException();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await RequireJellyfinUserAsync(db, principal.UserId, principal.BackendInstanceId,
            principal.BackendPrincipalId, cancellationToken);
        foreach (var grant in await JellyfinPlaylistGrants(db, user).ToListAsync(cancellationToken))
        {
            grant.RevokedAt = _clock.UtcNow;
            grant.UpdatedAt = _clock.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static IQueryable<SecretReferenceRecord> JellyfinPlaylistGrants(AllstarrDbContext db, UserRecord user) =>
        db.SecretReferences.Where(item => item.UserId == user.Id && item.Purpose == BackendCredentialScope.JellyfinPurpose &&
            item.RevokedAt == null);

    private static async Task<UserRecord> RequireJellyfinUserAsync(
        AllstarrDbContext db, Guid? userId, string backendInstanceId, string principalId, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Enabled &&
            (!userId.HasValue || item.Id == userId.Value) && item.BackendType == "jellyfin" &&
            item.BackendInstanceId == backendInstanceId && item.BackendPrincipalId == principalId, cancellationToken)
        ?? throw new UnauthorizedAccessException("The listener's backend identity is unavailable.");
}
