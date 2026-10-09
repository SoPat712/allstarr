using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Secrets;

public sealed record SecretAccessContext(Guid? UserId, string Purpose, bool AllowShared = false);

public sealed record BackendCredentialGrant(Guid ReferenceId, DateTimeOffset UpdatedAt);

public sealed record SecretReferenceInfo(
    Guid Id,
    Guid? UserId,
    string Purpose,
    int ActiveVersion,
    string KeyId,
    DateTimeOffset UpdatedAt,
    bool Revoked);

public sealed record SecretRotationResult(
    string ActiveKeyId,
    int Examined,
    int Rotated,
    int AlreadyActive);

public sealed class SecretLease : IDisposable
{
    private byte[]? _value;

    internal SecretLease(byte[] value)
    {
        _value = value;
    }

    public ReadOnlyMemory<byte> Value => _value ?? throw new ObjectDisposedException(nameof(SecretLease));

    public string ReadUtf8() => Encoding.UTF8.GetString(Value.Span);

    public void Dispose()
    {
        var value = Interlocked.Exchange(ref _value, null);
        if (value != null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }
}

public sealed class EncryptedSecretStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly FileSecretKeyRingProvider _keyRingProvider;
    private readonly SecretStoreOptions _options;
    private readonly IPlatformClock _clock;

    public EncryptedSecretStore(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        FileSecretKeyRingProvider keyRingProvider,
        SecretStoreOptions options,
        IPlatformClock clock)
    {
        _contextFactory = contextFactory;
        _keyRingProvider = keyRingProvider;
        _options = options;
        _clock = clock;
    }

    public async Task<SecretReferenceInfo> StoreAsync(
        Guid? userId,
        string purpose,
        ReadOnlyMemory<byte> plaintext,
        Guid? existingReferenceId = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var result = await StoreWithinTransactionAsync(
            context,
            userId,
            purpose,
            plaintext,
            existingReferenceId,
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Adds an encrypted secret version to a caller-owned database transaction. The caller must save and
    /// commit the transaction. This keeps a secret reference and the durable record that owns it atomic.
    /// </summary>
    public async Task<SecretReferenceInfo> StoreWithinTransactionAsync(
        AllstarrDbContext context,
        Guid? userId,
        string purpose,
        ReadOnlyMemory<byte> plaintext,
        Guid? existingReferenceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Database.CurrentTransaction == null)
        {
            throw new InvalidOperationException("An active database transaction is required.");
        }

        if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > 200)
        {
            throw new ArgumentException("Secret purpose is required and must be at most 200 characters.", nameof(purpose));
        }

        _options.Validate();
        if (plaintext.IsEmpty || plaintext.Length > _options.MaxSecretBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(plaintext),
                $"Secret values must contain 1 to {_options.MaxSecretBytes} bytes.");
        }

        var keyRing = await _keyRingProvider.LoadAsync(cancellationToken);
        var plaintextCopy = plaintext.ToArray();
        try
        {
            var now = _clock.UtcNow;
            SecretReferenceRecord reference;
            if (existingReferenceId.HasValue)
            {
                reference = await context.SecretReferences.SingleOrDefaultAsync(
                                item => item.Id == existingReferenceId.Value,
                                cancellationToken)
                            ?? throw new KeyNotFoundException("Secret reference not found.");
                EnsureOwnerMatch(reference, new SecretAccessContext(userId, purpose.Trim(), userId == null));
                if (reference.RevokedAt.HasValue)
                {
                    throw new InvalidOperationException("A revoked secret reference cannot be replaced.");
                }

                if (!reference.Purpose.Equals(purpose.Trim(), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Secret purpose cannot be changed during replacement.");
                }
            }
            else
            {
                reference = new SecretReferenceRecord
                {
                    Id = Guid.CreateVersion7(),
                    UserId = userId,
                    Purpose = purpose.Trim(),
                    CreatedAt = now,
                    UpdatedAt = now
                };
                context.SecretReferences.Add(reference);
            }

            var nextVersion = reference.ActiveVersion + 1;
            var encrypted = Encrypt(
                plaintextCopy,
                keyRing.GetActiveKey(),
                AssociatedData(reference, nextVersion));
            var previous = reference.ActiveVersion == 0
                ? null
                : await context.SecretVersions.SingleAsync(
                    item => item.SecretReferenceId == reference.Id &&
                            item.Version == reference.ActiveVersion,
                    cancellationToken);
            if (previous != null)
            {
                previous.RetiredAt = now;
            }

            var version = new SecretVersionRecord
            {
                Id = Guid.CreateVersion7(),
                SecretReferenceId = reference.Id,
                Version = nextVersion,
                KeyId = keyRing.ActiveKeyId,
                Nonce = encrypted.Nonce,
                Ciphertext = encrypted.Ciphertext,
                AuthenticationTag = encrypted.Tag,
                CreatedAt = now
            };
            context.SecretVersions.Add(version);
            reference.ActiveVersion = nextVersion;
            reference.UpdatedAt = now;
            return ToInfo(reference, version);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextCopy);
            ClearKeyRing(keyRing);
        }
    }

    public async Task<SecretLease> OpenProviderAccountAsync(
        ProviderAccountContext account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var current = await context.ProviderAccounts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == account.AccountId, cancellationToken);
        if (current == null || !current.Enabled || !account.Enabled ||
            current.ProviderId != account.ProviderId || current.Revision != account.Revision ||
            current.OwnerUserId != account.OwnerUserId ||
            current.Scope != account.Scope ||
            current.SecretReferenceId == null || current.SecretReferenceId != account.SecretReferenceId)
            throw new UnauthorizedAccessException("The provider account authorization is no longer current.");

        if (current.OwnerUserId is { } owner && !await context.Users.AsNoTracking()
                .AnyAsync(item => item.Id == owner && item.Enabled, cancellationToken))
            throw new UnauthorizedAccessException("The provider account owner is unavailable.");
        var purpose = $"provider-account:{current.ProviderId}:{current.Id:N}";
        var referenceAllowed = await context.SecretReferences.AsNoTracking().AnyAsync(item =>
            item.Id == current.SecretReferenceId && item.UserId == current.OwnerUserId &&
            item.Purpose == purpose && item.RevokedAt == null, cancellationToken);
        if (!referenceAllowed)
            throw new UnauthorizedAccessException("The credential does not belong to the selected provider account.");

        return await OpenAsync(current.SecretReferenceId.Value,
            new SecretAccessContext(current.OwnerUserId, purpose, AllowShared: current.OwnerUserId == null),
            cancellationToken);
    }

    public async Task<BackendCredentialGrant?> GetSubsonicPlaylistGrantAsync(
        AllstarrPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (principal.BackendType != "subsonic") throw new UnauthorizedAccessException();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await RequireSubsonicUserAsync(db, principal.UserId,
            principal.BackendInstanceId, principal.BackendPrincipalId, cancellationToken);
        return await PlaylistGrants(db, user).OrderByDescending(item => item.UpdatedAt)
            .Select(item => new BackendCredentialGrant(item.Id, item.UpdatedAt)).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<BackendCredentialGrant> StoreSubsonicPlaylistGrantAsync(
        AllstarrPrincipal principal, string password, CancellationToken cancellationToken = default)
    {
        if (principal.BackendType != "subsonic") throw new UnauthorizedAccessException();
        if (string.IsNullOrWhiteSpace(password) || password.Length > 2000)
            throw new ArgumentException("A backend password is required.", nameof(password));
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await RequireSubsonicUserAsync(db, principal.UserId,
            principal.BackendInstanceId, principal.BackendPrincipalId, cancellationToken);
        var existing = await PlaylistGrants(db, user).OrderByDescending(item => item.UpdatedAt)
            .Select(item => (Guid?)item.Id).FirstOrDefaultAsync(cancellationToken);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { username = user.BackendPrincipalId, password });
        try
        {
            var info = await StoreWithinTransactionAsync(db, user.Id,
                BackendCredentialScope.SubsonicPurpose, bytes, existing, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(info.Id, info.UpdatedAt);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task RevokeSubsonicPlaylistGrantAsync(
        AllstarrPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (principal.BackendType != "subsonic") throw new UnauthorizedAccessException();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await RequireSubsonicUserAsync(db, principal.UserId,
            principal.BackendInstanceId, principal.BackendPrincipalId, cancellationToken);
        var grants = await PlaylistGrants(db, user).ToListAsync(cancellationToken);
        foreach (var grant in grants)
        {
            grant.RevokedAt = _clock.UtcNow;
            grant.UpdatedAt = _clock.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SecretLease> OpenSubsonicPlaylistCredentialAsync(
        string backendInstanceId, string principalId, Guid? referenceId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await RequireSubsonicUserAsync(db, null, backendInstanceId, principalId, cancellationToken);
        var grants = PlaylistGrants(db, user);
        if (referenceId.HasValue) grants = grants.Where(item => item.Id == referenceId.Value);
        var reference = await grants.OrderByDescending(item => item.UpdatedAt)
            .Select(item => (Guid?)item.Id).FirstOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException("Playlist management consent is required for this listener.");
        return await OpenAsync(reference, new SecretAccessContext(user.Id, BackendCredentialScope.SubsonicPurpose), cancellationToken);
    }

    private static IQueryable<SecretReferenceRecord> PlaylistGrants(AllstarrDbContext db, UserRecord user) =>
        db.SecretReferences.Where(item => item.UserId == user.Id && item.Purpose == BackendCredentialScope.SubsonicPurpose &&
            item.RevokedAt == null);

    private static async Task<UserRecord> RequireSubsonicUserAsync(
        AllstarrDbContext db, Guid? userId, string backendInstanceId, string principalId,
        CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Enabled &&
            (!userId.HasValue || item.Id == userId.Value) && item.BackendType == "subsonic" &&
            item.BackendInstanceId == backendInstanceId && item.BackendPrincipalId == principalId, cancellationToken)
        ?? throw new UnauthorizedAccessException("The listener's backend identity is unavailable.");

    public async Task<SecretLease> OpenAsync(
        Guid referenceId,
        SecretAccessContext access,
        CancellationToken cancellationToken = default)
    {
        var keyRing = await _keyRingProvider.LoadAsync(cancellationToken);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var reference = await context.SecretReferences
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == referenceId, cancellationToken)
                ?? throw new KeyNotFoundException("Secret reference not found.");
            EnsureOwnerMatch(reference, access);
            if (reference.RevokedAt.HasValue)
            {
                throw new InvalidOperationException("Secret reference is revoked.");
            }

            var version = await context.SecretVersions
                .AsNoTracking()
                .SingleAsync(
                    item => item.SecretReferenceId == reference.Id &&
                            item.Version == reference.ActiveVersion,
                    cancellationToken);
            var plaintext = Decrypt(
                version,
                keyRing.GetKey(version.KeyId),
                AssociatedData(reference, version.Version));
            return new SecretLease(plaintext);
        }
        finally
        {
            ClearKeyRing(keyRing);
        }
    }

    public async Task RebindOwnerWithinTransactionAsync(
        AllstarrDbContext context,
        Guid referenceId,
        SecretAccessContext currentAccess,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Database.CurrentTransaction == null)
            throw new InvalidOperationException("An active database transaction is required.");

        var reference = await context.SecretReferences.SingleAsync(
            item => item.Id == referenceId, cancellationToken);
        EnsureOwnerMatch(reference, currentAccess);
        if (reference.UserId == userId) return;

        var keyRing = await _keyRingProvider.LoadAsync(cancellationToken);
        byte[]? plaintext = null;
        try
        {
            var current = await context.SecretVersions.SingleAsync(
                item => item.SecretReferenceId == reference.Id &&
                        item.Version == reference.ActiveVersion,
                cancellationToken);
            plaintext = Decrypt(
                current,
                keyRing.GetKey(current.KeyId),
                AssociatedData(reference, current.Version));
            var now = _clock.UtcNow;
            current.RetiredAt = now;
            reference.UserId = userId;
            reference.ActiveVersion++;
            reference.UpdatedAt = now;
            var encrypted = Encrypt(
                plaintext,
                keyRing.GetActiveKey(),
                AssociatedData(reference, reference.ActiveVersion));
            context.SecretVersions.Add(new SecretVersionRecord
            {
                Id = Guid.CreateVersion7(),
                SecretReferenceId = reference.Id,
                Version = reference.ActiveVersion,
                KeyId = keyRing.ActiveKeyId,
                Nonce = encrypted.Nonce,
                Ciphertext = encrypted.Ciphertext,
                AuthenticationTag = encrypted.Tag,
                CreatedAt = now
            });
        }
        finally
        {
            if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
            ClearKeyRing(keyRing);
        }
    }

    public async Task<SecretReferenceInfo> RotateEncryptionAsync(
        Guid referenceId,
        SecretAccessContext access,
        CancellationToken cancellationToken = default)
    {
        string purpose;
        Guid? userId;
        using var lease = await OpenAsync(referenceId, access, cancellationToken);
        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var reference = await context.SecretReferences.AsNoTracking()
                .SingleAsync(item => item.Id == referenceId, cancellationToken);
            purpose = reference.Purpose;
            userId = reference.UserId;
        }

        return await StoreAsync(
            userId,
            purpose,
            lease.Value,
            referenceId,
            cancellationToken);
    }

    public async Task<SecretRotationResult> RotateAllEncryptionAsync(
        CancellationToken cancellationToken = default)
    {
        var keyRing = await _keyRingProvider.LoadAsync(cancellationToken);
        string activeKeyId;
        try
        {
            activeKeyId = keyRing.ActiveKeyId;
        }
        finally
        {
            ClearKeyRing(keyRing);
        }

        List<(Guid Id, Guid? UserId, string Purpose, string KeyId)> references;
        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var rows = await context.SecretReferences.AsNoTracking()
                .Where(item => item.RevokedAt == null)
                .Join(
                    context.SecretVersions.AsNoTracking(),
                    reference => new { ReferenceId = reference.Id, Version = reference.ActiveVersion },
                    version => new { ReferenceId = version.SecretReferenceId, version.Version },
                    (reference, version) => new
                    {
                        reference.Id,
                        reference.UserId,
                        reference.Purpose,
                        version.KeyId
                    })
                .ToListAsync(cancellationToken);
            references = rows
                .Select(item => (item.Id, item.UserId, item.Purpose, item.KeyId))
                .ToList();
        }

        var rotated = 0;
        foreach (var reference in references.Where(item => item.KeyId != activeKeyId))
        {
            await RotateEncryptionAsync(
                reference.Id,
                new SecretAccessContext(reference.UserId, reference.Purpose, AllowShared: reference.UserId == null),
                cancellationToken);
            rotated++;
        }

        return new SecretRotationResult(
            activeKeyId,
            references.Count,
            rotated,
            references.Count - rotated);
    }

    public async Task RevokeAsync(
        Guid referenceId,
        SecretAccessContext access,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var reference = await context.SecretReferences.SingleOrDefaultAsync(
                            item => item.Id == referenceId,
                            cancellationToken)
                        ?? throw new KeyNotFoundException("Secret reference not found.");
        EnsureOwnerMatch(reference, access);
        reference.RevokedAt ??= _clock.UtcNow;
        reference.UpdatedAt = _clock.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }

    private static void EnsureOwnerMatch(
        SecretReferenceRecord reference,
        SecretAccessContext access)
    {
        if (!string.Equals(reference.Purpose, access.Purpose, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Secret purpose does not match the requested operation.");
        if (reference.UserId.HasValue)
        {
            if (access.UserId != reference.UserId)
            {
                throw new UnauthorizedAccessException("Secret reference is outside the requested user.");
            }

            return;
        }

        if (!access.AllowShared)
        {
            throw new UnauthorizedAccessException("Shared secret access is not allowed for this caller.");
        }
    }

    private static byte[] AssociatedData(SecretReferenceRecord reference, int version) =>
        Encoding.UTF8.GetBytes(
            $"allstarr-secret-v2|{reference.Id:N}|{reference.UserId?.ToString("N") ?? "shared"}|" +
            $"{version}|{reference.Purpose}");

    private static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Encrypt(
        byte[] plaintext,
        byte[] key,
        byte[] associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        CryptographicOperations.ZeroMemory(associatedData);
        return (nonce, ciphertext, tag);
    }

    private static byte[] Decrypt(
        SecretVersionRecord version,
        byte[] key,
        byte[] associatedData)
    {
        var plaintext = new byte[version.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                version.Nonce,
                version.Ciphertext,
                version.AuthenticationTag,
                plaintext,
                associatedData);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private static SecretReferenceInfo ToInfo(
        SecretReferenceRecord reference,
        SecretVersionRecord version) => new(
        reference.Id,
        reference.UserId,
        reference.Purpose,
        reference.ActiveVersion,
        version.KeyId,
        reference.UpdatedAt,
        reference.RevokedAt.HasValue);

    private static void ClearKeyRing(SecretKeyRing keyRing)
    {
        foreach (var key in keyRing.Keys.Values)
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
