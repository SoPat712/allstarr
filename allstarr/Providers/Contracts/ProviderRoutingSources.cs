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
    ProviderAccountResolver resolver) : IProviderRouteAccountResolver
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
            request.Actor.TenantId,
            userId.Value,
            backend?.BackendType ?? "system-job",
            backend?.BackendInstanceId ?? "durable-job",
            backend?.PrincipalId ?? request.Actor.DurableJobId?.ToString("N") ?? "system",
            "Provider route actor",
            request.Actor.Kind == ProviderActorKind.Administrator);
        var resolved = await resolver.ResolveAsync(
            new ProviderAccountResolutionRequest(
                principal,
                request.ProviderId,
                CapabilityName(request.Capability),
                request.RequestedAccountId,
                request.LibraryScopeId),
            cancellationToken);
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
                account.TenantId,
                account.OwnerUserId,
                account.LibraryScopeId,
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
