using allstarr.Core.Storage;

namespace allstarr.Core.Capabilities;

public enum ProviderActorKind
{
    User,
    Administrator,
    SystemJob
}

public sealed record ProviderBackendPrincipal
{
    public ProviderBackendPrincipal(string backendType, string backendInstanceId, string principalId)
    {
        BackendType = ProviderContractValidation.Catalog(backendType, nameof(backendType));
        BackendInstanceId = ProviderContractValidation.RequiredText(
            backendInstanceId,
            nameof(backendInstanceId),
            200);
        PrincipalId = ProviderContractValidation.RequiredText(principalId, nameof(principalId), 300);
    }

    public string BackendType { get; }

    public string BackendInstanceId { get; }

    public string PrincipalId { get; }
}

public sealed record ProviderActorContext
{
    public ProviderActorContext(
        Guid tenantId,
        ProviderActorKind kind,
        Guid? userId,
        ProviderBackendPrincipal? backendPrincipal = null,
        Guid? durableJobId = null,
        Guid? actingForUserId = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant ID is required.", nameof(tenantId));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (kind is ProviderActorKind.User or ProviderActorKind.Administrator &&
            (!userId.HasValue || userId == Guid.Empty || backendPrincipal == null))
        {
            throw new ArgumentException(
                "User and administrator actors require both a user ID and backend principal.",
                nameof(userId));
        }

        if (kind == ProviderActorKind.SystemJob &&
            (!durableJobId.HasValue || durableJobId == Guid.Empty))
        {
            throw new ArgumentException("System actors require a durable job ID.", nameof(durableJobId));
        }

        if (actingForUserId == Guid.Empty ||
            kind == ProviderActorKind.User && actingForUserId.HasValue)
        {
            throw new ArgumentException(
                "Only administrators or scoped system jobs may act for another user.",
                nameof(actingForUserId));
        }

        TenantId = tenantId;
        Kind = kind;
        UserId = userId;
        BackendPrincipal = backendPrincipal;
        DurableJobId = durableJobId;
        ActingForUserId = actingForUserId;
    }

    public Guid TenantId { get; }

    public ProviderActorKind Kind { get; }

    public Guid? UserId { get; }

    public ProviderBackendPrincipal? BackendPrincipal { get; }

    public Guid? DurableJobId { get; }

    public Guid? ActingForUserId { get; }

    public Guid? EffectiveUserId => ActingForUserId ?? UserId;
}

public sealed record ProviderAccountContext
{
    public ProviderAccountContext(
        Guid accountId,
        string providerId,
        ProviderAccountScope scope,
        long revision,
        bool enabled = true,
        Guid? tenantId = null,
        Guid? ownerUserId = null,
        string? libraryScopeId = null,
        string resolutionReason = "selected-account",
        Guid? secretReferenceId = null)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("A provider account ID is required.", nameof(accountId));
        }

        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        libraryScopeId = ProviderContractValidation.OptionalText(
            libraryScopeId,
            nameof(libraryScopeId),
            300);
        var validScope = scope switch
        {
            ProviderAccountScope.Global =>
                tenantId == null && ownerUserId == null && libraryScopeId == null,
            ProviderAccountScope.User =>
                tenantId is { } tenant && tenant != Guid.Empty &&
                ownerUserId is { } owner && owner != Guid.Empty &&
                libraryScopeId == null,
            ProviderAccountScope.Library =>
                tenantId is { } tenant && tenant != Guid.Empty &&
                ownerUserId == null && libraryScopeId != null,
            _ => false
        };
        if (!validScope)
        {
            throw new ArgumentException(
                "Provider account tenant, owner, and library fields must match its declared scope.",
                nameof(scope));
        }

        AccountId = accountId;
        ProviderId = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
        Scope = scope;
        Revision = revision;
        Enabled = enabled;
        TenantId = tenantId;
        OwnerUserId = ownerUserId;
        LibraryScopeId = libraryScopeId;
        ResolutionReason = ProviderContractValidation.Catalog(
            resolutionReason,
            nameof(resolutionReason));
        if (secretReferenceId == Guid.Empty)
        {
            throw new ArgumentException("Secret reference IDs cannot be empty.", nameof(secretReferenceId));
        }
        SecretReferenceId = secretReferenceId;
    }

    public Guid AccountId { get; }

    public string ProviderId { get; }

    public ProviderAccountScope Scope { get; }

    public long Revision { get; }

    public bool Enabled { get; }

    public Guid? TenantId { get; }

    public Guid? OwnerUserId { get; }

    public string? LibraryScopeId { get; }

    public string ResolutionReason { get; }

    public Guid? SecretReferenceId { get; }
}

public sealed record ProviderLibraryContext
{
    public ProviderLibraryContext(Guid tenantId, string scopeId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant ID is required.", nameof(tenantId));
        }

        TenantId = tenantId;
        ScopeId = ProviderContractValidation.RequiredText(scopeId, nameof(scopeId), 300);
    }

    public Guid TenantId { get; }

    public string ScopeId { get; }
}

public enum ProviderExplicitContentPolicy
{
    Allow,
    PreferClean,
    CleanOnly
}

public enum ProviderAudioQuality
{
    Any,
    DataSaver,
    Lossy,
    Lossless,
    HighResolution
}

public sealed record ProviderQualityPolicy
{
    public ProviderQualityPolicy(
        ProviderAudioQuality minimum,
        ProviderAudioQuality maximum,
        bool allowTranscode)
    {
        if (!Enum.IsDefined(minimum) || !Enum.IsDefined(maximum) || minimum > maximum)
        {
            throw new ArgumentException("The provider quality range is invalid.");
        }

        Minimum = minimum;
        Maximum = maximum;
        AllowTranscode = allowTranscode;
    }

    public ProviderAudioQuality Minimum { get; }

    public ProviderAudioQuality Maximum { get; }

    public bool AllowTranscode { get; }
}

public sealed record ProviderExecutionPolicy
{
    public ProviderExecutionPolicy(
        ProviderQualityPolicy quality,
        ProviderExplicitContentPolicy explicitContent,
        bool allowFallback,
        bool allowSharedAccount,
        bool allowManagedDownloads,
        IEnumerable<string>? allowedProviderIds = null)
    {
        ArgumentNullException.ThrowIfNull(quality);
        if (!Enum.IsDefined(explicitContent))
        {
            throw new ArgumentOutOfRangeException(nameof(explicitContent));
        }

        var providers = (allowedProviderIds ?? [])
            .Select(item => ProviderContractValidation.ProviderId(item, nameof(allowedProviderIds)))
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        if (providers.Distinct(StringComparer.Ordinal).Count() != providers.Length)
        {
            throw new ArgumentException("Allowed provider IDs cannot contain duplicates.", nameof(allowedProviderIds));
        }

        Quality = quality;
        ExplicitContent = explicitContent;
        AllowFallback = allowFallback;
        AllowSharedAccount = allowSharedAccount;
        AllowManagedDownloads = allowManagedDownloads;
        AllowedProviderIds = Array.AsReadOnly(providers);
    }

    public ProviderQualityPolicy Quality { get; }

    public ProviderExplicitContentPolicy ExplicitContent { get; }

    public bool AllowFallback { get; }

    public bool AllowSharedAccount { get; }

    public bool AllowManagedDownloads { get; }

    public IReadOnlyList<string> AllowedProviderIds { get; }

    public bool AllowsProvider(string providerId)
    {
        var candidate = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
        return AllowedProviderIds.Count == 0 || AllowedProviderIds.Contains(candidate, StringComparer.Ordinal);
    }
}

public sealed record ProviderExecutionContext
{
    public ProviderExecutionContext(
        ProviderActorContext actor,
        string providerId,
        ProviderAccountContext? account,
        ProviderLibraryContext? library,
        ProviderExecutionPolicy policy,
        string operationId,
        string correlationId,
        DateTimeOffset deadline,
        CancellationToken cancellationToken,
        string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(policy);
        providerId = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
        if (!policy.AllowsProvider(providerId))
        {
            throw new UnauthorizedAccessException("The execution policy does not allow the selected provider.");
        }

        if (account != null)
        {
            if (!account.Enabled)
            {
                throw new UnauthorizedAccessException("The selected provider account is disabled.");
            }

            if (!account.ProviderId.Equals(providerId, StringComparison.Ordinal))
            {
                throw new ArgumentException("The provider account belongs to another provider.", nameof(account));
            }

            if (account.Scope == ProviderAccountScope.Global && !policy.AllowSharedAccount)
            {
                throw new UnauthorizedAccessException("The execution policy does not allow a shared account.");
            }

            if (account.Scope != ProviderAccountScope.Global && account.TenantId != actor.TenantId)
            {
                throw new UnauthorizedAccessException("The provider account belongs to another tenant.");
            }

            if (account.Scope == ProviderAccountScope.User && account.OwnerUserId != actor.EffectiveUserId)
            {
                throw new UnauthorizedAccessException("The provider account belongs to another user.");
            }
        }

        if (library != null)
        {
            if (library.TenantId != actor.TenantId)
            {
                throw new UnauthorizedAccessException("The library belongs to another tenant.");
            }

            if (account?.Scope == ProviderAccountScope.Library &&
                !account.LibraryScopeId!.Equals(library.ScopeId, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("The provider account belongs to another library.");
            }
        }
        else if (account?.Scope == ProviderAccountScope.Library)
        {
            throw new ArgumentException("A library-scoped account requires library context.", nameof(library));
        }

        if (deadline == default)
        {
            throw new ArgumentException("A provider operation deadline is required.", nameof(deadline));
        }

        Actor = actor;
        ProviderId = providerId;
        Account = account;
        Library = library;
        Policy = policy;
        OperationId = ProviderContractValidation.RequiredText(operationId, nameof(operationId), 100);
        CorrelationId = ProviderContractValidation.RequiredText(correlationId, nameof(correlationId), 100);
        Deadline = deadline;
        CancellationToken = cancellationToken;
        IdempotencyKey = ProviderContractValidation.OptionalText(
            idempotencyKey,
            nameof(idempotencyKey),
            300);
    }

    public ProviderActorContext Actor { get; }

    public string ProviderId { get; }

    public ProviderAccountContext? Account { get; }

    public ProviderLibraryContext? Library { get; }

    public ProviderExecutionPolicy Policy { get; }

    public string OperationId { get; }

    public string CorrelationId { get; }

    public DateTimeOffset Deadline { get; }

    public CancellationToken CancellationToken { get; }

    public string? IdempotencyKey { get; }

    public bool IsExpired(DateTimeOffset now) => now >= Deadline;

    public TimeSpan Remaining(DateTimeOffset now) => Deadline <= now ? TimeSpan.Zero : Deadline - now;

    public string RequireIdempotencyKey() =>
        IdempotencyKey ?? throw new InvalidOperationException(
            "This provider operation requires an idempotency key.");

    public void RequireResourceOwner(
        ProviderExternalResourceId resourceId,
        ProviderResourceKind resourceKind)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        resourceId.RequireOwner(ProviderId, resourceKind);
    }
}
