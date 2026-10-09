using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using DurableHealthState = allstarr.Core.Storage.ProviderHealthState;
using RuntimeHealthState = allstarr.Services.Common.ProviderHealthState;

namespace allstarr.Core.Routing;

public sealed class DurableProviderRouteAccountResolver(
    ProviderAccountResolver resolver,
    IProviderRouteHealthSource health) : IProviderRouteAccountResolver
{
    public async Task<ProviderRouteAccountResolution?> ResolveAsync(
        ProviderRouteAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = request.Actor.EffectiveUserId;
        if (!userId.HasValue)
        {
            return null;
        }

        var backend = request.Actor.BackendPrincipal;
        var principal = new AllstarrPrincipal(
            userId.Value,
            backend?.BackendType ?? "system-job",
            backend?.BackendInstanceId ?? "durable-job",
            backend?.PrincipalId ?? request.Actor.DurableJobId?.ToString("N") ?? "system",
            "Provider route actor",
            request.Actor.Kind == ProviderActorKind.Administrator);
        var candidates = await resolver.ResolveCandidatesAsync(
            new ProviderAccountResolutionRequest(
                principal,
                request.ProviderId,
                CapabilityName(request.Capability),
                request.RequestedAccountId,
                request.AllowedScopes,
                request.AllowSharedAccount),
            cancellationToken);
        var resolved = request.RequestedAccountId.HasValue
            ? candidates.FirstOrDefault()
            : candidates.FirstOrDefault(candidate =>
            {
                var current = health.Get(request.ProviderId, candidate.Account.Id, request.Capability);
                return !current.CircuitOpen && current.State is
                    ProviderRouteHealthState.Healthy or ProviderRouteHealthState.Unknown;
            }) ?? candidates.FirstOrDefault();
        if (resolved == null)
        {
            return null;
        }

        var account = resolved.Account;
        return new ProviderRouteAccountResolution(
            new ProviderAccountContext(
                account.Id,
                account.ProviderId,
                account.Scope,
                account.Revision,
                account.Enabled,
                account.OwnerUserId,
                resolved.Reason.Replace('_', '-'),
                account.SecretReferenceId),
            account.Revision);
    }

    private static string CapabilityName(ProviderCapabilityKind capability) =>
        capability.ToString().ToLowerInvariant();
}

public sealed class DurableProviderRouteHealthSource(
    DurableProviderHealthStore healthStore,
    ProviderStatusManager runtimeStatus) : IProviderRouteHealthSource
{
    public ProviderRouteHealthSnapshot Get(
        string providerId,
        Guid? providerAccountId,
        ProviderCapabilityKind capability)
    {
        var capabilityName = capability.ToString().ToLowerInvariant();
        if (!providerAccountId.HasValue)
        {
            var status = runtimeStatus.GetAccountFreeStatus(providerId, capabilityName);
            return new ProviderRouteHealthSnapshot(status.Health switch
            {
                RuntimeHealthState.Healthy => ProviderRouteHealthState.Healthy,
                RuntimeHealthState.Degraded => ProviderRouteHealthState.Degraded,
                _ => ProviderRouteHealthState.Unknown
            }, CircuitOpen: false);
        }

        var accountId = providerAccountId.Value;
        var circuitOpen = healthStore.IsCircuitOpen(accountId, capabilityName);
        if (!healthStore.TryGetLatest(
                providerId,
                accountId,
                capabilityName,
                out var latest))
        {
            return new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Unknown, circuitOpen);
        }

        var state = latest.State switch
        {
            DurableHealthState.Healthy => ProviderRouteHealthState.Healthy,
            DurableHealthState.Degraded => ProviderRouteHealthState.Degraded,
            DurableHealthState.Unavailable => ProviderRouteHealthState.Unavailable,
            DurableHealthState.Unauthorized => ProviderRouteHealthState.Unauthorized,
            _ => ProviderRouteHealthState.Unknown
        };
        return new ProviderRouteHealthSnapshot(state, circuitOpen);
    }
}

public sealed class DurableProviderRouteSidecarSource(
    SidecarStatusCatalog sidecars) : IProviderRouteSidecarSource
{
    public bool IsReady(string dependencyId) =>
        sidecars.TryGet(dependencyId, out var status) &&
        status.State == SidecarRuntimeState.Ready;
}
