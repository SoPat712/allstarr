using allstarr.Core.Capabilities;
using allstarr.Core.Storage;

namespace allstarr.Core.Routing;

public sealed record ProviderRouteAccountRequest(
    ProviderActorContext Actor,
    string ProviderId,
    ProviderCapabilityKind Capability,
    Guid? RequestedAccountId,
    string? LibraryScopeId);

public sealed record ProviderRouteAccountResolution(
    ProviderAccountContext Account,
    long CurrentRevision);

public interface IProviderRouteAccountResolver
{
    Task<ProviderRouteAccountResolution?> ResolveAsync(
        ProviderRouteAccountRequest request,
        CancellationToken cancellationToken = default);
}

public enum ProviderRouteHealthState
{
    Unknown,
    Healthy,
    Degraded,
    Unavailable,
    Unauthorized
}

public sealed record ProviderRouteHealthSnapshot(
    ProviderRouteHealthState State,
    bool CircuitOpen);

public interface IProviderRouteHealthSource
{
    ProviderRouteHealthSnapshot Get(
        string providerId,
        Guid providerAccountId,
        ProviderCapabilityKind capability);
}

public interface IProviderRouteSidecarSource
{
    bool IsReady(string dependencyId);
}

public sealed record ProviderRouteProviderState
{
    public ProviderRouteProviderState(
        string providerId,
        bool capabilityEnabled = true,
        Guid? requestedAccountId = null,
        long? expectedAccountRevision = null,
        IEnumerable<ProviderAudioQuality>? availableQualities = null,
        string? trackCatalog = null,
        bool? isExplicit = null,
        bool rateLimitBudgetAvailable = true,
        bool storageCapacityAvailable = true,
        bool providerTermsAllowed = true)
    {
        ProviderId = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
        if (requestedAccountId == Guid.Empty)
        {
            throw new ArgumentException("A requested account ID cannot be empty.", nameof(requestedAccountId));
        }

        if (expectedAccountRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedAccountRevision));
        }

        var qualities = (availableQualities ?? [])
            .OrderBy(item => item)
            .ToArray();
        if (qualities.Any(item => !Enum.IsDefined(item)) || qualities.Distinct().Count() != qualities.Length)
        {
            throw new ArgumentException("Available qualities must be valid and unique.", nameof(availableQualities));
        }

        CapabilityEnabled = capabilityEnabled;
        RequestedAccountId = requestedAccountId;
        ExpectedAccountRevision = expectedAccountRevision;
        AvailableQualities = Array.AsReadOnly(qualities);
        TrackCatalog = trackCatalog == null
            ? null
            : ProviderContractValidation.Catalog(trackCatalog, nameof(trackCatalog));
        IsExplicit = isExplicit;
        RateLimitBudgetAvailable = rateLimitBudgetAvailable;
        StorageCapacityAvailable = storageCapacityAvailable;
        ProviderTermsAllowed = providerTermsAllowed;
    }

    public string ProviderId { get; }

    public bool CapabilityEnabled { get; }

    public Guid? RequestedAccountId { get; }

    public long? ExpectedAccountRevision { get; }

    public IReadOnlyList<ProviderAudioQuality> AvailableQualities { get; }

    public string? TrackCatalog { get; }

    public bool? IsExplicit { get; }

    public bool RateLimitBudgetAvailable { get; }

    public bool StorageCapacityAvailable { get; }

    public bool ProviderTermsAllowed { get; }
}

public sealed record ProviderRouteRequest
{
    public ProviderRouteRequest(
        ProviderCapabilityKind capability,
        ProviderActorContext actor,
        ProviderExecutionPolicy policy,
        string operationId,
        string correlationId,
        DateTimeOffset deadline,
        IEnumerable<string> providerPriority,
        IEnumerable<ProviderRouteProviderState>? providerStates = null,
        ProviderLibraryContext? library = null,
        ProviderExternalResourceId? sourceTrackId = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(capability))
        {
            throw new ArgumentOutOfRangeException(nameof(capability));
        }

        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(policy);
        if (deadline == default)
        {
            throw new ArgumentException("A route deadline is required.", nameof(deadline));
        }

        var priority = providerPriority
            .Select(item => ProviderContractValidation.ProviderId(item, nameof(providerPriority)))
            .ToArray();
        if (priority.Distinct(StringComparer.Ordinal).Count() != priority.Length)
        {
            throw new ArgumentException("Provider priority cannot contain duplicates.", nameof(providerPriority));
        }

        var states = (providerStates ?? [])
            .ToDictionary(item => item.ProviderId, StringComparer.Ordinal);
        if (sourceTrackId != null && sourceTrackId.ResourceKind != ProviderResourceKind.Track)
        {
            throw new ArgumentException("Route identity fallback accepts only track IDs.", nameof(sourceTrackId));
        }

        Capability = capability;
        Actor = actor;
        Policy = policy;
        OperationId = ProviderContractValidation.RequiredText(operationId, nameof(operationId), 100);
        CorrelationId = ProviderContractValidation.RequiredText(correlationId, nameof(correlationId), 100);
        Deadline = deadline;
        ProviderPriority = Array.AsReadOnly(priority);
        ProviderStates = states;
        Library = library;
        SourceTrackId = sourceTrackId;
        IdempotencyKey = ProviderContractValidation.OptionalText(idempotencyKey, nameof(idempotencyKey), 300);
        CancellationToken = cancellationToken;
    }

    public ProviderCapabilityKind Capability { get; }

    public ProviderActorContext Actor { get; }

    public ProviderExecutionPolicy Policy { get; }

    public string OperationId { get; }

    public string CorrelationId { get; }

    public DateTimeOffset Deadline { get; }

    public IReadOnlyList<string> ProviderPriority { get; }

    public IReadOnlyDictionary<string, ProviderRouteProviderState> ProviderStates { get; }

    public ProviderLibraryContext? Library { get; }

    public ProviderExternalResourceId? SourceTrackId { get; }

    public string? IdempotencyKey { get; }

    public CancellationToken CancellationToken { get; }
}

public enum ProviderRouteDecisionStatus
{
    Accepted,
    Rejected
}

public sealed record ProviderRouteCandidateDecision(
    string ProviderId,
    Guid? ProviderAccountId,
    ProviderRouteDecisionStatus Status,
    string ReasonCode,
    int Priority);

public sealed record ProviderRouteDecisionRecord(
    string CorrelationId,
    ProviderCapabilityKind Capability,
    string? SelectedProviderId,
    Guid? SelectedProviderAccountId,
    IReadOnlyList<ProviderRouteCandidateDecision> Candidates);

public sealed record ProviderRouteCandidate<TCapability>(
    int Priority,
    ProviderDescriptor Provider,
    ProviderCapabilityDescriptor Descriptor,
    TCapability Implementation,
    ProviderExecutionContext Context,
    ProviderExternalResourceId? TrackId)
    where TCapability : class, IProviderCapability;

public sealed record ProviderRoutePlan<TCapability>(
    ProviderRouteRequest Request,
    IReadOnlyList<ProviderRouteCandidate<TCapability>> Candidates,
    ProviderRouteDecisionRecord Decision)
    where TCapability : class, IProviderCapability;

public enum ProviderFallbackDisposition
{
    Advance,
    StopPolicy,
    StopFailure,
    Exhausted
}

public sealed record ProviderFallbackDecision<TCapability>(
    ProviderFallbackDisposition Disposition,
    string ReasonCode,
    ProviderRouteCandidate<TCapability>? NextCandidate)
    where TCapability : class, IProviderCapability;
