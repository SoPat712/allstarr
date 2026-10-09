using System.Security.Cryptography;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Intelligence;

public sealed record ListeningIntakeTokenInfo(
    Guid Id,
    bool RelayExternally,
    DateTimeOffset CreatedAt);

public sealed record ListeningIntakeTokenCreated(
    Guid Id,
    string Token,
    bool RelayExternally,
    DateTimeOffset CreatedAt);

public sealed record ListeningIntakeGrant(
    Guid TokenId,
    IntelligenceScope Scope,
    bool RelayExternally,
    ProtocolKind Protocol,
    AllstarrPrincipal Principal);

public sealed class ListeningIntakeTokenService(
    IDbContextFactory<AllstarrDbContext> factory,
    EncryptedSecretStore secrets,
    IPlatformClock clock)
{
    private const string Prefix = "als";
    private const string Purpose = "listening-intake-token";

    public async Task<IReadOnlyList<ListeningIntakeTokenInfo>> ListAsync(
        IntelligenceScope scope,
        CancellationToken cancellationToken = default)
    {
        IntelligencePolicyService.ValidateScope(scope);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await Query(db, scope).AsNoTracking().Where(item => item.RevokedAt == null)
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new ListeningIntakeTokenInfo(item.Id, item.RelayExternally, item.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<ListeningIntakeTokenCreated> CreateAsync(
        IntelligenceScope scope,
        bool relayExternally,
        CancellationToken cancellationToken = default)
    {
        IntelligencePolicyService.ValidateScope(scope);
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            var id = Guid.CreateVersion7();
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            if (!await IntelligencePolicyService.OwnsBackendAsync(db, scope, cancellationToken))
                throw new UnauthorizedAccessException("The intake user is unavailable for this backend.");
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var reference = await secrets.StoreWithinTransactionAsync(
                db, scope.OwnerUserId, Purpose, secret, cancellationToken: cancellationToken);
            var createdAt = clock.UtcNow;
            db.ListeningIntakeTokens.Add(new ListeningIntakeTokenRecord
            {
                Id = id,
                OwnerUserId = scope.OwnerUserId,
                Protocol = scope.Protocol,
                BackendInstanceId = scope.BackendInstanceId,
                SecretReferenceId = reference.Id,
                RelayExternally = relayExternally,
                CreatedAt = createdAt
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(id, Format(id, secret), relayExternally, createdAt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public async Task<bool> RevokeAsync(
        IntelligenceScope scope,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        IntelligencePolicyService.ValidateScope(scope);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var record = await Query(db, scope).SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (record == null) return false;
        var reference = await db.SecretReferences.SingleAsync(item =>
            item.Id == record.SecretReferenceId && item.UserId == scope.OwnerUserId && item.Purpose == Purpose, cancellationToken);
        var now = clock.UtcNow;
        record.RevokedAt ??= now;
        reference.RevokedAt ??= now;
        reference.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ListeningIntakeGrant?> AuthorizeAsync(
        string? token,
        CancellationToken cancellationToken = default)
    {
        if (!TryParse(token, out var id, out var supplied)) return null;
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var record = await db.ListeningIntakeTokens.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id && item.RevokedAt == null, cancellationToken);
            if (record == null ||
                !Enum.TryParse<ProtocolKind>(record.Protocol, true, out var protocol) ||
                protocol == ProtocolKind.Unknown)
                return null;
            var scope = new IntelligenceScope(record.OwnerUserId, record.Protocol, record.BackendInstanceId);
            var enabled = await IntelligencePolicyService.Query(db, scope).AsNoTracking()
                .AnyAsync(item => item.Enabled, cancellationToken);
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item =>
                item.Id == record.OwnerUserId && item.Enabled && item.BackendType == record.Protocol &&
                item.BackendInstanceId == record.BackendInstanceId, cancellationToken);
            if (!enabled || user == null) return null;
            using var stored = await secrets.OpenAsync(record.SecretReferenceId,
                new(record.OwnerUserId, Purpose), cancellationToken);
            if (stored.Value.Length != supplied.Length ||
                !CryptographicOperations.FixedTimeEquals(stored.Value.Span, supplied))
                return null;
            return new(id, scope, record.RelayExternally, protocol, new(
                record.OwnerUserId, record.Protocol, record.BackendInstanceId,
                user.BackendPrincipalId, user.DisplayName, user.IsAdmin));
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(supplied);
        }
    }

    internal static string Format(Guid id, ReadOnlySpan<byte> secret) =>
        $"{Prefix}_{id:N}_{Convert.ToHexStringLower(secret)}";

    internal static bool TryParse(string? token, out Guid id, out byte[] secret)
    {
        id = default;
        secret = [];
        if (token == null || token.Length != 101 || !token.StartsWith($"{Prefix}_", StringComparison.Ordinal))
            return false;
        if (!Guid.TryParseExact(token.AsSpan(4, 32), "N", out id)) return false;
        try
        {
            secret = Convert.FromHexString(token[37..]);
            return secret.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static IQueryable<ListeningIntakeTokenRecord> Query(AllstarrDbContext db, IntelligenceScope scope) =>
        db.ListeningIntakeTokens.Where(item =>
            item.OwnerUserId == scope.OwnerUserId &&
            item.Protocol == scope.Protocol && item.BackendInstanceId == scope.BackendInstanceId &&
            db.Users.Any(user => user.Id == scope.OwnerUserId && user.Enabled &&
                user.BackendType == scope.Protocol && user.BackendInstanceId == scope.BackendInstanceId));
}
