using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Jobs;

public sealed record DurableJobPolicySnapshot(
    int Version,
    string AuthorizationRule,
    string? ProviderId,
    string? Capability,
    string? ProviderAccountScope,
    long? ProviderAccountRevision = null);

public sealed record DurableJobSavedContext(
    Guid OwnerUserId,
    Guid? ProviderAccountId,
    string? ProviderCapability,
    string CorrelationId,
    string PolicySnapshotJson);

public sealed record DurableJobContextAuthorization(
    bool Authorized,
    string? ErrorCode = null,
    string? SafeMessage = null)
{
    public static DurableJobContextAuthorization Allow() => new(true);

    public static DurableJobContextAuthorization Deny(string errorCode, string safeMessage) =>
        new(false, errorCode, safeMessage);
}

/// <summary>
/// Validates and snapshots the identity/account scope attached to durable work. The exact saved account is
/// checked again before execution; this service never asks the provider resolver for a replacement account.
/// </summary>
public sealed class DurableJobContextAuthorizer
{
    private const int SnapshotVersion = 2;

    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly IProviderRegistry? _providers;

    public DurableJobContextAuthorizer(IDbContextFactory<AllstarrDbContext> contextFactory,
        IProviderRegistry? providers = null)
    {
        _contextFactory = contextFactory;
        _providers = providers;
    }

    public async Task<DurableJobSavedContext> AuthorizeEnqueueAsync(
        Guid? ownerUserId,
        Guid? providerAccountId,
        string? capability,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        if (!ownerUserId.HasValue || ownerUserId == Guid.Empty)
        {
            throw new ArgumentException(
                "Durable jobs require the initiating user.");
        }

        var normalizedCapability = NormalizeCapability(capability, providerAccountId.HasValue);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var userIsActive = await context.Users.AsNoTracking().AnyAsync(
            item => item.Id == ownerUserId.Value &&
                    item.Enabled,
            cancellationToken);
        if (!userIsActive)
        {
            throw new UnauthorizedAccessException(
                "The initiating user is missing or disabled.");
        }

        ProviderAccountRecord? account = null;
        if (providerAccountId.HasValue)
        {
            if (providerAccountId == Guid.Empty)
            {
                throw new ArgumentException("Provider account ID cannot be empty.", nameof(providerAccountId));
            }

            account = await context.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == providerAccountId.Value,
                cancellationToken);
            if (account == null ||
                !await IsExactAccountAuthorizedAsync(
                    context,
                    account,
                    ownerUserId.Value,
                    normalizedCapability!,
                    cancellationToken))
            {
                throw new UnauthorizedAccessException(
                    "The selected provider account is unavailable or outside the initiating context.");
            }
        }

        var snapshot = BuildSnapshot(account, normalizedCapability);
        return new DurableJobSavedContext(
            ownerUserId.Value,
            providerAccountId,
            normalizedCapability,
            RedactCorrelationId(correlationId),
            JsonSerializer.Serialize(snapshot));
    }

    public async Task<DurableJobContextAuthorization> ReauthorizeAsync(
        DurableJobClaim claim,
        CancellationToken cancellationToken = default)
    {
        if (!claim.OwnerUserId.HasValue || claim.OwnerUserId == Guid.Empty)
        {
            return DurableJobContextAuthorization.Deny(
                "job_context_missing",
                "The durable job does not contain an initiating user.");
        }

        DurableJobPolicySnapshot? savedSnapshot;
        try
        {
            savedSnapshot = claim.PolicySnapshot.Deserialize<DurableJobPolicySnapshot>();
        }
        catch (JsonException)
        {
            savedSnapshot = null;
        }

        if (savedSnapshot == null || savedSnapshot.Version != SnapshotVersion)
        {
            return DurableJobContextAuthorization.Deny(
                "job_policy_snapshot_invalid",
                "The durable job policy snapshot is missing or unsupported.");
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var userIsActive = await context.Users.AsNoTracking().AnyAsync(
            item => item.Id == claim.OwnerUserId.Value &&
                    item.Enabled,
            cancellationToken);
        if (!userIsActive)
        {
            return DurableJobContextAuthorization.Deny(
                "job_initiator_unauthorized",
                "The initiating user is no longer authorized for this durable job.");
        }

        if (!claim.ProviderAccountId.HasValue)
        {
            var expected = BuildSnapshot(null, null);
            return claim.ProviderCapability == null && savedSnapshot == expected
                ? DurableJobContextAuthorization.Allow()
                : DurableJobContextAuthorization.Deny(
                    "job_policy_snapshot_mismatch",
                    "The durable job policy snapshot does not match its saved execution context.");
        }

        if (string.IsNullOrWhiteSpace(claim.ProviderCapability) ||
            !string.Equals(
                claim.ProviderCapability,
                savedSnapshot.Capability,
                StringComparison.Ordinal))
        {
            return DurableJobContextAuthorization.Deny(
                "job_policy_snapshot_invalid",
                "The provider-bound durable job has no saved capability policy.");
        }

        var account = await context.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == claim.ProviderAccountId.Value,
            cancellationToken);
        if (account == null ||
            !await IsExactAccountAuthorizedAsync(
                context,
                account,
                claim.OwnerUserId.Value,
                claim.ProviderCapability,
                cancellationToken))
        {
            return DurableJobContextAuthorization.Deny(
                "job_provider_account_unauthorized",
                "The saved provider account is no longer authorized for this durable job.");
        }

        var currentSnapshot = BuildSnapshot(account, claim.ProviderCapability);
        return savedSnapshot == currentSnapshot
            ? DurableJobContextAuthorization.Allow()
            : DurableJobContextAuthorization.Deny(
                "job_policy_snapshot_mismatch",
                "The saved provider policy no longer authorizes this exact execution context.");
    }

    private DurableJobPolicySnapshot BuildSnapshot(
        ProviderAccountRecord? account,
        string? capability)
    {
        if (account == null)
        {
            return new DurableJobPolicySnapshot(
                SnapshotVersion,
                "initiator_only",
                null,
                null,
                null);
        }

        var authorizationRule = account.OwnerUserId.HasValue ? "personal_account" : "shared_account";
        return new DurableJobPolicySnapshot(
            SnapshotVersion,
            authorizationRule,
            account.ProviderId.Trim().ToLowerInvariant(),
            capability,
            account.Scope.ToString().ToLowerInvariant(),
            account.Revision);
    }

    private async Task<bool> IsExactAccountAuthorizedAsync(
        AllstarrDbContext context,
        ProviderAccountRecord account,
        Guid ownerUserId,
        string capability,
        CancellationToken cancellationToken)
    {
        if (!account.Enabled || !Enum.TryParse<ProviderCapabilityKind>(capability, true, out var kind) ||
            !Enum.IsDefined(kind) || capability != kind.ToString().ToLowerInvariant())
        {
            return false;
        }

        var owned = account.OwnerUserId == null || account.OwnerUserId == ownerUserId;
        if (!owned || _providers != null && (!_providers.TryGet(account.ProviderId, out var provider) ||
                provider?.Capabilities.Any(item => item.Capability == kind && item.HasUsableImplementation &&
                    item.AllowedAccountScopes.Contains(account.Scope)) != true))
            return false;

        if (account.SecretReferenceId is not { } secretId) return true;
        var purpose = $"provider-account:{account.ProviderId}:{account.Id:N}";
        return await context.SecretReferences.AsNoTracking().AnyAsync(item => item.Id == secretId &&
            item.UserId == account.OwnerUserId &&
            item.Purpose == purpose && item.RevokedAt == null, cancellationToken);
    }

    private static string? NormalizeCapability(string? value, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                throw new ArgumentException(
                    "Provider-bound durable jobs require a capability.",
                    nameof(value));
            }

            return null;
        }

        if (!required)
        {
            throw new ArgumentException(
                "A job capability cannot be saved without an exact provider account.",
                nameof(value));
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Capability must be at most 100 characters.");
        }

        return normalized;
    }

    internal static string RedactCorrelationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Guid.CreateVersion7().ToString("N");
        }

        var normalized = value.Trim();
        if (normalized.Length <= 100 && normalized.All(IsSafeCorrelationCharacter))
        {
            return normalized;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"redacted-{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private static bool IsSafeCorrelationCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or ':' or '-';
}
