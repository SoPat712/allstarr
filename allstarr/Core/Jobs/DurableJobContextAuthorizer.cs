using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Jobs;

public sealed record DurableJobPolicySnapshot(
    int Version,
    string AuthorizationRule,
    string? ProviderId,
    string? Capability,
    string? ProviderAccountScope);

public sealed record DurableJobSavedContext(
    Guid TenantId,
    Guid OwnerUserId,
    Guid? ProviderAccountId,
    string? LibraryScopeId,
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
    private const int SnapshotVersion = 1;

    private static readonly IReadOnlySet<string> PersonalCapabilities =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "playlist",
            "personal-library",
            "scrobbling",
            "favorites"
        };

    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly ProviderPolicyOptions _providerPolicy;

    public DurableJobContextAuthorizer(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        ProviderPolicyOptions providerPolicy)
    {
        _contextFactory = contextFactory;
        _providerPolicy = providerPolicy;
    }

    public async Task<DurableJobSavedContext> AuthorizeEnqueueAsync(
        Guid? tenantId,
        Guid? ownerUserId,
        Guid? providerAccountId,
        string? libraryScopeId,
        string? capability,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        if (!tenantId.HasValue || tenantId == Guid.Empty ||
            !ownerUserId.HasValue || ownerUserId == Guid.Empty)
        {
            throw new ArgumentException(
                "Durable jobs require the initiating tenant and user.");
        }

        var normalizedLibraryScope = NormalizeLibraryScope(libraryScopeId);
        var normalizedCapability = NormalizeCapability(capability, providerAccountId.HasValue);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var userIsActive = await context.Users.AsNoTracking().AnyAsync(
            item => item.Id == ownerUserId.Value &&
                    item.TenantId == tenantId.Value &&
                    item.Status == PlatformUserStatus.Active,
            cancellationToken);
        if (!userIsActive)
        {
            throw new UnauthorizedAccessException(
                "The initiating user is missing, disabled, or outside the tenant scope.");
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
                !IsExactAccountAuthorized(
                    account,
                    tenantId.Value,
                    ownerUserId.Value,
                    normalizedLibraryScope,
                    normalizedCapability!))
            {
                throw new UnauthorizedAccessException(
                    "The selected provider account is unavailable or outside the initiating context.");
            }
        }

        var snapshot = BuildSnapshot(account, normalizedCapability);
        return new DurableJobSavedContext(
            tenantId.Value,
            ownerUserId.Value,
            providerAccountId,
            normalizedLibraryScope,
            normalizedCapability,
            RedactCorrelationId(correlationId),
            JsonSerializer.Serialize(snapshot));
    }

    public async Task<DurableJobContextAuthorization> ReauthorizeAsync(
        DurableJobClaim claim,
        CancellationToken cancellationToken = default)
    {
        if (!claim.TenantId.HasValue || !claim.OwnerUserId.HasValue)
        {
            return DurableJobContextAuthorization.Deny(
                "job_context_missing",
                "The durable job does not contain an initiating tenant and user.");
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
                    item.TenantId == claim.TenantId.Value &&
                    item.Status == PlatformUserStatus.Active,
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
            !IsExactAccountAuthorized(
                account,
                claim.TenantId.Value,
                claim.OwnerUserId.Value,
                claim.LibraryScopeId,
                claim.ProviderCapability))
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

        var authorizationRule = account.Scope switch
        {
            ProviderAccountScope.User => "user_account",
            ProviderAccountScope.Library => "library_account",
            ProviderAccountScope.Global when
                capability!.Equals("download", StringComparison.OrdinalIgnoreCase) &&
                _providerPolicy.SharedDownloaderAccountId == account.Id => "policy_shared_downloader",
            ProviderAccountScope.Global => "global_account",
            _ => throw new InvalidOperationException("Unsupported provider account scope.")
        };
        return new DurableJobPolicySnapshot(
            SnapshotVersion,
            authorizationRule,
            account.ProviderId.Trim().ToLowerInvariant(),
            capability,
            account.Scope.ToString().ToLowerInvariant());
    }

    private bool IsExactAccountAuthorized(
        ProviderAccountRecord account,
        Guid tenantId,
        Guid ownerUserId,
        string? libraryScopeId,
        string capability)
    {
        if (!account.Enabled)
        {
            return false;
        }

        return account.Scope switch
        {
            ProviderAccountScope.User =>
                account.TenantId == tenantId && account.OwnerUserId == ownerUserId,
            ProviderAccountScope.Library =>
                account.TenantId == tenantId &&
                !string.IsNullOrWhiteSpace(libraryScopeId) &&
                string.Equals(account.LibraryScopeId, libraryScopeId, StringComparison.Ordinal),
            ProviderAccountScope.Global =>
                account.TenantId == null &&
                _providerPolicy.AllowGlobalAccounts &&
                (!PersonalCapabilities.Contains(capability) ||
                 _providerPolicy.AllowGlobalPersonalAccounts),
            _ => false
        };
    }

    private static string? NormalizeLibraryScope(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > 300)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Library scope must be at most 300 characters.");
        }

        return normalized;
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
