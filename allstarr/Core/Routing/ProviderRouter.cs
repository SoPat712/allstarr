using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;

namespace allstarr.Core.Routing;

public interface IProviderRouter
{
    Task<ProviderRoutePlan<TCapability>> PlanAsync<TCapability>(ProviderRouteRequest request)
        where TCapability : class, IProviderCapability;

    ProviderFallbackDecision<TCapability> EvaluateFallback<TCapability>(
        ProviderRoutePlan<TCapability> plan,
        int failedCandidateIndex,
        ProviderError error)
        where TCapability : class, IProviderCapability;
}

public sealed class ProviderRouter(
    IProviderRegistry registry,
    IProviderRouteAccountResolver accounts,
    IProviderRouteHealthSource health,
    IProviderRouteSidecarSource sidecars,
    ITrackIdentityService identities) : IProviderRouter
{
    private static readonly IReadOnlyDictionary<ProviderCapabilityKind, Type> CapabilityContracts =
        new Dictionary<ProviderCapabilityKind, Type>
        {
            [ProviderCapabilityKind.Metadata] = typeof(IProviderMetadataCapability),
            [ProviderCapabilityKind.Streaming] = typeof(IProviderStreamingCapability),
            [ProviderCapabilityKind.Download] = typeof(IProviderDownloadCapability),
            [ProviderCapabilityKind.Playlist] = typeof(IProviderPlaylistCapability),
            [ProviderCapabilityKind.Lyrics] = typeof(IProviderLyricsCapability),
            [ProviderCapabilityKind.Health] = typeof(IProviderHealthProbeCapability)
        };

    private static readonly IReadOnlySet<ProviderErrorKind> AllowedFallbackFailures =
        new HashSet<ProviderErrorKind>
        {
            ProviderErrorKind.NotFound,
            ProviderErrorKind.NotSupported,
            ProviderErrorKind.CapabilityUnavailable,
            ProviderErrorKind.RateLimited,
            ProviderErrorKind.IncompatibleMedia,
            ProviderErrorKind.TransientFailure
        };

    public async Task<ProviderRoutePlan<TCapability>> PlanAsync<TCapability>(ProviderRouteRequest request)
        where TCapability : class, IProviderCapability
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireTypedContract<TCapability>(request.Capability);
        request.CancellationToken.ThrowIfCancellationRequested();
        if (request.Deadline <= DateTimeOffset.UtcNow)
        {
            throw new TimeoutException("The provider route deadline has expired.");
        }

        var orderedProviders = registry
            .FindByCapability(request.Capability, includeNonOperational: true)
            .OrderBy(provider => ExplicitPriority(request, provider.Id))
            .ThenBy(provider => Priority(request, provider.Id))
            .ThenBy(provider => provider.Id, StringComparer.Ordinal)
            .ToArray();
        var decisions = new List<ProviderRouteCandidateDecision>(orderedProviders.Length);
        var candidates = new List<ProviderRouteCandidate<TCapability>>(orderedProviders.Length);
        ProviderExecutionContext? sourceContext = null;

        foreach (var provider in orderedProviders)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var priority = Priority(request, provider.Id);
            var descriptor = provider.Capabilities.Single(item => item.Capability == request.Capability);
            var state = request.ProviderStates.GetValueOrDefault(provider.Id) ??
                        new ProviderRouteProviderState(provider.Id);
            ProviderAccountContext? account = null;

            void Reject(string reason) => decisions.Add(new ProviderRouteCandidateDecision(
                provider.Id,
                account?.AccountId,
                ProviderRouteDecisionStatus.Rejected,
                reason,
                priority));

            if (!request.Policy.AllowsProvider(provider.Id))
            {
                Reject("provider-not-allowed");
                continue;
            }

            if (!state.CapabilityEnabled)
            {
                Reject("capability-disabled");
                continue;
            }

            if (!state.ProviderTermsAllowed)
            {
                Reject("provider-terms-denied");
                continue;
            }

            if (!state.RateLimitBudgetAvailable)
            {
                Reject("rate-limit-budget-exhausted");
                continue;
            }

            if (request.Policy.ExplicitContent == ProviderExplicitContentPolicy.CleanOnly &&
                state.IsExplicit != false)
            {
                Reject(state.IsExplicit == true ? "explicit-content-denied" : "explicit-state-unknown");
                continue;
            }

            if (!descriptor.HasUsableImplementation ||
                !registry.TryGetCapability<TCapability>(provider.Id, request.Capability, out var implementation))
            {
                Reject("capability-unavailable");
                continue;
            }

            ProviderRouteAccountResolution? resolvedAccount;
            try
            {
                resolvedAccount = descriptor.AccountRequirement == ProviderAccountRequirement.None
                    ? null
                    : await accounts.ResolveAsync(
                        new ProviderRouteAccountRequest(
                            request.Actor,
                            provider.Id,
                            request.Capability,
                            state.RequestedAccountId,
                            request.Library?.ScopeId),
                        request.CancellationToken);
            }
            catch (UnauthorizedAccessException)
            {
                Reject("account-not-authorized");
                continue;
            }

            account = resolvedAccount?.Account;
            if (descriptor.AccountRequirement == ProviderAccountRequirement.Required && account == null)
            {
                Reject("account-required");
                continue;
            }

            if (account != null &&
                (!account.Enabled ||
                 resolvedAccount!.CurrentRevision != account.Revision ||
                 state.ExpectedAccountRevision.HasValue &&
                 state.ExpectedAccountRevision != resolvedAccount.CurrentRevision))
            {
                Reject(!account.Enabled ? "account-disabled" : "account-stale");
                continue;
            }

            if (account != null && !descriptor.AllowedAccountScopes.Contains(account.Scope))
            {
                Reject("account-scope-denied");
                continue;
            }

            if (account != null)
            {
                var snapshot = health.Get(provider.Id, account.AccountId, request.Capability);
                if (snapshot.CircuitOpen)
                {
                    Reject("circuit-open");
                    continue;
                }

                if (snapshot.State is ProviderRouteHealthState.Unavailable or
                    ProviderRouteHealthState.Unauthorized)
                {
                    Reject(snapshot.State == ProviderRouteHealthState.Unauthorized
                        ? "health-unauthorized"
                        : "health-unavailable");
                    continue;
                }
            }

            if (descriptor.SidecarDependency != null && !sidecars.IsReady(descriptor.SidecarDependency))
            {
                Reject("sidecar-not-ready");
                continue;
            }

            if (request.Capability == ProviderCapabilityKind.Download &&
                !request.Policy.AllowManagedDownloads)
            {
                Reject("managed-download-denied");
                continue;
            }

            if (request.Capability == ProviderCapabilityKind.Download &&
                !state.StorageCapacityAvailable)
            {
                Reject("storage-capacity-unavailable");
                continue;
            }

            if (request.Capability == ProviderCapabilityKind.Download &&
                request.IdempotencyKey == null)
            {
                Reject("idempotency-key-required");
                continue;
            }

            if (request.Capability is ProviderCapabilityKind.Streaming or ProviderCapabilityKind.Download &&
                !HasPolicyQuality(state.AvailableQualities, request.Policy.Quality))
            {
                Reject("quality-policy-denied");
                continue;
            }

            ProviderExecutionContext context;
            try
            {
                context = CreateContext(request, provider.Id, account);
            }
            catch (UnauthorizedAccessException)
            {
                Reject("execution-policy-denied");
                continue;
            }

            ProviderExternalResourceId? targetTrack = null;
            if (request.SourceTrackId != null)
            {
                if (request.SourceTrackId.ProviderId.Equals(provider.Id, StringComparison.Ordinal))
                {
                    targetTrack = request.SourceTrackId;
                    sourceContext = context;
                }
                else
                {
                    sourceContext ??= await CreateSourceContextAsync(request);
                    if (sourceContext == null)
                    {
                        Reject("source-account-unresolved");
                        continue;
                    }

                    var translation = await identities.TranslateAsync(
                        sourceContext,
                        request.SourceTrackId,
                        context,
                        new ProviderTrackIdentityTarget(
                            provider.Id,
                            ProviderResourceKind.Track,
                            state.TrackCatalog),
                        request.CancellationToken);
                    if (translation.Status != TrackIdentityTranslationStatus.Translated ||
                        translation.Target?.Verification is not (
                            ProviderIdentityVerification.Verified or ProviderIdentityVerification.Pinned))
                    {
                        Reject("verified-identity-required");
                        continue;
                    }

                    targetTrack = translation.Target.ExternalId;
                }
            }

            decisions.Add(new ProviderRouteCandidateDecision(
                provider.Id,
                account?.AccountId,
                ProviderRouteDecisionStatus.Accepted,
                candidates.Count == 0 ? "selected" : "eligible-fallback",
                priority));
            candidates.Add(new ProviderRouteCandidate<TCapability>(
                priority,
                provider,
                descriptor,
                implementation!,
                context,
                targetTrack));
        }

        var selected = candidates.FirstOrDefault();
        var decision = new ProviderRouteDecisionRecord(
            request.CorrelationId,
            request.Capability,
            selected?.Provider.Id,
            selected?.Context.Account?.AccountId,
            decisions.AsReadOnly());
        return new ProviderRoutePlan<TCapability>(request, candidates.AsReadOnly(), decision);
    }

    public ProviderFallbackDecision<TCapability> EvaluateFallback<TCapability>(
        ProviderRoutePlan<TCapability> plan,
        int failedCandidateIndex,
        ProviderError error)
        where TCapability : class, IProviderCapability
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(error);
        if (failedCandidateIndex < 0 || failedCandidateIndex >= plan.Candidates.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(failedCandidateIndex));
        }

        if (!plan.Request.Policy.AllowFallback)
        {
            return new ProviderFallbackDecision<TCapability>(
                ProviderFallbackDisposition.StopPolicy,
                "fallback-policy-denied",
                null);
        }

        if (!AllowedFallbackFailures.Contains(error.Kind))
        {
            return new ProviderFallbackDecision<TCapability>(
                ProviderFallbackDisposition.StopFailure,
                $"failure-{error.Code}",
                null);
        }

        var nextIndex = failedCandidateIndex + 1;
        if (nextIndex >= plan.Candidates.Count)
        {
            return new ProviderFallbackDecision<TCapability>(
                ProviderFallbackDisposition.Exhausted,
                "fallback-exhausted",
                null);
        }

        return new ProviderFallbackDecision<TCapability>(
            ProviderFallbackDisposition.Advance,
            $"fallback-{error.Code}",
            plan.Candidates[nextIndex]);
    }

    private async Task<ProviderExecutionContext?> CreateSourceContextAsync(ProviderRouteRequest request)
    {
        var source = request.SourceTrackId!;
        var state = request.ProviderStates.GetValueOrDefault(source.ProviderId) ??
                    new ProviderRouteProviderState(source.ProviderId);
        ProviderRouteAccountResolution? resolution;
        try
        {
            resolution = await accounts.ResolveAsync(
                new ProviderRouteAccountRequest(
                    request.Actor,
                    source.ProviderId,
                    request.Capability,
                    state.RequestedAccountId,
                    request.Library?.ScopeId),
                request.CancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (resolution != null &&
            (!resolution.Account.Enabled ||
             resolution.Account.Revision != resolution.CurrentRevision ||
             state.ExpectedAccountRevision.HasValue &&
             state.ExpectedAccountRevision != resolution.CurrentRevision))
        {
            return null;
        }

        try
        {
            return CreateContext(request, source.ProviderId, resolution?.Account);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ProviderExecutionContext CreateContext(
        ProviderRouteRequest request,
        string providerId,
        ProviderAccountContext? account) => new(
        request.Actor,
        providerId,
        account,
        request.Library,
        request.Policy,
        request.OperationId,
        request.CorrelationId,
        request.Deadline,
        request.CancellationToken,
        request.IdempotencyKey);

    private static bool HasPolicyQuality(
        IReadOnlyList<ProviderAudioQuality> available,
        ProviderQualityPolicy policy) =>
        available.Any(item => item >= policy.Minimum && item <= policy.Maximum);

    private static int Priority(ProviderRouteRequest request, string providerId)
    {
        for (var index = 0; index < request.ProviderPriority.Count; index++)
        {
            if (request.ProviderPriority[index].Equals(providerId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static int ExplicitPriority(ProviderRouteRequest request, string providerId)
    {
        if (request.Policy.ExplicitContent != ProviderExplicitContentPolicy.PreferClean) return 0;
        var state = request.ProviderStates.GetValueOrDefault(providerId);
        return state?.IsExplicit switch { false => 0, null => 1, true => 2 };
    }

    private static void RequireTypedContract<TCapability>(ProviderCapabilityKind capability)
        where TCapability : class, IProviderCapability
    {
        if (typeof(TCapability) != CapabilityContracts[capability])
        {
            throw new ArgumentException(
                $"Capability '{capability}' must be planned through '{CapabilityContracts[capability].Name}'.");
        }
    }
}
